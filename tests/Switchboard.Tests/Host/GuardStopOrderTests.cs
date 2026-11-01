using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Switchboard.Host.Hosting;

namespace Switchboard.Tests.Host;

/// <summary>
/// Why: the guard's shutdown safe state (every controlled device off) has to be sent while the command
/// path, MQTT and the Home Assistant link are still up, and state.json written after the offs. That
/// must not be a side effect of the order the services were registered in: a later reshuffle of the
/// composition root would silently stop sending the offs.
/// </summary>
public sealed class GuardStopOrderTests
{
    [Fact]
    public void The_guard_shuts_down_in_the_stopping_phase_the_host_runs_before_any_service_stops()
    {
        Assert.True(typeof(IHostedLifecycleService).IsAssignableFrom(typeof(GuardService)));
    }

    [Theory]
    [MemberData(nameof(Permutations))]
    public async Task The_offs_leave_before_the_command_path_mqtt_and_the_link_stop_whatever_the_registration_order(string[] registration)
    {
        var log = new List<string>();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        foreach (var name in registration)
        {
            builder.Services.AddSingleton<IHostedService>(name == "guard" ? (IHostedService)new FakeGuard(log) : new FakeService(name, log));
        }

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        // The guard's offs and its writes come first, whatever the order; every service's stop follows.
        Assert.Equal(["guard:offs", "guard:persisted"], log.Take(2));
        Assert.Equal(["command:stop", "guard:stop", "link:stop", "mqtt:stop"], log.Skip(2).Order(StringComparer.Ordinal));
        Assert.All(new[] { "command:stop", "mqtt:stop", "link:stop" }, s => Assert.True(log.IndexOf(s) > log.IndexOf("guard:persisted")));
    }

    public static TheoryData<string[]> Permutations()
    {
        var data = new TheoryData<string[]>();
        string[] all = ["guard", "command", "mqtt", "link"];
        foreach (var order in Permute(all))
        {
            data.Add(order);
        }

        return data;
    }

    private static IEnumerable<string[]> Permute(string[] items)
    {
        if (items.Length == 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permute(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    private sealed class FakeService(string name, List<string> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            log.Add($"{name}:stop");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGuard(List<string> log) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken)
        {
            log.Add("guard:offs");
            log.Add("guard:persisted");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            log.Add("guard:stop");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
