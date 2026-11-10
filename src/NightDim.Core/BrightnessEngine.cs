namespace NightDim.Core;

// Call from a serial coordinator. Display topology changes and effect writes share this ordering.
public sealed class BrightnessEngine(IGammaTransport gamma, IOverlayBackend overlay, IGammaJournal journal)
{
    public const int MinimumGammaOnlyBrightness = 43;
    private const double GammaFloor = 0.80;
    private const int MaximumOpacity = 252;
    private readonly GammaRampBackend _gamma = new(gamma, journal);
    private readonly Dictionary<string, byte> _opacities = new(StringComparer.Ordinal);

    public DisplayEffectResult Apply(DisplayInfo display, EffectSettings settings, DimmingMode mode = DimmingMode.Hybrid)
    {
        settings = settings.Normalize();
        var overlayOnly = mode == DimmingMode.OverlayOnly;
        if (!overlayOnly && settings == EffectSettings.Neutral)
        {
            bool restored = Restore(display);
            return new DisplayEffectResult(restored ? EffectMechanism.Neutral : EffectMechanism.Unavailable,
                restored ? EffectDiagnostic.Neutral : EffectDiagnostic.RestoreUnverified, 0, false);
        }
        if (mode == DimmingMode.GammaOnly)
            return ApplyGammaOnly(display, settings);

        double target = TargetForBrightness(settings.Brightness);
        double gammaGain = Math.Max(GammaFloor, target);
        (bool gammaApplied, EffectDiagnostic status) = overlayOnly
            ? (false, EffectDiagnostic.OverlayOnly)
            : _gamma.Apply(display, settings, gammaGain);
        if (!gammaApplied && !_gamma.TryReleaseOwnership(display))
        {
            // An uncompensated overlay could stack with our last LUT and dim the screen twice.
            RemoveOverlay(display.Id);
            return new DisplayEffectResult(EffectMechanism.Unavailable,
                status, 0, false, EffectDiagnostic.DimmingSuspended);
        }

        double residual = gammaApplied ? target / gammaGain : target;
        byte opacity = (byte)Math.Clamp((int)Math.Round(255 * (1 - residual)), 0, MaximumOpacity);
        EffectDiagnostic? additionalDiagnostic = null;
        if (opacity > 0)
        {
            if (overlay.TrySetOpacity(display, opacity))
            {
                _opacities[display.Id] = opacity;
                return new DisplayEffectResult(gammaApplied ? EffectMechanism.Hybrid : EffectMechanism.Overlay,
                    status, opacity, gammaApplied);
            }

            additionalDiagnostic = EffectDiagnostic.OverlayUnavailable;
        }

        RemoveOverlay(display.Id);
        var mechanism = gammaApplied ? EffectMechanism.Gamma
            : overlayOnly && opacity == 0 ? EffectMechanism.Neutral : EffectMechanism.Unavailable;
        return new DisplayEffectResult(mechanism, status, 0, gammaApplied, additionalDiagnostic);
    }

    private DisplayEffectResult ApplyGammaOnly(DisplayInfo display, EffectSettings settings)
    {
        RemoveOverlay(display.Id);
        int brightness = Math.Max(MinimumGammaOnlyBrightness, settings.Brightness);
        var (applied, status) = _gamma.Apply(display, settings, TargetForBrightness(brightness));
        if (!applied)
        {
            var diagnostic = _gamma.TryReleaseOwnership(display) ? (EffectDiagnostic?)null : EffectDiagnostic.DimmingSuspended;
            return new DisplayEffectResult(EffectMechanism.Unavailable, status, 0, false, diagnostic);
        }

        return new DisplayEffectResult(EffectMechanism.Gamma, status, 0, true,
            brightness != settings.Brightness ? EffectDiagnostic.GammaOnlyLimited : null);
    }

    // A direct 1% alpha residual could leave only 2–3 distinguishable encoded values.
    // Shape the relative slider to retain more SDR code levels at the dark end.
    private static double TargetForBrightness(int brightness) => Math.Pow(brightness / 100.0, 0.60);

    public bool ProbeOverlay(DisplayInfo display) => overlay.TrySetOpacity(display, _opacities.GetValueOrDefault(display.Id));

    public bool Restore(DisplayInfo display)
    {
        RemoveOverlay(display.Id);
        return _gamma.Restore(display);
    }

    public bool Recover(DisplayInfo display) => _gamma.Recover(display);

    public void ForgetGammaState(string displayId) => _gamma.Disconnect(displayId);

    public void Disconnect(string displayId)
    {
        RemoveOverlay(displayId);
        ForgetGammaState(displayId);
    }

    private void RemoveOverlay(string displayId)
    {
        _opacities.Remove(displayId);
        overlay.Remove(displayId);
    }
}
