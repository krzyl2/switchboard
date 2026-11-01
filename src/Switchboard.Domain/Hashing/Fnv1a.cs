using System.Text;

namespace Switchboard.Domain.Hashing;

/// <summary>
/// FNV-1a, 64 bit, over the UTF-8 bytes of a text: the one fingerprint of goals, devices, plans and forecasts. It is stored (plan entries carry it) and compared
/// across runs, so the constants and the byte order never change; it is a change detector, not a security hash.
/// </summary>
public static class Fnv1a
{
    private const ulong Offset = 14695981039346656037;
    private const ulong Prime = 1099511628211;

    public static ulong Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = Offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * Prime;
        }

        return hash;
    }

    /// <summary>The same hash as a signed number (the form revisions and fingerprints are stored and compared in).</summary>
    public static long Signed(string text) => unchecked((long)Hash(text));
}
