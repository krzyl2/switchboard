using System.Collections.Immutable;
using System.Reflection;
using Switchboard.Application.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// SAFE-07 at the guard: what survives a restart is the overload budget (thermal state), never a
/// lockout. The downtime earns recovery only after a clean stop with the clock moving forward;
/// after a crash the loads were uncontrolled, so the stored value stands.
/// </summary>
public sealed class GuardRestartTests
{
    private static readonly DateTimeOffset SavedAt = GuardScenario.Start - TimeSpan.FromMinutes(5);

    [Fact]
    public void Budget_recovers_for_downtime_when_clock_moved_forward()
    {
        // 0.4 saved five minutes ago at a clean stop: 300 s of a 600 s recovery add 0.5.
        var s = Restored(new GuardMemory(Budgets(0.4), CleanShutdown: true));

        Assert.Equal(0.9, s.PhaseStatus(Phase.L1).Budget, 9);
    }

    [Fact]
    public void Budget_stays_when_clock_stepped_back()
    {
        // Saved "in the future": the clock stepped back since, so no downtime can be trusted.
        var s = Restored(new GuardMemory(Budgets(0.4, GuardScenario.Start + TimeSpan.FromHours(1)), CleanShutdown: true));

        Assert.Equal(0.4, s.PhaseStatus(Phase.L1).Budget, 12);
    }

    [Fact]
    public void Crash_restart_keeps_stored_budget()
    {
        var s = Restored(new GuardMemory(Budgets(0.4), CleanShutdown: false));

        Assert.Equal(0.4, s.PhaseStatus(Phase.L1).Budget, 12);
    }

    [Fact]
    public void Restored_budget_kept_while_configuration_is_invalid()
    {
        // A broken document at start must not wipe the stored budgets on the next save.
        var s = new GuardScenario(TestDevices.Document());
        s.Engine.Restore(new GuardMemory(Budgets(0.4), CleanShutdown: false), s.Now);
        s.ConfigurationValid(false);
        s.Evaluate();

        Assert.Equal(0.4, s.Engine.Export().Budgets[Phase.L1].Budget, 12);
    }

    [Fact]
    public void Lockouts_restart_at_process_start()
    {
        // The heater was on for hours before the restart; its 6 min min_on starts again now.
        var s = new GuardScenario(TestDevices.Document(), settledLockouts: false);
        s.SampleAll(8, 8, 8);

        var heater = s.DeviceStatus("heater_l1");
        Assert.Equal(TimeSpan.FromMinutes(6), heater.MinOnRemaining);
        Assert.Equal(TimeSpan.Zero, heater.MinOffRemaining);
        s.RunFor(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(4), s.DeviceStatus("heater_l1").MinOnRemaining);
    }

    [Fact]
    public void Memory_holds_no_lockout()
    {
        // Structural: nothing in what is persisted can carry a lockout expiry.
        var properties = typeof(GuardMemory).GetProperties().Concat(typeof(BudgetMemory).GetProperties());

        Assert.DoesNotContain(properties, p => p.PropertyType == typeof(TimeSpan) || p.Name.Contains("Lockout", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Budget", "At"], typeof(BudgetMemory).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Where(n => n != "EqualityContract"));
    }

    private static GuardScenario Restored(GuardMemory memory)
    {
        var s = new GuardScenario(TestDevices.Document());
        s.Engine.Restore(memory, s.Now);
        s.SampleAll(8, 8, 8);
        return s;
    }

    private static ImmutableDictionary<Phase, BudgetMemory> Budgets(double budget, DateTimeOffset? at = null) =>
        new[] { Phase.L1, Phase.L2, Phase.L3 }.ToImmutableDictionary(p => p, _ => new BudgetMemory(budget, at ?? SavedAt));
}
