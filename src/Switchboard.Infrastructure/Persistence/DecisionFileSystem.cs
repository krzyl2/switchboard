using System.Text;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>The file operations the decision log needs, narrow enough for a test double that fails like a full disk.</summary>
public interface IDecisionFileSystem
{
    void CreateDirectory(string path);

    /// <summary>File names (not paths) in <paramref name="directory"/>; empty when it does not exist.</summary>
    IReadOnlyList<string> List(string directory);

    /// <summary>Appends <paramref name="text"/> (UTF-8), creating the file if needed, and flushes it to disk.</summary>
    void Append(string path, string text);

    IEnumerable<string> ReadLines(string path);

    /// <summary>True when the file is missing, empty, or its last byte is a line feed.</summary>
    bool EndsWithNewline(string path);

    void Delete(string path);
}

public sealed class DecisionFileSystem : IDecisionFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IReadOnlyList<string> List(string directory) =>
        Directory.Exists(directory) ? [.. Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>()] : [];

    public void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Flush(flushToDisk: true);
    }

    public IEnumerable<string> ReadLines(string path) => File.ReadLines(path, Encoding.UTF8);

    public bool EndsWithNewline(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return true;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    public void Delete(string path) => File.Delete(path);
}
