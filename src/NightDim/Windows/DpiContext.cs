using System;
using System.Runtime.InteropServices;

namespace NightDim.Windows;

internal readonly struct DpiContext : IDisposable
{
    private readonly nint _previous;

    private DpiContext(nint previous) => _previous = previous;

    public static DpiContext PerMonitorV2() => new(SetThreadDpiAwarenessContext(-4));

    public void Dispose()
    {
        if (_previous != 0)
            SetThreadDpiAwarenessContext(_previous);
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
