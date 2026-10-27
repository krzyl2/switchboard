using Microsoft.Extensions.Options;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Goals;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.State;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Persistence;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the guard and its two writers (decision log, state.json). Registered last, so it stops
/// first: on stop it evaluates the shutdown safe state (every controlled device off, SAFE-01) and,
/// while commands may leave, sends it before the link closes (bounded by
/// HomeAssistant:ShutdownCommandWait), then writes the decision log and state.json, all within
/// Persistence:ShutdownTimeout plus that wait, while the MQTT and Home Assistant links are still open
/// behind it. A state.json that could not be trusted starts with control locked off and the kill
/// switch on until the user confirms. A guard loop that fails stops the whole add-on: a process that
/// keeps serving a frozen status, with the health entity ON, would look protected while nothing is
/// evaluated; stopping turns the health entity OFF and the Supervisor restarts the add-on.
/// </summary>
internal sealed class GuardService(
    GuardRunner runner,
    RuntimeStateCoordinator state,
    JsonlDecisionLog log,
    KillSwitchState killSwitch,
    ControlSwitchState control,
    DeviceOverrides overrides,
    GoalLedger goals,
    GuardStatusHolder status,
    ModeState mode,
    EffectorGate gate,
    HomeAssistantDeviceCommandPort port,
    PhaseEvidenceRecorder evidence,
    ActivePhaseTestService activeTest,
    IOptions<PersistenceOptions> options,
    IOptions<HomeAssistantOptions> homeAssistant,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<GuardService> logger) : IHostedService
{
    private readonly CancellationTokenSource _guardStop = new();
    private readonly CancellationTokenSource _writersStop = new();
    private Task _guard = Task.CompletedTask;
    private Task _writers = Task.CompletedTask;
    private string? _devices;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Everything persisted is applied before the first evaluation: budgets (credited for the
        // downtime only after a clean stop), the restore baselines, the kill switch, the control
        // switch and the overrides. Lockouts are not persisted: they all start now (SAFE-07).
        log.Start();
        var loaded = state.Loaded;
        state.Attach(runner, killSwitch);
        state.Attach(overrides);
        state.Attach(goals);
        state.Attach(control);
        evidence.Attach(runner);
        activeTest.Attach(runner);
        runner.Restore(loaded.State.GuardMemory);
        gate.Restore(loaded.State.StartBaselines, loaded.State.SelfStarted);
        killSwitch.Restore(loaded.State.KillSwitch, loaded.State.KillSwitchChangedAt);
        if (loaded.Status == StateFileStatus.ResetCorrupt)
        {
            // Maintainer survey 2026-10-25: control off and the kill switch on until confirmed in the UI.
            control.Lock(ControlLockReason.StateFileCorrupt);
        }
        else
        {
            control.Restore(loaded.State.Control);
        }

        overrides.Restore(loaded.State.Overrides);
        goals.Restore(loaded.State.Goals);
        var now = time.GetUtcNow();
        state.BeginObservation(log.OldestDay() is { } oldest && oldest < now ? oldest : now);
        status.Published += OnPublished;

        // The configuration is not read here: the first guard step resolves the effective mode (and
        // records it), so a broken input port fails the guard loop, not the start.
        var current = mode.For(configurationValid: true);
        logger.LogInformation(
            "Guard and dispatch starting (option {Ceiling}, control {Control}; the first step resolves the effective mode); state.json: {State}, previous stop {Stop}",
            current.Ceiling,
            current.ControlAllowed ? "allowed" : "not allowed",
            loaded.Status,
            loaded.State.CleanShutdown ? "clean" : "not clean");

        _guard = runner.RunAsync(_guardStop.Token);
        _ = _guard.ContinueWith(
            t =>
            {
                logger.LogCritical(t.Exception, "The guard loop stopped with an error; protection decisions are no longer evaluated. Stopping the add-on");
                lifetime.StopApplication();
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        _writers = Task.WhenAll(log.RunAsync(_writersStop.Token), state.RunAsync(_writersStop.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var limit = options.Value.ShutdownTimeout + homeAssistant.Value.ShutdownCommandWait;
        using var deadline = new CancellationTokenSource(limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            // The loop first, so the shutdown step runs alone.
            await _guardStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _guard.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already logged when it happened; the shutdown safe state is still recorded.
            }

            try
            {
                await runner.ShutdownAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The same failure as the loop's, most likely; what was recorded still gets written.
                logger.LogCritical(ex, "The shutdown safe state could not be evaluated; writing what was recorded");
            }

            // While commands may leave, the shutdown safe state's offs go out before the link
            // closes; a decision whose command got no answer in time is recorded as not answered.
            await port.WhenIdleAsync(homeAssistant.Value.ShutdownCommandWait, linked.Token).ConfigureAwait(false);
            runner.Drain();
            await log.FlushAsync(linked.Token).ConfigureAwait(false);
            await state.FlushAsync(linked.Token).ConfigureAwait(false);
            logger.LogInformation("Guard stopped: shutdown safe state recorded, decision log and state.json written");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The guard's stop did not complete within {Timeout}; the next start treats this stop as not clean", limit);
        }
        finally
        {
            status.Published -= OnPublished;
            await _writersStop.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            // Both writers end at their next await; one stuck in a write is left to the process exit.
            await _writers.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            logger.LogWarning("A persistence writer was still busy when the guard stopped");
        }
    }

    private void OnPublished(object? sender, GuardStatus published)
    {
        // The device list changes only with a valid configuration; worth one line each time.
        var devices = string.Join(", ", published.Devices.Select(d => d.Id.Value).Order(StringComparer.Ordinal));
        if (published.Breaker is null || devices == _devices)
        {
            return;
        }

        _devices = devices;
        logger.LogInformation(
            "Guard watches {Count} declared device(s): {Devices}; effective mode {Mode}",
            published.Devices.Length,
            devices.Length == 0 ? "none" : devices,
            published.Mode.Effective);
    }
}
