namespace NightDim.Core;

public sealed class GammaRamp
{
    public const int ChannelSize = 256;
    public const int Size = 3 * ChannelSize;
    private readonly ushort[] _values;

    public GammaRamp(ReadOnlySpan<ushort> values)
    {
        if (values.Length != Size)
            throw new ArgumentException("A gamma ramp must have 256 entries per RGB channel.", nameof(values));
        _values = values.ToArray();
    }

    public ReadOnlySpan<ushort> Values => _values;

    public static GammaRamp Identity { get; } = CreateIdentity();

    public bool Matches(GammaRamp other) => Values.SequenceEqual(other.Values);

    internal bool WithinOneByteStep(GammaRamp other)
    {
        for (int index = 0; index < Size; index++)
        {
            if (Math.Abs(_values[index] - other._values[index]) > 257)
                return false;
        }

        return true;
    }

    internal bool IsRestorable()
    {
        for (int index = 0; index < Size; index++)
        {
            int input = index % ChannelSize;
            if (Math.Abs(_values[index] - Identity._values[index]) > 32768)
                return false;
            if (input > 0 && _values[index] < _values[index - 1])
                return false;
        }

        return true;
    }

    private static GammaRamp CreateIdentity()
    {
        Span<ushort> identity = stackalloc ushort[Size];
        for (int index = 0; index < identity.Length; index++)
            identity[index] = (ushort)((index % ChannelSize) * 257);
        return new GammaRamp(identity);
    }
}
