namespace NightDim.Core;

public enum HdrState { Unknown, Sdr, Hdr }

public sealed record DisplayInfo(
    string Id, string DeviceName, string Name,
    int X, int Y, int Width, int Height, bool IsPrimary, HdrState Hdr,
    bool CanRecoverGamma = true);

public interface IGammaTransport
{
    bool TryRead(DisplayInfo display, out GammaRamp? ramp);
    bool TryWrite(DisplayInfo display, GammaRamp ramp);
}

public interface IOverlayBackend
{
    // Zero opacity creates or validates the window without making it visible.
    bool TrySetOpacity(DisplayInfo display, byte opacity);
    void Remove(string displayId);
}

public enum EffectMechanism { Neutral, Gamma, Overlay, Hybrid, Unavailable }

public enum EffectDiagnostic
{
    Neutral,
    RestoreUnverified,
    HdrUnsupported,
    SdrUnconfirmed,
    RecoveryUnsupported,
    JournalReadFailed,
    BaselineReadFailed,
    ReadbackUnavailable,
    CorrectionLimitedByBaseline,
    ReadbackConfirmed,
    ReadbackConfirmedLimited,
    JournalWriteFailed,
    PostWriteReadbackUnavailable,
    ConfirmedRampSaveFailed,
    ChangeUnverifiedWriteSuspended,
    ChangeUnverifiedRestoreDeferred,
    ExternalRampChange,
    DimmingSuspended,
    OverlayUnavailable,
    OverlayOnly,
}

public sealed record DisplayEffectResult(
    EffectMechanism Mechanism, EffectDiagnostic Status, byte OverlayOpacity, bool GammaApplied,
    EffectDiagnostic? AdditionalDiagnostic = null)
{
    public bool CanPreviewExtreme => OverlayOpacity > 0;
}
