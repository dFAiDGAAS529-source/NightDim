using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NightDim.Core;

namespace NightDim.Windows;

public sealed class OverlayBackend : IOverlayBackend, IDisposable
{
    private const string ClassName = "NightDim.DisplayOverlay";
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint LwaAlpha = 2;
    private const int BlackBrush = 4;
    private const int GwlExStyle = -20;

    private static readonly WindowProc WindowProcedure = ProcessWindowMessage;
    private static readonly Lazy<bool> ClassRegistered = new(RegisterOverlayClass);
    private readonly Dictionary<string, OverlayWindow> _windows = new(StringComparer.Ordinal);

    // Create, position, and destroy HWNDs on the same dispatcher thread.
    public bool TrySetOpacity(DisplayInfo display, byte opacity)
    {
        if (display.Width <= 0 || display.Height <= 0 || opacity == byte.MaxValue || !ClassRegistered.Value)
        {
            Remove(display.Id);
            return false;
        }

        using var dpi = DpiContext.PerMonitorV2();
        var bounds = new NativeRect(display.X, display.Y, display.X + display.Width, display.Y + display.Height);
        if (!_windows.TryGetValue(display.Id, out var window))
        {
            var styles = WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
            var hwnd = CreateWindowEx(styles, ClassName, "", WsPopup, display.X, display.Y,
                display.Width, display.Height, 0, 0, GetModuleHandle(null), 0);
            if (hwnd == 0)
                return false;

            window = new OverlayWindow(hwnd);
            _windows.Add(display.Id, window);
        }

        if (window.Opacity != opacity && !SetLayeredWindowAttributes(window.Handle, 0, opacity, LwaAlpha))
        {
            Remove(display.Id);
            return false;
        }

        var visible = opacity != 0;
        if (window.Bounds != bounds || window.Visible != visible)
        {
            var flags = SwpNoActivate | SwpNoOwnerZOrder | (visible ? SwpShowWindow : SwpHideWindow);
            if (!SetWindowPos(window.Handle, -1, display.X, display.Y, display.Width, display.Height, flags))
            {
                Remove(display.Id);
                return false;
            }
        }

        window.Opacity = opacity;
        window.Visible = visible;
        window.Bounds = bounds;
        return true;
    }

    internal OverlayInspection? Inspect(string displayId)
    {
        if (!_windows.TryGetValue(displayId, out var window))
            return null;

        using var dpi = DpiContext.PerMonitorV2();
        var alphaRead = GetLayeredWindowAttributes(window.Handle, out _, out var alpha, out var flags);
        var affinityRead = GetWindowDisplayAffinity(window.Handle, out var affinity);
        var style = unchecked((uint)GetWindowLong(window.Handle, GwlExStyle));
        GetWindowRect(window.Handle, out var rect);
        return new OverlayInspection(window.Handle, alphaRead, alpha, flags, affinityRead, affinity, style,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public void Remove(string displayId)
    {
        if (_windows.Remove(displayId, out var window))
            DestroyWindow(window.Handle);
    }

    public void Clear()
    {
        foreach (var window in _windows.Values)
            DestroyWindow(window.Handle);
        _windows.Clear();
    }

    public void Dispose() => Clear();

    private static bool RegisterOverlayClass()
    {
        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            Instance = GetModuleHandle(null),
            Background = GetStockObject(BlackBrush),
            ClassName = ClassName
        };
        return RegisterClassEx(ref windowClass) != 0;
    }

    private static nint ProcessWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        const uint wmPaint = 0x000F;
        const uint wmNcHitTest = 0x0084;
        const uint wmMouseActivate = 0x0021;
        const int htTransparent = -1;
        const int maNoActivate = 3;

        switch (message)
        {
            case wmNcHitTest:
                return htTransparent;
            case wmMouseActivate:
                return maNoActivate;
            case wmPaint:
                var dc = BeginPaint(window, out var paint);
                FillRect(dc, ref paint.PaintBounds, GetStockObject(BlackBrush));
                EndPaint(window, ref paint);
                return 0;
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private sealed class OverlayWindow(nint handle)
    {
        public nint Handle { get; } = handle;
        public byte? Opacity { get; set; }
        public NativeRect Bounds { get; set; }
        public bool Visible { get; set; }
    }

    internal readonly record struct OverlayInspection(nint Hwnd, bool AlphaRead, byte Alpha, uint LayeredFlags,
        bool AffinityRead, uint Affinity, uint ExtendedStyle, int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    // PAINTSTRUCT ends in BYTE rgbReserved[32]. Keep that storage inline and blittable.
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint Dc;
        public int Erase;
        public NativeRect PaintBounds;
        public int Restore;
        public int IncrementalUpdate;
        private uint _reserved0, _reserved1, _reserved2, _reserved3, _reserved4, _reserved5, _reserved6, _reserved7;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint creationData);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint window, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);
}
