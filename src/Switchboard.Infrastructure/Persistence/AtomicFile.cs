using System.Runtime.InteropServices;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// The one way a data file is replaced: the bytes go to a temporary file, are flushed to disk, the
/// temporary file is moved over the target, and the directory is flushed too where the platform
/// allows (without that, a power cut right after the rename can bring back the old name). A reader
/// therefore sees the old file or the new one, never part of either, and a crash mid-write leaves the
/// previous file whole.
/// </summary>
internal static class AtomicFile
{
    public const string TemporarySuffix = ".tmp";

    private const int ReadOnly = 0;

    /// <summary>Writes <paramref name="payload"/> to <c>&lt;path&gt;.tmp</c> (replacing a leftover one) and moves it over <paramref name="path"/>.</summary>
    /// <param name="replace">The move; a test passes one that fails. Defaults to <see cref="Replace"/>.</param>
    public static void Write(string path, ReadOnlySpan<byte> payload, Action<string, string>? replace = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + TemporarySuffix;
        WriteTemporary(temporary, payload, FileMode.Create);
        (replace ?? Replace)(temporary, path);
    }

    /// <summary>Writes and flushes the temporary file; <paramref name="mode"/> is Create (a leftover is replaced) or CreateNew (a name that must be free).</summary>
    public static void WriteTemporary(string temporary, ReadOnlySpan<byte> payload, FileMode mode)
    {
        using var stream = new FileStream(temporary, mode, FileAccess.Write, FileShare.None);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Moves the flushed temporary file over the target, then flushes the directory.</summary>
    public static void Replace(string temporary, string target)
    {
        File.Move(temporary, target, overwrite: true);
        if (Path.GetDirectoryName(Path.GetFullPath(target)) is { } directory)
        {
            SyncDirectory(directory);
        }
    }

    /// <summary>
    /// fsync on the directory; best effort and true when it ran. Windows has no such call and the
    /// file system does not need it; a failure after the rename is not a failed write (the new file is in place).
    /// </summary>
    internal static bool SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var descriptor = open(directory, ReadOnly);
            if (descriptor < 0)
            {
                return false;
            }

            try
            {
                return fsync(descriptor) == 0;
            }
            finally
            {
                _ = close(descriptor);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

#pragma warning disable SYSLIB1054 // Three trivial calls with blittable arguments; the marshalling generator needs unsafe blocks.
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
#pragma warning restore SYSLIB1054
}
