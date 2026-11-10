namespace NightDim.Core;

internal sealed class GammaRampBackend(IGammaTransport transport, IGammaJournal journal)
{
    private sealed class State(GammaRamp baseline)
    {
        public GammaRamp Baseline { get; } = baseline;
        public GammaRamp? Applied { get; set; }
        public GammaRamp? Requested { get; set; }
        public GammaRamp? Pending { get; set; }
        public EffectDiagnostic? Failure { get; set; }
        public GammaJournalEntry Entry => new(Baseline, Applied, Pending);
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public (bool Applied, EffectDiagnostic Status) Apply(DisplayInfo display, EffectSettings settings, double gain)
    {
        if (display.Hdr == HdrState.Hdr)
            return (false, EffectDiagnostic.HdrUnsupported);
        if (display.Hdr != HdrState.Sdr)
            return (false, EffectDiagnostic.SdrUnconfirmed);
        if (!display.CanRecoverGamma)
            return (false, EffectDiagnostic.RecoveryUnsupported);

        if (!_states.TryGetValue(display.Id, out State? state))
        {
            if (!Recover(display))
                return (false, EffectDiagnostic.JournalReadFailed);
            if (!transport.TryRead(display, out GammaRamp? baseline) || baseline is null || !baseline.IsRestorable())
                return (false, EffectDiagnostic.BaselineReadFailed);

            state = new State(baseline);
            _states[display.Id] = state;
        }

        if (state.Failure is { } failure)
            return (false, failure);

        GammaRamp requested = GammaCurve.Build(state.Baseline, settings, gain);
        GammaRamp candidate = GammaCurve.ConstrainForGdi(requested, state.Baseline, out bool constrained);
        if (!transport.TryRead(display, out GammaRamp? before) || before is null)
            return Reject(state, EffectDiagnostic.ReadbackUnavailable);

        if (!(state.Applied ?? state.Baseline).Matches(before))
            return YieldOwnership(display, state);

        if (candidate.Matches(state.Baseline))
        {
            if (!Restore(display))
                return (false, EffectDiagnostic.RestoreUnverified);
            return (false, EffectDiagnostic.CorrectionLimitedByBaseline);
        }

        EffectDiagnostic status = constrained ? EffectDiagnostic.ReadbackConfirmedLimited : EffectDiagnostic.ReadbackConfirmed;
        if (state.Applied is not null && state.Requested?.Matches(candidate) == true)
            return (true, status);

        var intent = new GammaJournalEntry(state.Baseline, state.Applied, candidate);
        if (!journal.TrySave(display.Id, intent))
        {
            RestoreState(display, state);
            return Reject(state, EffectDiagnostic.JournalWriteFailed);
        }

        // Recheck after saving the journal. The Windows API has no atomic compare-and-set.
        if (!transport.TryRead(display, out GammaRamp? lastRead) || lastRead is null)
        {
            if (state.Applied is null)
                journal.TryRemove(display.Id);
            else
                journal.TrySave(display.Id, state.Entry);
            return Reject(state, EffectDiagnostic.ReadbackUnavailable);
        }
        if (!before.Matches(lastRead))
            return YieldOwnership(display, state);

        state.Pending = candidate;
        bool wrote = transport.TryWrite(display, candidate);
        if (!transport.TryRead(display, out GammaRamp? after) || after is null)
            return Reject(state, EffectDiagnostic.PostWriteReadbackUnavailable);

        if (wrote && !before.Matches(after) && candidate.WithinOneByteStep(after))
        {
            state.Applied = after;
            state.Requested = candidate;
            state.Pending = null;
            if (!journal.TrySave(display.Id, state.Entry) && !after.Matches(candidate))
            {
                RestoreState(display, state);
                return Reject(state, EffectDiagnostic.ConfirmedRampSaveFailed);
            }

            return (true, status);
        }

        bool restored = RestoreState(display, state);
        return Reject(state, restored
            ? EffectDiagnostic.ChangeUnverifiedWriteSuspended
            : EffectDiagnostic.ChangeUnverifiedRestoreDeferred);
    }

    public bool Restore(DisplayInfo display)
    {
        if (!TryReleaseOwnership(display))
            return false;

        _states.Remove(display.Id);
        return true;
    }

    public void Disconnect(string displayId) => _states.Remove(displayId);

    public bool TryReleaseOwnership(DisplayInfo display)
    {
        if (_states.TryGetValue(display.Id, out State? state))
        {
            // Retain the failure latch when falling back so gamma writes do not resume automatically.
            if (!RestoreState(display, state))
                return false;
            if (state.Failure is null)
                _states.Remove(display.Id);
            return true;
        }

        // Topology changes or restarts can leave only the persisted entry.
        return journal.TryRead(display.Id, out GammaJournalEntry? entry)
            && (entry is null || RestoreEntry(display, entry));
    }

    public bool Recover(DisplayInfo display)
    {
        if (_states.ContainsKey(display.Id) || !display.CanRecoverGamma)
            return true;
        return TryReleaseOwnership(display);
    }

    private bool RestoreState(DisplayInfo display, State state)
    {
        if ((state.Applied is not null || state.Pending is not null) && !RestoreEntry(display, state.Entry))
            return false;

        state.Applied = null;
        state.Requested = null;
        state.Pending = null;
        return true;
    }

    private bool RestoreEntry(DisplayInfo display, GammaJournalEntry entry)
    {
        if (display.Hdr != HdrState.Sdr || !display.CanRecoverGamma ||
            !transport.TryRead(display, out GammaRamp? current) || current is null)
            return false;

        if (!entry.Baseline.Matches(current) && entry.Owns(current))
        {
            transport.TryWrite(display, entry.Baseline);
            if (!transport.TryRead(display, out GammaRamp? restored) || restored is null ||
                !entry.Baseline.Matches(restored))
                return false;
        }

        // If another writer owns the current curve, relinquish our journal without touching it.
        return journal.TryRemove(display.Id);
    }

    private (bool, EffectDiagnostic) YieldOwnership(DisplayInfo display, State state)
    {
        journal.TryRemove(display.Id);
        state.Applied = null;
        state.Pending = null;
        return Reject(state, EffectDiagnostic.ExternalRampChange);
    }

    private static (bool, EffectDiagnostic) Reject(State state, EffectDiagnostic reason)
    {
        state.Failure = reason;
        return (false, reason);
    }
}
