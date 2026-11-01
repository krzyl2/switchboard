using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Dispatch;

namespace Switchboard.Application.Effector;

    /// <summary>One step's inputs and the decisions it collects.</summary>
    internal sealed record StepContext(
        GuardEvaluation Evaluation,
        DispatchEvaluation? Dispatch,
        OverrideSnapshot Overrides,
        ModeStatus Mode,
        ControlInputs? Inputs,
        DateTimeOffset Now,
        ImmutableArray<Decision>.Builder Decisions)
    {
        public DispatchSetup? DispatchSetup => Inputs?.DispatchSetup;
    }
