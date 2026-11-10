using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NightDim.Localization;

namespace NightDim;

internal enum HotKey { Restore = 1, Toggle, Brighter, Dimmer }

internal sealed class HotKeys : IDisposable
{
    private const uint ControlAlt = 0x0003;
    private const uint ControlShift = 0x0006;
    private const uint NoRepeat = 0x4000;
    private readonly HashSet<HotKey> _registered = [];
    private readonly nint _window;
    private readonly string? _restore;

    internal HotKeys(HwndSource source)
    {
        _window = source.Handle;
        if (Register(HotKey.Restore, ControlAlt, 0x79))
            _restore = "Ctrl+Alt+F10";
        else if (Register(HotKey.Restore, ControlShift, 0x79))
            _restore = "Ctrl+Shift+F10";
        Register(HotKey.Toggle, ControlAlt, 0x78);
        Register(HotKey.Brighter, ControlAlt, 0x21);
        Register(HotKey.Dimmer, ControlAlt, 0x22);
    }

    internal bool HasRestore => _restore is not null;

    internal string Summary
    {
        get
        {
            var keys = new List<string>
            {
                _restore is null ? UiText.Get("RestoreShortcutUnavailable") : UiText.Format("RestoreShortcut", _restore),
            };
            if (_registered.Contains(HotKey.Brighter) && _registered.Contains(HotKey.Dimmer))
                keys.Add("±1% Ctrl+Alt+PgUp/PgDn");
            else if (_registered.Contains(HotKey.Brighter))
                keys.Add("+1% Ctrl+Alt+PgUp");
            else if (_registered.Contains(HotKey.Dimmer))
                keys.Add("−1% Ctrl+Alt+PgDn");
            if (_registered.Contains(HotKey.Toggle))
                keys.Add(UiText.Get("ToggleShortcut"));
            return string.Join("  ·  ", keys);
        }
    }

    internal bool IsRegistered(HotKey key) => _registered.Contains(key);

    private bool Register(HotKey key, uint modifiers, uint virtualKey)
    {
        if (!RegisterHotKey(_window, (int)key, modifiers | NoRepeat, virtualKey))
            return false;
        return _registered.Add(key);
    }

    public void Dispose()
    {
        foreach (var key in _registered)
            UnregisterHotKey(_window, (int)key);
        _registered.Clear();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
