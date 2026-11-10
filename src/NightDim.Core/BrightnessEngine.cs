namespace NightDim.Core;

// Call from a serial coordinator. Display topology changes and effect writes share this ordering.
public sealed class BrightnessEngine(IGammaTransport gamma, IOverlayBackend overlay, IGammaJournal journal)
{
    private const double GammaFloor = 0.80;
    private const int MaximumOpacity = 252;
    private readonly GammaRampBackend _gamma = new(gamma, journal);
    private readonly Dictionary<string, byte> _opacities = new(StringComparer.Ordinal);

    public DisplayEffectResult Apply(DisplayInfo display, EffectSettings settings)
    {
        settings = settings.Normalize();
        if (settings == EffectSettings.Neutral)
        {
            bool restored = Restore(display);
            return new DisplayEffectResult(restored ? EffectMechanism.Neutral : EffectMechanism.Unavailable,
                restored ? EffectDiagnostic.Neutral : EffectDiagnostic.RestoreUnverified, 0, false);
        }

        // A direct 1% alpha residual could leave only 2–3 distinguishable encoded values.
        // Shape the relative slider to retain more SDR code levels at the dark end.
        double target = Math.Pow(settings.Brightness / 100.0, 0.60);
        double gammaGain = Math.Max(GammaFloor, target);
        (bool gammaApplied, EffectDiagnostic status) = _gamma.Apply(display, settings, gammaGain);
        if (settings.Brightness <= 10 && !gammaApplied && _gamma.HasPendingOwnership(display.Id))
        {
            RemoveOverlay(display.Id);
            return new DisplayEffectResult(EffectMechanism.Unavailable,
                status, 0, false, EffectDiagnostic.ExtremeDimmingSuspended);
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
        return new DisplayEffectResult(gammaApplied ? EffectMechanism.Gamma : EffectMechanism.Unavailable,
            status, 0, gammaApplied, additionalDiagnostic);
    }

    public bool ProbeOverlay(DisplayInfo display) => overlay.TrySetOpacity(display, _opacities.GetValueOrDefault(display.Id));

    public bool Restore(DisplayInfo display)
    {
        RemoveOverlay(display.Id);
        return _gamma.Restore(display);
    }

    public bool Recover(DisplayInfo display) => _gamma.Recover(display);

    public void Disconnect(string displayId)
    {
        RemoveOverlay(displayId);
        _gamma.Disconnect(displayId);
    }

    private void RemoveOverlay(string displayId)
    {
        _opacities.Remove(displayId);
        overlay.Remove(displayId);
    }
}
