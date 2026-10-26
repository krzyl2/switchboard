using Switchboard.Domain.Balance;

namespace Switchboard.Application.Balance;

/// <summary>
/// The baseline of the running hour in the data folder, so a restart inside the hour keeps the balance
/// (BAL-01). Untrusted on read (a corrupt file is set aside and the balance simply starts unknown),
/// written atomically. Implemented in Infrastructure; written only from the monitor's writer loop,
/// never from the thread that publishes the guard's inputs.
/// </summary>
public interface IBalanceBaselineFile
{
    /// <summary>The saved baseline, or null when there is none or the file was unusable.</summary>
    HourBaseline? Load();

    /// <summary>Throws <see cref="IOException"/> when the disk refuses.</summary>
    void Save(HourBaseline baseline);
}
