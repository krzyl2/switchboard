using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Application.Planning;

/// <summary>
/// A spread goal through the real <see cref="PlannerService"/> (EV.md 2a): replanned every minute for a night of charging with a SoC that moves in whole percent steps
/// and with noise. Why it matters: the commanded ceiling is what the go-e is told; if it dithers, every change is an amp command and counts against
/// persistence_up_cycles, and a ceiling that climbs to 20 A in the last hours ends the charge early.
/// </summary>
public sealed class PlannerServiceSpreadTests
{
    private static readonly DeviceName DeviceA = DeviceName.Parse("device_a");
    private static readonly DateTimeOffset Start = Local(2026, 10, 26, 22).ToUniversalTime();
    private static readonly DateTimeOffset Deadline = Local(2026, 10, 27, 6);

    private sealed class World(double kwh, double noise, double quantum) : IPlannerSource
    {
        public double Remaining { get; set; } = kwh;
        public double Noise { get; set; } = noise;
        public int Tick { get; set; }
        public PlannerCapture? Capture(DateTimeOffset now)
        {
            var inputs = Custom(now, (int)Math.Ceiling((Deadline - now).TotalHours) + 1, _ => ("z", 1.0, 0.1, 0));
            // reported remaining: what a SoC in whole percent of 18 kWh would show (quantised), plus noise
            var shown = quantum > 0 ? Math.Round(Remaining / quantum) * quantum : Remaining;
            shown += ((Tick * 7919 % 13) - 6) / 6.0 * Noise;
            var dev = new PlanDevice(DeviceA, PlanDeviceKind.Modulating, 1380, 4600, TimeSpan.Zero, [new PlanKeyShare(L1, 1)], true, new ValueSpec(10, ValueFollows.Static, 1), null, 1, 230);
            var goal = Goal(DeviceA, Math.Max(0, shown), Deadline, true) with { Spread = true };
            return new PlannerCapture(Input(now, inputs, [dev], [goal], Capacity(5000)), [], "f", "fp");
        }
    }

    private sealed class NoSuspend : IPlannerSuspension
    {
        public bool Suspended => false;
        public event EventHandler? Changed { add { } remove { } }
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.0, 0.18)]
    [InlineData(0.1, 0.18)]
    public async Task Through_the_service_the_commanded_ceiling_does_not_dither(double noise, double quantum)
    {
        var time = new FakeTimeProvider(Start);
        time.SetLocalTimeZone(Warsaw);
        var world = new World(26.0, noise, quantum);
        var holder = new PlanHolder();
        var service = new PlannerService(world, new NoSuspend(), holder, time, new LogCapture().For<PlannerService>());
        await service.StepAsync(TestContext.Current.CancellationToken);
        time.Advance(Parameters().StartDelay);
        await service.StepAsync(TestContext.Current.CancellationToken);
        var seen = new List<(DateTimeOffset At, double Cap, double Energy)>();
        while (time.GetUtcNow() < Deadline)
        {
            world.Tick++;
            await service.StepAsync(TestContext.Current.CancellationToken);
            var entry = holder.Current.Plan?.EntryAt(DeviceA, time.GetUtcNow());
            if (entry is { Want: PlanWant.Run or PlanWant.Urgent })
            {
                seen.Add((time.GetUtcNow(), entry.MaxPowerW, entry.EnergyKwh));
                var dt = TimeSpan.FromMinutes(1).TotalHours;
                var p = entry.MaxPowerW / 1000 * dt;
                world.Remaining = Math.Max(0, world.Remaining - p);
            }
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var caps = seen.Select(s => s.Cap).ToList();
        var changes = 0;
        for (var i = 1; i < caps.Count; i++)
        {
            if (Math.Abs(caps[i] - caps[i - 1]) > 1)
            {
                changes++;
            }
        }

        // Until the last slot (a single-slot window is not spread) the ceiling stays below the device's: no jump to 20 A for the escalation margin.
        var beforeLastSlot = seen.Where(s => s.At < Deadline.AddHours(-1)).Select(s => s.Cap).ToList();
        Assert.NotEmpty(beforeLastSlot);
        Assert.True(changes <= 3, $"the ceiling changed {changes} times");
        Assert.True(beforeLastSlot.Max() < 4600, "a spread goal on schedule never climbs to the ceiling of the device before its last slot");
    }
}
