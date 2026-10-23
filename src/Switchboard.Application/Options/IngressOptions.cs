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
}

public sealed class IngressOptionsValidator : IValidateOptions<IngressOptions>
{
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

        return OptionsValidation.Result(failures);
    }
}
