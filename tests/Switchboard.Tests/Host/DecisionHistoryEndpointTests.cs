using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// MODE-04 under 04 §8 "Decision log or /data unwritable": while the log cannot be written, the
/// newest decisions exist only in the log's memory. The newest history page shows them too, so the
/// sheds happening now never disappear behind a frozen list of files; older pages are file positions
/// and stay exactly as the files hold them.
/// </summary>
public sealed class DecisionHistoryEndpointTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Newest_page_shows_unwritten_decisions_held_in_memory()
    {
        var onDisk = new[] { TestDecisions.Shed(At.AddMinutes(-1)), TestDecisions.Shed(At.AddMinutes(-2)) }.ToImmutableArray();
        var unwritten = TestDecisions.Shed(At.AddMinutes(3));
        var memory = new[] { onDisk[1], onDisk[0], TestDecisions.Shed(At.AddMinutes(2)), unwritten };

        var merged = DecisionHistoryEndpoint.WithUnwritten(onDisk, memory, Query());

        Assert.Equal([unwritten.Id, memory[2].Id, onDisk[0].Id, onDisk[1].Id], merged.Select(d => d.Id));
    }

    [Fact]
    public void Unwritten_decisions_respect_the_time_range()
    {
        var onDisk = ImmutableArray<Decision>.Empty;
        var inside = TestDecisions.Shed(At.AddMinutes(1));
        var after = TestDecisions.Shed(At.AddHours(2));

        var merged = DecisionHistoryEndpoint.WithUnwritten(onDisk, [inside, after], Query(to: At.AddHours(1)));

        Assert.Equal([inside.Id], merged.Select(d => d.Id));
    }

    private static DecisionHistoryQuery Query(DateTimeOffset? to = null) => new(null, to, null, null, null, null, 50, null);
}
