using System.Net;
using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>Bound from <c>Switchboard:Ingress</c>. Values live in appsettings.json, never in code.</summary>
public sealed class IngressOptions
{
    public const string SectionName = "Switchboard:Ingress";

    /// <summary>The only port the process listens on; must equal ingress_port in config.yaml.</summary>
    public required int Port { get; init; }

    /// <summary>Address the Supervisor ingress proxy connects from; the only remote peer allowed.</summary>
    public required string TrustedProxy { get; init; }

    /// <summary>
    /// The largest request body any endpoint accepts: sized for the largest legitimate one, the setup
    /// wizard's draft of a configuration file at its size limit. An endpoint may hold a smaller limit of its own.
    /// </summary>
    public required long MaxRequestBodyBytes { get; init; }
}

public sealed class IngressOptionsValidator : IValidateOptions<IngressOptions>
{
    private const long MinRequestBodyBytes = 64 * 1024;
    private const long MaxRequestBodyBytes = 16 * 1024 * 1024;

    public ValidateOptionsResult Validate(string? name, IngressOptions options)
    {
        var failures = new List<string>();
        if (options.Port <= IPEndPoint.MinPort || options.Port > IPEndPoint.MaxPort)
        {
            failures.Add($"{IngressOptions.SectionName}:Port must be between 1 and {IPEndPoint.MaxPort}.");
        }

        if (!IPAddress.TryParse(options.TrustedProxy, out _))
        {
            failures.Add($"{IngressOptions.SectionName}:TrustedProxy must be an IP address.");
        }

        if (options.MaxRequestBodyBytes < MinRequestBodyBytes || options.MaxRequestBodyBytes > MaxRequestBodyBytes)
        {
            failures.Add($"{IngressOptions.SectionName}:MaxRequestBodyBytes must be between {MinRequestBodyBytes} and {MaxRequestBodyBytes}.");
        }

        return OptionsValidation.Result(failures);
    }
}
