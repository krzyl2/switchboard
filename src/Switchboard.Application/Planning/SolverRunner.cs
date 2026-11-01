using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>What one solve came to: the plan when it computed and verified, the report either way, and for a failure the exception or the limit it ran into.</summary>
internal sealed record SolveOutcome(PlanSnapshot? Plan, SolveReport Report, Exception? Exception = null, TimeSpan? Limit = null)
{
    /// <summary>A solve that produced no plan (also an input that could not be built, which never reached a solver).</summary>
    public static SolveOutcome Failure(DateTimeOffset now, TimeSpan duration, SolveResult result, string reason, ImmutableArray<PlanViolation> violations, Exception? exception, TimeSpan? limit = null) =>
        new(null, new SolveReport(now, duration, result, reason, violations), exception, limit);
}

/// <summary>
/// Runs the solver off the loop with a time limit and verifies what it returns (15-PLANNER, "Failure"): a solver that throws, exceeds
/// <c>planner.max_solve_time</c> or returns a plan the <see cref="PlanVerifier"/> refuses gives no plan and says why. It changes nothing in the service and
/// publishes nothing; the caller decides what a failure does.
/// </summary>
internal sealed class SolverRunner(Func<PlanInput, CancellationToken, PlanSnapshot> solver, TimeProvider time, ILogger logger)
{
    public async Task<SolveOutcome> SolveAsync(PlanInput input, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        using var solving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = Task.Run(() => solver(input, solving.Token), CancellationToken.None);
        var timeout = Task.Delay(input.Parameters.MaxSolveTime, time, waiting.Token);
        Task finished;
        try
        {
            finished = await Task.WhenAny(task, timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await solving.CancelAsync().ConfigureAwait(false);
            throw;
        }

        if (finished != task)
        {
            // Over the time allowed: the cooperative solver stops at its next check, an uncooperative one is abandoned and its result never read.
            await solving.CancelAsync().ConfigureAwait(false);
            _ = task.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return SolveOutcome.Failure(now, time.GetElapsedTime(started), SolveResult.Timeout, reason, [], null, input.Parameters.MaxSolveTime);
        }

        await waiting.CancelAsync().ConfigureAwait(false);
        PlanSnapshot plan;
        try
        {
            plan = await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SolveOutcome.Failure(now, time.GetElapsedTime(started), SolveResult.Failed, reason, [], ex);
        }

        ImmutableArray<PlanViolation> violations;
        try
        {
            violations = PlanVerifier.Verify(input, plan);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SolveOutcome.Failure(now, time.GetElapsedTime(started), SolveResult.Failed, reason, [], ex);
        }

        if (!violations.IsEmpty)
        {
            return SolveOutcome.Failure(now, time.GetElapsedTime(started), SolveResult.Invalid, reason, violations, null);
        }

        return new SolveOutcome(plan, new SolveReport(now, time.GetElapsedTime(started), SolveResult.Computed, reason, []));
    }

    public void LogComputed(SolveOutcome outcome)
    {
        var plan = outcome.Plan!;
        logger.LogInformation(
            "plan_computed: plan {PlanId} for {Slots} slots and {Devices} devices, planned cost {Cost:0.##}, solved in {Milliseconds:0.0} ms ({Reason}).",
            plan.Id,
            plan.Slots.Length,
            plan.Devices.Length,
            plan.Cost.Planned,
            outcome.Report.Duration.TotalMilliseconds,
            outcome.Report.Reason);
    }

    /// <param name="planHeld">A plan is still in force (it stays until it ages out).</param>
    public void LogFailure(SolveOutcome outcome, bool planHeld)
    {
        var report = outcome.Report;
        var kept = planHeld ? "the last plan stays until it ages out" : "no plan is held";
        switch (report.Result)
        {
            case SolveResult.Timeout:
                logger.LogWarning("plan_failed: the solver did not finish within {Limit} ({Reason}); {Kept}.", outcome.Limit, report.Reason, kept);
                break;
            case SolveResult.Invalid:
                logger.LogWarning(
                    "plan_failed: the verifier refused the plan ({Violations}) ({Reason}); {Kept}.",
                    string.Join(", ", report.Violations.Select(v => v.Code).Distinct()),
                    report.Reason,
                    kept);
                break;
            default:
                logger.LogWarning(outcome.Exception, "plan_failed: the plan could not be computed ({Reason}); {Kept}.", report.Reason, kept);
                break;
        }
    }
}
