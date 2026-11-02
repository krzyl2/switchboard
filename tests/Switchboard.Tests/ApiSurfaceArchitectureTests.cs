using Microsoft.AspNetCore.Http;
using Switchboard.Tests.Host;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// API surface: the read-only runtime UI endpoints answer GET only; every state-changing method on
/// them is refused.
/// </summary>
public sealed class ApiSurfaceArchitectureTests
{
    [Theory]
    [InlineData("/api/power")]
    [InlineData("/api/statistics")]
    [InlineData("/api/excursions")]
    [InlineData("/api/devices")]
    public async Task New_endpoints_are_get_only(string path)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var get = await PresentationEndpointTests.SendAsync(factory, "GET", path);
        Assert.Equal(StatusCodes.Status200OK, get.Response.StatusCode);
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" })
        {
            var context = await PresentationEndpointTests.SendAsync(factory, method, path);
            Assert.False(context.Response.StatusCode is >= 200 and < 300, $"{method} {path} answered {context.Response.StatusCode}");
        }
    }
}
