namespace Switchboard.Tests.Support;

internal static class RepoPaths
{
    private const string SolutionFile = "Switchboard.slnx";

    /// <summary>The repository root, found by walking up from the test binaries to the solution file.</summary>
    public static string Root { get; } = FindRoot();

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"{SolutionFile} not found above {AppContext.BaseDirectory}.");
    }
}
