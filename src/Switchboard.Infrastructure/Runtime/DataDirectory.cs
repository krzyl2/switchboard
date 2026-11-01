namespace Switchboard.Infrastructure.Runtime;

/// <summary>
/// The add-on's private data folder (<see cref="RuntimeEnvironment.DataPath"/>) as a type of its own, so the
/// files kept there ask the container for it instead of a bare string. A string converts to it implicitly.
/// </summary>
public sealed record DataDirectory(string Path)
{
    public static implicit operator DataDirectory(string path) => new(path);

    public override string ToString() => Path;
}
