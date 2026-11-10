namespace NightDim.Core;

public sealed record EffectSettings(
    int Brightness = 100,
    int Temperature = 6500,
    double Gamma = 1,
    int BlueReduction = 0,
    double Red = 1,
    double Green = 1,
    double Blue = 1)
{
    public static EffectSettings Neutral { get; } = new();

    public EffectSettings Normalize() => new(
        Math.Clamp(Brightness, 1, 100), Math.Clamp(Temperature, 2000, 8000),
        ClampFinite(Gamma, 0.5, 2), Math.Clamp(BlueReduction, 0, 100),
        ClampFinite(Red, 0, 1), ClampFinite(Green, 0, 1), ClampFinite(Blue, 0, 1));

    private static double ClampFinite(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : 1;
}
