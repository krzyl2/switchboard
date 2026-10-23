using System.Net;
using Switchboard.Application.Options;

namespace Switchboard.Host.Web;

/// <summary>
/// Only the Supervisor ingress proxy (and loopback, for the s6 watchdog and local checks) may talk
/// to the add-on. Home Assistant authenticates the user before the proxy forwards anything, so a
/// request from any other address is a peer that skipped that authentication.
/// </summary>
public sealed class IngressAccessPolicy
{
    private readonly IPAddress _trustedProxy;

    public IngressAccessPolicy(IngressOptions options, bool trustAllRequests)
    {
        ArgumentNullException.ThrowIfNull(options);
        _trustedProxy = Normalize(IPAddress.Parse(options.TrustedProxy));
        TrustAllRequests = trustAllRequests;
    }

    /// <summary>Dev compose only (SWITCHBOARD_DEV_TRUST_ALL_REQUESTS); logged as a warning at startup.</summary>
    public bool TrustAllRequests { get; }

    public bool IsAllowed(IPAddress? remote)
    {
        if (TrustAllRequests)
        {
            return true;
        }

        if (remote is null)
        {
            return false;
        }

        var address = Normalize(remote);
        return IPAddress.IsLoopback(address) || address.Equals(_trustedProxy);
    }

    // A dual-stack socket reports IPv4 peers as ::ffff:a.b.c.d; compare them as plain IPv4.
    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
