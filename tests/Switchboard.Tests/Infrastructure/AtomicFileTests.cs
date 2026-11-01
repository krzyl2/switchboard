using System.Text;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// Every data file (state, statistics, excursions, load profile, EV state, phase evidence, the
/// configuration the wizard saves) is replaced through one helper. What matters is what a reader
/// and a restart can see: the old file or the new one, never part of either.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDataDirectory _data = new();

    private string Target => Path.Combine(_data.Path, "file.json");

    [Fact]
    public void A_write_creates_the_file_with_exactly_the_bytes_and_leaves_no_temporary_file()
    {
        byte[] payload = [0x7b, 0x0a, 0x7d, 0xff];

        AtomicFile.Write(Target, payload);

        Assert.Equal(payload, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + AtomicFile.TemporarySuffix));
        Assert.Equal([Target], Directory.GetFiles(_data.Path));
    }

    [Fact]
    public void A_write_creates_a_missing_directory()
    {
        var nested = Path.Combine(_data.Path, "a", "b", "file.json");

        AtomicFile.Write(nested, "x"u8);

        Assert.Equal("x", File.ReadAllText(nested));
    }

    [Fact]
    public void A_failed_replace_leaves_the_previous_file_whole_and_never_shows_the_new_content_under_the_name()
    {
        // Why: a crash or a refused rename between "written" and "in place" must not corrupt the file the next start reads.
        AtomicFile.Write(Target, "old"u8);

        var ex = Assert.Throws<IOException>(() => AtomicFile.Write(Target, "new"u8, (_, _) => throw new IOException("rename refused")));

        Assert.Equal("rename refused", ex.Message);
        Assert.Equal("old", File.ReadAllText(Target));
    }

    [Fact]
    public void A_failed_temporary_write_leaves_the_previous_file_whole()
    {
        AtomicFile.Write(Target, "old"u8);
        Directory.CreateDirectory(Target + AtomicFile.TemporarySuffix);

        Assert.ThrowsAny<Exception>(() => AtomicFile.Write(Target, "new"u8));

        Assert.Equal("old", File.ReadAllText(Target));
    }

    [Fact]
    public void A_leftover_temporary_file_of_a_crashed_write_is_replaced_and_never_read_as_the_file()
    {
        File.WriteAllText(Target + AtomicFile.TemporarySuffix, "half a wri");
        Assert.False(File.Exists(Target));

        AtomicFile.Write(Target, "whole"u8);

        Assert.Equal("whole", File.ReadAllText(Target));
        Assert.False(File.Exists(Target + AtomicFile.TemporarySuffix));
    }

    [Fact]
    public void A_write_over_a_longer_old_file_leaves_no_tail_of_the_old_one()
    {
        AtomicFile.Write(Target, Encoding.UTF8.GetBytes(new string('a', 4096)));

        AtomicFile.Write(Target, "b"u8);

        Assert.Equal("b", File.ReadAllText(Target));
    }

    [Fact]
    public void A_named_temporary_file_that_exists_is_refused_rather_than_taken_over()
    {
        // The configuration store names its temporary file uniquely and must never write into an existing one.
        var temporary = Path.Combine(_data.Path, ".t.tmp");
        File.WriteAllText(temporary, "mine");

        Assert.Throws<IOException>(() => AtomicFile.WriteTemporary(temporary, "x"u8, FileMode.CreateNew));

        Assert.Equal("mine", File.ReadAllText(temporary));
    }

    [Fact]
    public void The_directory_is_flushed_where_the_platform_can_and_skipped_without_error_where_it_cannot()
    {
        var synced = AtomicFile.SyncDirectory(_data.Path);

        Assert.Equal(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), synced);
        Assert.False(AtomicFile.SyncDirectory(Path.Combine(_data.Path, "missing")));
    }

    public void Dispose() => _data.Dispose();
}
