using Switchboard.Domain.Balance;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// What the hourly balance and the free power say in this step (BAL-02, 14-ECONOMICS): owns the two smoothing windows (the
/// balance's and net billing's) and the last view, own load and net free power the allowance gate reads. Nothing here decides
/// anything; the gate narrows with what it reports.
/// </summary>
internal sealed class BalanceTracker
{
    private readonly FreePowerWindow _free = new();
    private readonly FreePowerWindow _freeNet = new();

    /// <summary>The hourly balance as this step read it; null while no energy counters are mapped.</summary>
    public BalanceView? View { get; private set; }

    /// <summary>What the controlled devices draw in this step; null while any is not known.</summary>
    public Watts? OwnLoad { get; private set; }

    /// <summary>Free power under net billing in this step; null under hourly netting and while not known.</summary>
    public Watts? NetFree { get; private set; }

    /// <summary>Reads this step: the flows of our own loads, the balance and the net-billing free power.</summary>
    public void Observe(DispatchInput input, DispatchSetup setup, Dictionary<Phase, Watts?> ours, DateTimeOffset now)
    {
        var (exportSample, ownLoad) = OwnFlows(input, setup, ours);
        View = ObserveBalance(input, setup, exportSample, ownLoad, now);
        OwnLoad = ownLoad;
        NetFree = ObserveNetFree(setup, exportSample, now);
    }

    /// <summary>No valid configuration, or a clock that went back: the windows start again and no view is held.</summary>
    public void Reset()
    {
        _free.Reset();
        _freeNet.Reset();
        View = null;
        OwnLoad = null;
        NetFree = null;
    }

    /// <summary>
    /// What Switchboard's own loads and the grid add up to in this step: the instantaneous free power (grid net export plus
    /// every own load added back, so the projection does not shrink by what Switchboard itself switched on) and the own load
    /// (what the controlled devices draw). Either is null while any phase's power or any own load is not known.
    /// </summary>
    private static (Watts? Export, Watts? Load) OwnFlows(DispatchInput input, DispatchSetup setup, Dictionary<Phase, Watts?> ours)
    {
        Watts? export = Watts.Zero;
        Watts? load = Watts.Zero;
        foreach (var phase in setup.Phases)
        {
            var own = ours.GetValueOrDefault(phase);
            load = load is { } running && own is { } drawn ? running + drawn : null;
            export = export is { } sum && own is { } added && input.Inputs.For(phase) is { DataValid: true, NetImport: { } netImport } ? sum + added - netImport : null;
        }

        return (export, load);
    }

    /// <summary>
    /// The hourly balance as this step reads it (BAL-02): free power smoothed over the window, and the balance of the running
    /// hour, valid only for the hour its time to the :00 is taken from and only while the monitor's reading is no older than
    /// max_age. Null while no energy counters are mapped: the balance is off until configured and nothing is gated by it.
    /// </summary>
    private BalanceView? ObserveBalance(DispatchInput input, DispatchSetup setup, Watts? export, Watts? load, DateTimeOffset now)
    {
        if (setup.Balance is not { } config || input.Inputs.Energy is null)
        {
            _free.Reset();
            return null;
        }

        var parameters = config.Parameters;
        var free = _free.Observe(now, export, parameters);
        double? balance = null;
        var untilNext = TimeSpan.Zero;
        BalanceBand? band = null;
        if (input.Balance is { } status && status.Reading.Wh is { } wh && now >= status.At && now - status.At <= parameters.MaxAge)
        {
            var remaining = status.Reading.HourStart.AddHours(1) - now;
            if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(1))
            {
                balance = wh;
                untilNext = remaining;
                band = HourlyBalanceRule.Band(remaining, parameters);
            }
        }

        double? projection = balance is { } known && free is { } freePower && load is { } drawing
            ? HourlyBalanceRule.Project(known, freePower, drawing, untilNext, parameters)
            : null;
        return new BalanceView(balance, free, load, untilNext, band, projection);
    }

    /// <summary>
    /// Free power under net billing (14-ECONOMICS): there is no hour to project, the instantaneous surplus decides, smoothed over
    /// economics.net_billing_window instead of the balance's window. It needs no energy counters. Null under hourly netting
    /// (the balance's own free power is used) and while the estimate is not known.
    /// </summary>
    private Watts? ObserveNetFree(DispatchSetup setup, Watts? export, DateTimeOffset now)
    {
        if (setup.Economics is not { Settlement.Id: SettlementModelId.NetBilling } economics || setup.Balance is not { } config)
        {
            _freeNet.Reset();
            return null;
        }

        return _freeNet.Observe(now, export, config.Parameters with { SmoothingWindow = economics.Parameters.NetBillingWindow });
    }
}
