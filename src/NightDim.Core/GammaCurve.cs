namespace NightDim.Core;

public static class GammaCurve
{
    // These relative white points are approximate screen RGB coordinates.
    // Linear interpolation keeps the control continuous and 6500 K neutral.
    private static readonly (int Kelvin, double Red, double Green, double Blue)[] WhitePoints =
    [
        (2000, 1, 0.54, 0.06),
        (2700, 1, 0.65, 0.33),
        (3200, 1, 0.73, 0.48),
        (4000, 1, 0.82, 0.65),
        (5000, 1, 0.90, 0.82),
        (6500, 1, 1, 1),
        (8000, 0.87, 0.92, 1)
    ];

    public static GammaRamp Build(GammaRamp baseline, EffectSettings settings, double brightnessGain)
    {
        settings = settings.Normalize();
        brightnessGain = double.IsFinite(brightnessGain) ? Math.Clamp(brightnessGain, 0, 1) : 1;
        if (settings with { Brightness = 100 } == EffectSettings.Neutral && brightnessGain == 1)
            return baseline;

        var (red, green, blue) = WhitePoint(settings.Temperature);
        Span<double> gains = [red * settings.Red, green * settings.Green,
            blue * settings.Blue * (1 - settings.BlueReduction / 100.0)];
        ReadOnlySpan<ushort> source = baseline.Values;
        Span<ushort> values = stackalloc ushort[GammaRamp.Size];

        for (int channel = 0; channel < gains.Length; channel++)
        {
            int start = channel * GammaRamp.ChannelSize;
            for (int input = 0; input < GammaRamp.ChannelSize; input++)
            {
                double corrected = Math.Pow(input / 255.0, 1 / settings.Gamma);
                double index = corrected * brightnessGain * gains[channel] * 255;
                int lower = (int)index;
                int upper = Math.Min(lower + 1, 255);
                double weight = index - lower;
                values[start + input] = (ushort)Math.Round(
                    source[start + lower] * (1 - weight) + source[start + upper] * weight);
            }
        }

        return new GammaRamp(values);
    }

    internal static GammaRamp ConstrainForGdi(GammaRamp requested, GammaRamp baseline, out bool constrained)
    {
        const int MaximumDeviation = 32000;
        const double MinimumMiddleGray = 0.12 * ushort.MaxValue;
        Span<ushort> values = stackalloc ushort[GammaRamp.Size];
        ReadOnlySpan<ushort> original = baseline.Values;
        double black = WeightedGray(original, 0);
        double baselineMid = WeightedGray(original, 128) - black;
        double requiredMid = Math.Min(baselineMid, MinimumMiddleGray);
        constrained = false;

        for (int index = 0; index < values.Length; index++)
        {
            int identity = GammaRamp.Identity.Values[index];
            // Do not pull a calibrated baseline toward identity merely to add a margin.
            int lower = Math.Min(original[index], Math.Max(0, identity - MaximumDeviation));
            int upper = Math.Max(original[index], Math.Min(ushort.MaxValue, identity + MaximumDeviation));
            ushort target = requested.Values[index];
            values[index] = (ushort)Math.Clamp((int)target, lower, upper);
            constrained |= target != values[index];
        }

        double targetMid = WeightedGray(values, 128) - black;
        if (targetMid < requiredMid)
        {
            constrained = true;
            double retain = (baselineMid - requiredMid) / (baselineMid - targetMid);
            for (int index = 0; index < values.Length; index++)
                values[index] = (ushort)Math.Round(original[index] + (values[index] - original[index]) * retain);
        }

        return new GammaRamp(values);
    }

    private static double WeightedGray(ReadOnlySpan<ushort> values, int input) =>
        0.2126 * values[input] + 0.7152 * values[GammaRamp.ChannelSize + input] +
        0.0722 * values[2 * GammaRamp.ChannelSize + input];

    private static (double Red, double Green, double Blue) WhitePoint(int kelvin)
    {
        for (int index = 1; index < WhitePoints.Length; index++)
        {
            var upper = WhitePoints[index];
            if (kelvin > upper.Kelvin)
                continue;

            var lower = WhitePoints[index - 1];
            double position = (kelvin - lower.Kelvin) / (double)(upper.Kelvin - lower.Kelvin);
            return (lower.Red + (upper.Red - lower.Red) * position,
                lower.Green + (upper.Green - lower.Green) * position,
                lower.Blue + (upper.Blue - lower.Blue) * position);
        }

        return (1, 1, 1);
    }
}
