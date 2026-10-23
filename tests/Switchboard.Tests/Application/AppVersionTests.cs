using Switchboard.Application;

namespace Switchboard.Tests.Application;

/// <summary>Users compare the shown version with the store's; a commit hash suffix makes that harder.</summary>
public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+abc123def", "0.1.0")]
    [InlineData("1.2.3-beta.1+sha.5114f85", "1.2.3-beta.1")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("+abc", "unknown")]
    public void Build_metadata_is_stripped(string? informational, string expected)
    {
        Assert.Equal(expected, AppVersion.FromInformationalVersion(informational).Value);
    }
}
