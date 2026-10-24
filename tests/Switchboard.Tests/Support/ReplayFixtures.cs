using Switchboard.Infrastructure.Replay;

namespace Switchboard.Tests.Support;

/// <summary>
/// Recorded and synthetic replay data. The live recording is the maintainer's household
/// consumption and is gitignored; tests that need it fail loudly, naming the file, when it is absent
/// (they are never skipped silently).
/// </summary>
internal static class ReplayFixtures
{
    public const string SyntheticFile = "synthetic-three-phase-import-export.csv";

    private static readonly Lazy<ReplayData> Synthetic = new(() => ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", SyntheticFile)));

    private static readonly Lazy<ReplayData?> Live = new(LoadLive);

    public static ReplayData SyntheticData => Synthetic.Value;

    public static ReplayData LiveData
    {
        get
        {
            if (Live.Value is { } data)
            {
                return data;
            }

            Assert.Fail(
                "The recorded live fixture tests/fixtures/replay/live-*.csv.gz is missing. It is gitignored (the maintainer's "
                + "household data). Place it there, or run the suite with --filter Category!=LiveReplay to leave these tests out.");
            return null!;
        }
    }

    private static ReplayData? LoadLive()
    {
        var directory = RepoPaths.Combine("tests", "fixtures", "replay");
        var file = Directory.Exists(directory) ? Directory.GetFiles(directory, "live-*.csv.gz").Order().LastOrDefault() : null;
        return file is null ? null : ReplayData.Load(file);
    }
}
