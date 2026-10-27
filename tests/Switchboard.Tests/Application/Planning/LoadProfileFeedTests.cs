using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Configuration;
using Switchboard.Application.Planning;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;
using Microsoft.Extensions.Time.Testing;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.Planning;

/// <summary>
/// The wiring of the load profile to the running configuration. Why it matters: <c>load-profile.json</c> is the planner's own file, so a 0.10 file (no
/// <c>planner:</c> section) must neither read nor write it nor create anything, or an upgrade would change what is on disk before the planner was ever
/// switched on; and with a planner the profile must be fed from the statistics writer loop and survive a failing disk without touching the statistics.
/// </summary>
public sealed class LoadProfileFeedTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Monday10 = new(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));

    private sealed class CountingStore : IPresentationStore<LoadProfile>
    {
        public int Loads { get; private set; }

        public int Saves { get; private set; }

        public LoadProfile? Saved { get; set; }

        public Exception? LoadThrows { get; set; }

        public PresentationLoad<LoadProfile> Load()
        {
            Loads++;
            return LoadThrows is { } ex ? throw ex : new(Saved, Saved is null ? StateFileStatus.Fresh : StateFileStatus.Loaded, null);
        }

        public void Save(LoadProfile state)
        {
            Saves++;
            Saved = state;
        }
    }

    private static ConfigurationResult Config(string planner = "") => TestConfigurations.Valid(TestConfigurations.ThreePhase() + planner);

    private static LoadProfileFeed Feed(CountingStore store, ConfigurationResult? config) =>
        new(store, () => config?.Configuration, new SignalClock(new SteppableTime(Monday10) { Zone = Warsaw }));

    private static StatisticsSnapshot TwoHours()
    {
        var buckets = Enumerable.Range(0, 24).Select(i => EnergyBucket.Empty(Monday10.AddMinutes(5 * i)) with { Observed = TimeSpan.FromMinutes(5), Uncontrolled = new KilowattHours(0.1) });
        return new StatisticsSnapshot(Monday10.AddHours(2), TimeSpan.FromMinutes(5), false, [], buckets.ToImmutableSortedDictionary(b => b.Start, b => b), LifetimeCounters.Zero, Monday10.AddHours(2), 1);
    }

    [Fact]
    public void Without_a_planner_section_the_file_is_neither_read_nor_written()
    {
        // Why: a 0.10 file must leave no load-profile.json behind and read none: the profile exists only for a planner.
        var store = new CountingStore();
        var feed = Feed(store, Config());

        var moved = feed.Update(TwoHours(), Monday10.AddHours(2).AddMinutes(1));

        Assert.False(moved);
        Assert.Same(LoadProfile.Empty, feed.Current);
        Assert.Equal((0, 0), (store.Loads, store.Saves));
    }

    [Fact]
    public void Without_a_configuration_at_all_nothing_is_touched()
    {
        // Why: before the first valid configuration nothing is known about the planner; an invalid document is not a planner either.
        var store = new CountingStore();
        var feed = Feed(store, null);

        Assert.False(feed.Update(TwoHours(), Monday10.AddHours(3)));
        Assert.Equal((0, 0), (store.Loads, store.Saves));
    }

    [Fact]
    public void With_a_planner_section_the_profile_is_restored_once_seeded_from_the_statistics_and_saved()
    {
        // Why: the first update seeds the profile from the retained buckets (a fresh install does not wait a week for a shape), and a second one with nothing
        // new must not rewrite the file (flash storage).
        var store = new CountingStore();
        var feed = Feed(store, Config("\nplanner:\n  dispatch: off\n"));
        var now = Monday10.AddHours(2).AddMinutes(1);

        var first = feed.Update(TwoHours(), now);
        var again = feed.Update(TwoHours(), now);

        Assert.True(first);
        Assert.False(again);
        Assert.Equal((1, 1), (store.Loads, store.Saves));
        Assert.Equal(2, feed.Current.FilledBins);
        Assert.Equal(1.2, feed.Current.Bins[10].MeanKw, 9);
    }

    [Fact]
    public void A_read_before_any_update_restores_the_profile_so_the_plan_does_not_start_from_the_fallback()
    {
        // Why: api/plan may be asked before the first statistics pass; a profile saved yesterday must already be what it forecasts from.
        var seeded = new CountingStore();
        Feed(seeded, Config("\nplanner:\n  dispatch: off\n")).Update(TwoHours(), Monday10.AddHours(2).AddMinutes(1));
        var store = new CountingStore { Saved = seeded.Saved };

        var feed = Feed(store, Config("\nplanner:\n  dispatch: off\n"));

        Assert.Equal(2, feed.Current.FilledBins);
        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public async Task The_statistics_writer_loop_feeds_it_and_a_failing_profile_file_never_stops_the_statistics()
    {
        // Why: load-profile.json is a convenience of the planner; an IOException from it (a full disk) must not break statistics.json, and must be
        // logged once, not at every interval. Without a planner section the same loop touches nothing.
        var clock = new SteppableTime(Monday10) { Zone = Warsaw };
        var log = new LogCapture();
        var statistics = new FeedMemory<StatisticsState>();
        var profile = new CountingStore { LoadThrows = new IOException("disk gone") };
        var hub = TestStatistics.Hub();
        var persistence = new PresentationPersistence(
            hub,
            statistics,
            new FeedMemory<ExcursionState>(),
            MsOptions.Create(TestStatistics.Options()),
            new FakeTimeProvider(Monday10),
            new SignalClock(clock),
            log.For<PresentationPersistence>(),
            Feed(profile, Config("\nplanner:\n  dispatch: off\n")));
        persistence.Load();

        await persistence.SaveDueAsync(CancellationToken.None);
        await persistence.SaveDueAsync(CancellationToken.None);

        Assert.Equal(2, profile.Loads);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("load-profile.json", StringComparison.Ordinal));

        var quiet = new CountingStore();
        var without = new PresentationPersistence(hub, statistics, new FeedMemory<ExcursionState>(), MsOptions.Create(TestStatistics.Options()), new FakeTimeProvider(Monday10), new SignalClock(clock), log.For<PresentationPersistence>(), Feed(quiet, Config()));
        without.Load();
        await without.SaveDueAsync(CancellationToken.None);
        Assert.Equal((0, 0), (quiet.Loads, quiet.Saves));
    }

    private sealed class FeedMemory<T> : IPresentationStore<T>
        where T : class
    {
        public PresentationLoad<T> Load() => new(null, StateFileStatus.Fresh, null);

        public void Save(T state)
        {
        }
    }
}
