using System.Runtime.InteropServices;
using NightDim.Core;

namespace NightDim.Windows;

public sealed class GdiGammaTransport : IGammaTransport
{
    public bool TryRead(DisplayInfo display, out GammaRamp? ramp) => TryRead(Resolve(display), out ramp);

    internal static bool TryReadUnverified(DisplayInfo display, out GammaRamp? ramp) =>
        TryRead(display.DeviceName, out ramp);

    public bool TryWrite(DisplayInfo display, GammaRamp ramp)
    {
        var device = Resolve(display);
        if (device is null)
            return false;

        var dc = CreateDisplayDc(device, device, null, 0);
        if (dc == 0)
            return false;

        try
        {
            return SetDeviceGammaRamp(dc, ramp.Values.ToArray());
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    private static bool TryRead(string? device, out GammaRamp? ramp)
    {
        ramp = null;
        if (device is null)
            return false;

        var dc = CreateDisplayDc(device, device, null, 0);
        if (dc == 0)
            return false;

        try
        {
            var values = new ushort[3 * 256];
            if (!GetDeviceGammaRamp(dc, values))
                return false;

            ramp = new GammaRamp(values);
            return true;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    private static string? Resolve(DisplayInfo display) => display.CanRecoverGamma
        ? DisplayManager.ResolveGammaDevice(display.Id)
        : null;

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateDisplayDc(string driver, string device, string? port, nint initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(nint dc, [Out] ushort[] values);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(nint dc, [In] ushort[] values);
}
