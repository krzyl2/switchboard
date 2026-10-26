using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Balance;
using Switchboard.Domain.Balance;
using Switchboard.Infrastructure.Persistence;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// /data/balance-baseline.json: the counters at the start of the running hour survive a restart, so the
/// hourly balance is not lost for up to an hour. The file is untrusted on read: a corrupt one is set
/// aside and the balance starts unknown, never a crash and never a made-up baseline.
/// </summary>
public sealed class BalanceBaselineFileTests : IDisposable
{
    private static readonly DateTimeOffset Hour = new(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));

    private IBalanceBaselineFile File() => new BalanceBaselineFile(_directory, NullLogger<BalanceBaselineFile>.Instance);

    [Fact]
    public void The_baseline_survives_a_restart_to_the_instant_and_the_counters()
    {
        File().Save(new HourBaseline(Hour, 12228.751, 16864.138));

        var loaded = File().Load();

        Assert.NotNull(loaded);
        Assert.Equal(Hour, loaded.HourStart);
        Assert.Equal(12228.751, loaded.ExportKwh);
        Assert.Equal(16864.138, loaded.ImportKwh);
    }

    [Fact]
    public void An_absent_file_gives_no_baseline()
    {
        Assert.Null(File().Load());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schema_version":2,"hour_start":"2026-10-26T09:00:00Z","export_kwh":1,"import_kwh":1}""")]
    [InlineData("""{"schema_version":1,"hour_start":"2026-10-26T09:00:00Z","export_kwh":-1,"import_kwh":1}""")]
    [InlineData("""{"schema_version":1,"hour_start":"yesterday","export_kwh":1,"import_kwh":1}""")]
    [InlineData("""{"schema_version":1,"hour_start":"2026-10-26T09:00:00Z","export_kwh":1,"import_kwh":1,"extra":1}""")]
    public void A_corrupt_file_is_set_aside_and_there_is_no_baseline(string content)
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(Path.Combine(_directory, BalanceBaselineFile.Name), content);

        var loaded = File().Load();

        Assert.Null(loaded);
        Assert.True(System.IO.File.Exists(Path.Combine(_directory, BalanceBaselineFile.Name + ".corrupt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
