namespace Switchboard.Application;

/// <summary>The release number shown in the UI, the status API and the MQTT device block.</summary>
public sealed record AppVersion(string Value)
{
    private const string Unknown = "unknown";

    /// <summary>
    /// Strips build metadata (<c>+commit</c>) so users see the number they installed, not a hash.
    /// </summary>
    public static AppVersion FromInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new AppVersion(Unknown);
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var plain = plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        return new AppVersion(plain.Length == 0 ? Unknown : plain);
    }

    public override string ToString() => Value;
}
