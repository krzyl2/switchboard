using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Infrastructure.Persistence;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// /data/mqtt-client.json: the per-install part of the MQTT client id. A broker keeps one session per client id and drops the older connection when the same
/// id connects again, so a constant id let two installs on one broker knock each other off (and inherit each other's Last Will), while a new id at every
/// start would leave stale sessions behind. The id is therefore generated once, kept across restarts, and a damaged file never stops the add-on.
/// </summary>
public sealed class MqttClientIdFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));
    private readonly string _other = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_id_is_generated_once_and_the_next_start_reads_the_same_one()
    {
        var first = File(_directory).LoadOrCreate();
        var second = File(_directory).LoadOrCreate();

        Assert.Equal(first, second);
        Assert.Matches(@"\A[0-9a-f]{12}\z", first.Value);
        Assert.True(System.IO.File.Exists(Path.Combine(_directory, MqttClientIdFile.Name)));
    }

    [Fact]
    public void Two_installs_get_different_ids()
    {
        Assert.NotEqual(File(_directory).LoadOrCreate(), File(_other).LoadOrCreate());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schema_version":1,"install_id":"0123456789AB"}""")]
    [InlineData("""{"schema_version":1,"install_id":"0123456789a"}""")]
    [InlineData("""{"schema_version":1,"install_id":"0123456789ab\n"}""")]
    [InlineData("""{"schema_version":1,"install_id":"switchboard"}""")]
    [InlineData("""{"schema_version":2,"install_id":"0123456789ab"}""")]
    [InlineData("""{"schema_version":1,"install_id":"0123456789ab","extra":1}""")]
    public void A_corrupt_file_is_set_aside_and_a_new_id_is_kept_from_then_on(string content)
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(Path.Combine(_directory, MqttClientIdFile.Name), content);

        var created = File(_directory).LoadOrCreate();

        Assert.NotEqual("0123456789ab", created.Value);
        Assert.Matches(@"\A[0-9a-f]{12}\z", created.Value);
        Assert.True(System.IO.File.Exists(Path.Combine(_directory, MqttClientIdFile.Name + ".corrupt")));
        Assert.Equal(created, File(_directory).LoadOrCreate());
    }

    [Fact]
    public void A_valid_file_written_by_hand_is_used_as_it_is()
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(Path.Combine(_directory, MqttClientIdFile.Name), """{"schema_version":1,"install_id":"0123456789ab"}""");

        Assert.Equal("0123456789ab", File(_directory).LoadOrCreate().Value);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_left_in_place_and_its_id_is_used_again_once_it_reads()
    {
        // An unreadable file (a lock, a wrong owner or mode after a restore, an I/O error) may still hold the kept id: writing a new one over it
        // would change the install's client id for good, and with it a broker ACL keyed on the old one. The id is new for this process only.
        if (!OperatingSystem.IsWindows())
        {
            // A read-only denial the test can set and lift needs an ACL; as root (the container) a Unix file mode would not deny it.
            Assert.Skip("Needs a Windows ACL to deny reading the file while it can still be replaced.");
            return;
        }

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, MqttClientIdFile.Name);
        System.IO.File.WriteAllText(path, """{"schema_version":1,"install_id":"0123456789ab"}""");

        DenyReading(path, true);
        var meanwhile = File(_directory).LoadOrCreate();
        DenyReading(path, false);

        Assert.NotEqual("0123456789ab", meanwhile.Value);
        Assert.Matches(@"\A[0-9a-f]{12}\z", meanwhile.Value);
        Assert.Equal("0123456789ab", File(_directory).LoadOrCreate().Value);
    }

    [Fact]
    public void An_unwritable_data_folder_still_gives_an_id()
    {
        // The broker connection must not depend on /data: the id then lasts for this process only.
        Directory.CreateDirectory(_other);
        var notAFolder = Path.Combine(_other, "data");
        System.IO.File.WriteAllText(notAFolder, "a file where the data folder should be");

        var id = File(notAFolder).LoadOrCreate();

        Assert.Matches(@"\A[0-9a-f]{12}\z", id.Value);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _directory, _other })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static MqttClientIdFile File(string directory) => new(directory, NullLogger<MqttClientIdFile>.Instance);

    /// <summary>Denies (or allows again) reading the file's content to the current user; its attributes stay readable and the folder may still replace it.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DenyReading(string path, bool deny)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ReadData,
            System.Security.AccessControl.AccessControlType.Deny);
        if (deny)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRule(rule);
        }

        file.SetAccessControl(security);
    }
}
