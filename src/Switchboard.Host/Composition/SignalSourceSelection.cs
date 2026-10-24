using System.Globalization;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Host.Composition;

public enum SignalSourceKind
{
    HomeAssistant,
    Replay,
}

/// <summary>
/// Where signals come from: Home Assistant, or (standalone development only) a recorded history
/// replayed on its own clock. Replay never runs as an add-on: an add-on holds a Supervisor token and
/// protects a real installation, so a replay setting there is a mistake, not a mode.
/// </summary>
public sealed record SignalSourceSelection(SignalSourceKind Kind, string? ReplayPath, double ReplaySpeed)
{
    // A development limit, not a control parameter: beyond this a timer tick covers minutes of data.
    private const double MaxReplaySpeed = 1000;

    public bool IsReplay => Kind == SignalSourceKind.Replay;

    public static SignalSourceSelection Resolve(RuntimeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.DevReplayPath is null)
        {
            return new SignalSourceSelection(SignalSourceKind.HomeAssistant, null, 1);
        }

        if (environment.IsAddon)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableNames.DevReplay} is for standalone development only; refusing to start as an add-on with it set.");
        }

        var speed = 1.0;
        if (!string.IsNullOrWhiteSpace(environment.DevReplaySpeed)
            && (!double.TryParse(environment.DevReplaySpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out speed)
                || !(speed > 0 && speed <= MaxReplaySpeed)))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableNames.DevReplaySpeed} must be a number greater than 0 and at most {MaxReplaySpeed}.");
        }

        return new SignalSourceSelection(SignalSourceKind.Replay, environment.DevReplayPath, speed);
    }
}
