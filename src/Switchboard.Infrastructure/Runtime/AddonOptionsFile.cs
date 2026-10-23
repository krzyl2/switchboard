namespace Switchboard.Infrastructure.Runtime;

public static class AddonOptionsFile
{
    /// <summary>
    /// Returns the file contents, or null when it is missing or unreadable. A null here resolves
    /// to shadow mode, so an unreadable file can never be mistaken for an "active" setting.
    /// </summary>
    public static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
