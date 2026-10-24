using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Persistence;

namespace Switchboard.Tests.Support;

/// <summary>Decisions with every input filled, for the decision log and the API.</summary>
internal static class TestDecisions
{
    private static long _sequence;

    public static Decision Shed(DateTimeOffset at, string device = "heater_l1", Phase phase = Phase.L1, ReasonCode reason = ReasonCode.GuardTierExceeded) =>
        new(
            $"test-{Interlocked.Increment(ref _sequence):D9}",
            at,
            DecisionScope.Device,
            DeviceName.Parse(device),
            null,
            phase,
            DecisionAction.SwitchOff,
            reason,
            ReasonCode.LockoutMinOn,
            OperatingMode.Shadow,
            false,
            ReasonCode.ModeShadow,
            new DecisionInputs(
                new PhaseDecisionInputs(phase, true, null, new Amperes(33), new Amperes(26.4), 1.32, 4, TierReaction.ShedOneImmediately, 0.97, new Amperes(28.25), new Amperes(14.13), new Amperes(-18.87), FlowDirection.Import, at - TimeSpan.FromSeconds(1)),
                new DeviceDecisionInputs(new RelayApplied(true), true, null, 1, 0.35, null, TimeSpan.FromSeconds(42), TimeSpan.Zero, true),
                null,
                null,
                null));

    public static Decision Event(DateTimeOffset at, DecisionAction action = DecisionAction.SafeStateEntered, ReasonCode reason = ReasonCode.SafeState) =>
        new($"test-{Interlocked.Increment(ref _sequence):D9}", at, DecisionScope.System, null, null, null, action, reason, null, OperatingMode.Shadow, false, null, DecisionInputs.None);

    public static Decision Mode(DateTimeOffset at) =>
        new(
            $"test-{Interlocked.Increment(ref _sequence):D9}",
            at,
            DecisionScope.System,
            null,
            null,
            null,
            DecisionAction.ModeResolved,
            ReasonCode.ModeShadow,
            null,
            OperatingMode.Shadow,
            false,
            null,
            DecisionInputs.None with { Mode = new ModeDecisionInputs(OperatingMode.Active, OperatingMode.Shadow, ModeNote.ActiveUnavailableBeforeStage3) });

    public static PersistenceOptions Options(int memory = 1000, int pending = 10000, int maxPage = 500) => new()
    {
        StateSaveInterval = TimeSpan.FromMinutes(1),
        StateBudgetSaveStep = 0.05,
        StateMaxFileBytes = 65536,
        DecisionMemoryCapacity = memory,
        DecisionPendingCapacity = pending,
        DecisionMaxPageSize = maxPage,
        WriteRetryInterval = TimeSpan.FromSeconds(30),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
    };
}

/// <summary>The real file system, which can be told to fail like a full disk, optionally after writing part of the text.</summary>
internal sealed class FlakyFileSystem : IDecisionFileSystem
{
    private readonly DecisionFileSystem _real = new();

    public bool Fail { get; set; }

    /// <summary>When failing, write this many characters of the text first (a write cut off mid-line).</summary>
    public int PartialCharacters { get; set; }

    public int Appends { get; private set; }

    public void CreateDirectory(string path)
    {
        ThrowIfFailing();
        _real.CreateDirectory(path);
    }

    public IReadOnlyList<string> List(string directory) => _real.List(directory);

    public void Append(string path, string text)
    {
        Appends++;
        if (Fail)
        {
            if (PartialCharacters > 0)
            {
                _real.Append(path, text[..Math.Min(PartialCharacters, text.Length)]);
            }

            throw new IOException("No space left on device");
        }

        _real.Append(path, text);
    }

    public IEnumerable<string> ReadLines(string path) => _real.ReadLines(path);

    public bool EndsWithNewline(string path) => _real.EndsWithNewline(path);

    public void Delete(string path)
    {
        ThrowIfFailing();
        _real.Delete(path);
    }

    private void ThrowIfFailing()
    {
        if (Fail && PartialCharacters == 0)
        {
            throw new IOException("No space left on device");
        }
    }
}

/// <summary>A throwaway data folder.</summary>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Decisions => System.IO.Path.Combine(Path, JsonlDecisionLog.DirectoryName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }
}
