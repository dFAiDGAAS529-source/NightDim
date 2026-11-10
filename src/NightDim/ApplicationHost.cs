using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NightDim.Core;
using NightDim.Localization;
using NightDim.Windows;
using Forms = System.Windows.Forms;

namespace NightDim;

internal sealed class ApplicationHost : IDisposable
{
    private const int ExtremeBrightness = 10;
    private const int PreviewSeconds = 15;

    private readonly Application _app;
    private readonly MainWindow _window;
    private readonly DisplayManager _displayManager = new();
    private readonly ProfileManager _profiles;
    private readonly StartupRegistration _startup = new(Environment.ProcessPath!);
    private readonly EffectWorker _worker;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _topologyTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly Dictionary<string, int> _approvedMinimum = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _selectedProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScreenResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _messages;
    private HotKeys? _keys;
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _toggleMenu;
    private Preferences _committed;
    private Preferences _draft;
    private IReadOnlyList<DisplayInfo> _displays = [];
    private long _previewDeadline;
    private bool _previewReady;
    private bool _ending;
    private bool _suspended;
    private bool _refreshAfterResume;
    private bool _started;
    private bool _wtsRegistered;
    private int _revision;
    private UiMessage? _notice;

    internal ApplicationHost(Application app, string directory)
    {
        _app = app;
        _profiles = new ProfileManager(Path.Combine(directory, "settings.json"));
        _committed = _profiles.Load();
        _draft = _committed;
        UiText.Apply(app, _committed.Language);
        _window = new MainWindow();
        _worker = new EffectWorker(new FileGammaJournal(Path.Combine(directory, "gamma-recovery")));
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(110) };
        _topologyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _previewTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await ApplyDraftAsync(allowPreview: true);
        };
        _topologyTimer.Tick += async (_, _) =>
        {
            _topologyTimer.Stop();
            await RefreshDisplaysAsync();
        };
        _previewTimer.Tick += (_, _) => UpdatePreview();
    }

    internal async Task StartAsync(bool show = true)
    {
        _messages = new HwndSource(new HwndSourceParameters("NightDim events")
        {
            WindowStyle = unchecked((int)0x80000000),
            Width = 0,
            Height = 0,
        });
        _messages.AddHook(WindowMessage);
        _keys = new HotKeys(_messages);
        _wtsRegistered = WTSRegisterSessionNotification(_messages.Handle, 0);
        CreateTray();
        BindWindow();
        _window.SetStartupState(_startup.Read());
        _displayManager.DisplaysChanged += (_, _) => QueueRefresh();
        _app.MainWindow = _window;
        await RefreshDisplaysAsync();
        _started = true;
        if (show)
            ShowWindow();
    }

    private void BindWindow()
    {
        _window.Closing += HideToTray;
        _window.DpiChanged += (_, _) => QueueRefresh();
        _window.Activated += (_, _) => QueueRefresh();
        _window.EffectToggled += SetEnabled;
        _window.SynchronizeToggled += synchronize => Change(preferences =>
        {
            if (synchronize)
                preferences.Synchronized = SelectedSettings(preferences);
            preferences.Synchronize = synchronize;
        });
        _window.DisplaySelected += id =>
        {
            if (InPreview || _ending)
                return;
            var next = Copy(_draft);
            next.SelectedDisplayId = id;
            ChangeDraft(next, render: true);
        };
        _window.ProfileSelected += id =>
        {
            if (SelectedDisplay() is not { } selected)
                return;
            _selectedProfiles[ProfileScope()] = id;
            Change(preferences => SetSettings(preferences, selected.Id, preferences.Profiles[id]), render: true);
        };
        _window.ProfileSaved += id =>
        {
            if (id == "neutral" || InPreview || _ending)
                return;
            Change(preferences => preferences.Profiles[id] = SelectedSettings(preferences), render: true);
        };
        _window.SettingsChanged += settings =>
        {
            if (SelectedDisplay() is { } selected)
                Change(preferences => SetSettings(preferences, selected.Id, settings));
        };
        _window.ResetRequested += () =>
        {
            if (SelectedDisplay() is not { } selected)
                return;
            _selectedProfiles[ProfileScope()] = "neutral";
            Change(preferences => SetSettings(preferences, selected.Id, EffectSettings.Neutral), render: true);
        };
        _window.PreviewAnswered += keep => _ = AnswerPreviewAsync(keep);
        _window.ExitRequested += () => _ = ExitAsync();
        _window.LanguageChanged += SetLanguage;
        _window.StartupChanged += SetStartup;
    }

    private void SetLanguage(string language)
    {
        if (_ending || InPreview || language == UiText.Language)
            return;

        UiText.Apply(_app, language);
        _window.RefreshLanguage();
        _window.SetDisplays(_displays);
        if (_trayMenu is not null)
        {
            foreach (Forms.ToolStripItem item in _trayMenu.Items)
            {
                if (item.Tag is string key)
                    item.Text = UiText.Get(key);
            }
        }
        _committed.Language = UiText.Language;
        if (!_profiles.Save(_committed))
            _notice = new("SaveFailed");
        Render();
    }

    private void SetStartup(bool enable)
    {
        if (!_startup.Set(enable))
            _notice = new("StartupError");
        _window.SetStartupState(_startup.Read());
        Render();
    }

    private void CreateTray()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add(TrayItem("TrayOpen", ShowWindow));
        _toggleMenu = TrayItem("TrayEffect", () => SetEnabled(!_draft.Enabled));
        _trayMenu.Items.Add(_toggleMenu);
        _trayMenu.Items.Add(TrayItem("TrayRestore", () => _ = EmergencyAsync()));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(TrayItem("TrayExit", () => _ = ExitAsync()));
        _trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _tray = new Forms.NotifyIcon
        {
            Text = "NightDim",
            Icon = _trayIcon ?? SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private static Forms.ToolStripMenuItem TrayItem(string key, Action click) =>
        new(UiText.Get(key), null, (_, _) => click()) { Tag = key };

    private void ShowWindow()
    {
        if (_ending)
            return;
        if (!_window.IsVisible)
            _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void HideToTray(object? sender, CancelEventArgs e)
    {
        if (_ending)
            return;
        e.Cancel = true;
        if (InPreview)
            _ = AnswerPreviewAsync(false);
        _window.Hide();
    }

    private void Change(Action<Preferences> change, bool render = false)
    {
        if (InPreview || _ending || _suspended)
            return;
        var next = Copy(_draft);
        change(next);
        ChangeDraft(next, render);
    }

    private void ChangeDraft(Preferences next, bool render)
    {
        Interlocked.Increment(ref _revision);
        _notice = null;
        _draft = next;
        if (render)
            Render();
        _debounce.Stop();
        _debounce.Start();
    }

    private void SetEnabled(bool enable)
    {
        if (!enable && InPreview)
        {
            _ = EmergencyAsync();
            return;
        }
        Change(preferences => preferences.Enabled = enable, render: true);
    }

    private void StepBrightness(int delta)
    {
        if (SelectedDisplay() is not { } selected)
            return;
        Change(preferences =>
        {
            var settings = SettingsFor(preferences, selected.Id);
            SetSettings(preferences, selected.Id, settings with
            {
                Brightness = Math.Clamp(settings.Brightness + delta, 1, 100),
            });
        }, render: true);
    }

    private void QueueRefresh()
    {
        if (_ending || _suspended)
            return;
        _topologyTimer.Stop();
        _topologyTimer.Start();
    }

    private async Task RefreshDisplaysAsync()
    {
        if (_ending || _suspended)
            return;
        try
        {
            var displays = _displayManager.Enumerate()
                .OrderByDescending(display => display.IsPrimary)
                .ThenBy(display => display.DeviceName, StringComparer.Ordinal)
                .ToArray();
            if (_started && !_refreshAfterResume && _displays.SequenceEqual(displays))
                return;
            _refreshAfterResume = false;
            _debounce.Stop();
            Interlocked.Increment(ref _revision);
            _approvedMinimum.Clear();
            EndPreview();
            _displays = displays;
            _draft = Copy(_committed);
            if (!displays.Any(display => display.Id == _draft.SelectedDisplayId))
                _draft.SelectedDisplayId = displays.FirstOrDefault()?.Id;
            if (UnapprovedDisplays(_draft).Length > 0)
            {
                _draft.Enabled = false;
                _notice = new("LowRecheck");
            }
            _window.SetDisplays(_displays);
            Render();
            await ApplyDraftAsync(allowPreview: false, urgent: true);
        }
        catch (Exception ex)
        {
            await EmergencyAsync(new UiMessage("DisplaysRefreshFailed", ex.Message));
        }
    }

    private async Task ApplyDraftAsync(bool allowPreview, bool urgent = false)
    {
        if (_ending || _suspended)
            return;
        var revision = Interlocked.Increment(ref _revision);
        var requested = _draft;
        var displays = _displays;
        var low = UnapprovedDisplays(requested);
        var preview = low.Length > 0;
        if (preview && (!allowPreview || _keys?.HasRestore != true))
        {
            _selectedProfiles.Remove(ProfileScope());
            _draft = _committed;
            if (UnapprovedDisplays(_draft).Length > 0)
            {
                _draft = Copy(_committed);
                _draft.Enabled = false;
            }
            _notice = new("LowNeedsHotkey");
            Render();
            // A previous debounced update may have been interrupted between monitors.
            await ApplyDraftAsync(allowPreview: false, urgent: true);
            return;
        }

        if (preview)
        {
            _previewDeadline = Environment.TickCount64 + PreviewSeconds * 1000;
            _previewReady = false;
            UpdatePreview();
            _previewTimer.Start();
            ShowWindow();
        }

        try
        {
            var result = await _worker.InvokeAsync(context =>
            {
                if (revision != Volatile.Read(ref _revision))
                    return null;
                context.UpdateDisplays(displays);
                if (!requested.Enabled)
                    return new ApplyResult([], context.RestoreAll(), true);

                var compatible = low.All(display => display.Width > 0 && display.Height > 0
                    && context.Engine.ProbeOverlay(display));
                if (!compatible)
                    return new ApplyResult([], true, false);

                var applied = new List<ScreenResult>();
                foreach (var display in displays)
                {
                    if (revision != Volatile.Read(ref _revision))
                        return null;
                    var effect = context.Engine.Apply(display, SettingsFor(requested, display.Id));
                    applied.Add(new ScreenResult(display.Id, effect, context.Overlay.CaptureExcluded(display.Id)));
                    if (low.Any(item => item.Id == display.Id) && !effect.CanPreviewExtreme)
                        return new ApplyResult(applied, true, false);
                }
                return new ApplyResult(applied, true, true);
            }, urgent);

            if (revision != Volatile.Read(ref _revision) || result is null)
                return;
            if (!result.Compatible)
            {
                await AnswerPreviewAsync(false, new UiMessage("PreviewUnavailable"));
                return;
            }

            _results.Clear();
            foreach (var item in result.Screens)
                _results[item.Id] = item;
            if (!result.Restored)
                _notice = new("RestoreJournalKept");

            if (preview)
            {
                _previewReady = true;
                UpdatePreview();
            }
            else
            {
                Commit(requested);
            }
            Render();
        }
        catch (Exception ex)
        {
            if (revision == Volatile.Read(ref _revision))
                await EmergencyAsync(new UiMessage("DisplayControlFailed", ex.Message));
        }
    }

    private DisplayInfo[] UnapprovedDisplays(Preferences preferences) => !preferences.Enabled ? [] :
        _displays.Where(display =>
        {
            var brightness = SettingsFor(preferences, display.Id).Brightness;
            return brightness <= ExtremeBrightness
                && (!_approvedMinimum.TryGetValue(display.Id, out var minimum) || brightness < minimum);
        }).ToArray();

    private bool InPreview => _previewDeadline != 0;

    private void UpdatePreview()
    {
        if (!InPreview)
            return;
        var remaining = _previewDeadline - Environment.TickCount64;
        if (remaining <= 0)
        {
            _ = AnswerPreviewAsync(false, new UiMessage("PreviewTimedOut"));
            return;
        }
        _window.SetPreview((int)((remaining + 999) / 1000), _previewReady);
    }

    private async Task AnswerPreviewAsync(bool keep, UiMessage? notice = null)
    {
        if (!InPreview || _ending)
            return;
        if (keep && _previewReady && Environment.TickCount64 < _previewDeadline)
        {
            foreach (var display in UnapprovedDisplays(_draft))
                _approvedMinimum[display.Id] = SettingsFor(_draft, display.Id).Brightness;
            EndPreview();
            Commit(_draft);
            Render();
            return;
        }

        Interlocked.Increment(ref _revision);
        EndPreview();
        _selectedProfiles.Remove(ProfileScope());
        _draft = _committed;
        _notice = notice ?? new UiMessage("PreviewReverted");
        Render();
        await ApplyDraftAsync(allowPreview: false, urgent: true);
    }

    private void EndPreview()
    {
        _previewTimer.Stop();
        _previewDeadline = 0;
        _previewReady = false;
        _window.SetPreview(null);
    }

    private void Commit(Preferences preferences)
    {
        if (ReferenceEquals(_committed, preferences))
            return;
        preferences.Language = UiText.Language;
        _committed = preferences;
        if (!_profiles.Save(preferences))
            _notice = new("SaveFailed");
    }

    private async Task EmergencyAsync(UiMessage? notice = null)
    {
        if (_ending)
            return;
        var revision = Interlocked.Increment(ref _revision);
        _debounce.Stop();
        EndPreview();
        _approvedMinimum.Clear();
        _draft = Copy(_committed);
        _draft.Enabled = false;
        _results.Clear();
        _notice = notice ?? new UiMessage("EmergencyRestored");
        Render();
        try
        {
            var restoration = _worker.InvokeAsync(context => context.RestoreAll(), urgent: true);
            Commit(_draft);
            var restored = await restoration;
            if (!restored && revision == Volatile.Read(ref _revision))
            {
                _notice = new("OverlayRemovedLutUnconfirmed");
                Render();
            }
        }
        catch (Exception)
        {
            _notice = new("RecoveryNextLaunch");
            Render();
        }
    }

    private void Suspend()
    {
        if (_ending || _suspended)
            return;
        _suspended = true;
        Interlocked.Increment(ref _revision);
        _debounce.Stop();
        _topologyTimer.Stop();
        EndPreview();
        _draft = _committed;
        _results.Clear();
        _ = _worker.InvokeAsync(context => context.RestoreAll(), urgent: true);
    }

    private void Resume()
    {
        _suspended = false;
        _refreshAfterResume = true;
        QueueRefresh();
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        _displayManager.HandleWindowMessage(message, wParam);
        if (message == 0x0312 && _keys?.IsRegistered((HotKey)wParam) == true) // WM_HOTKEY
        {
            handled = true;
            switch ((HotKey)wParam)
            {
                case HotKey.Restore: _ = EmergencyAsync(); break;
                case HotKey.Toggle: SetEnabled(!_draft.Enabled); break;
                case HotKey.Brighter: StepBrightness(1); break;
                case HotKey.Dimmer: StepBrightness(-1); break;
            }
        }
        else if (message == 0x0218) // WM_POWERBROADCAST
        {
            if (wParam == 0x4)
                Suspend();
            else if (wParam is 0x7 or 0x12)
                Resume();
        }
        else if (message == 0x02B1) // WM_WTSSESSION_CHANGE
        {
            if (wParam is 0x2 or 0x4 or 0x7)
                Suspend();
            else if (wParam is 0x1 or 0x3 or 0x8)
                Resume();
        }
        return 0;
    }

    private void Render()
    {
        var display = SelectedDisplay();
        var settings = SelectedSettings(_draft);
        var profile = _selectedProfiles.GetValueOrDefault(ProfileScope()) ??
            _draft.Profiles.FirstOrDefault(pair => pair.Value == settings).Key ?? "custom";
        var profileMatches = _draft.Profiles.TryGetValue(profile, out var saved) && saved == settings;
        _window.ShowSettings(settings, _draft.Enabled, _draft.Synchronize, display?.Id, profile, profileMatches);
        if (_toggleMenu is not null)
            _toggleMenu.Checked = _draft.Enabled;

        var status = UiText.Get("StatusOff");
        var detail = UiText.Get("StatusOffDetail");
        if (display is null)
        {
            status = UiText.Get("NoMonitors");
            detail = UiText.Get("NoMonitorsDetail");
        }
        else if (_draft.Enabled)
        {
            if (_results.TryGetValue(display.Id, out var result))
            {
                status = result.Effect.Mechanism switch
                {
                    EffectMechanism.Hybrid => "Hybrid",
                    EffectMechanism.Gamma => "Gamma",
                    EffectMechanism.Overlay => "Overlay",
                    EffectMechanism.Neutral => UiText.Get("NeutralStatus"),
                    _ => UiText.Get("EffectUnavailable"),
                };
                detail = UiText.Describe(result.Effect);
                if (result.CaptureExcluded == false)
                    detail += " " + UiText.Get("CaptureExclusionDenied");
            }
            else
            {
                status = UiText.Get("CheckingDisplay");
                detail = UiText.Get("CheckingDisplayDetail");
            }
        }
        _window.SetStatus(status, detail, _keys?.Summary ?? "", _notice?.Text ?? "");
    }

    private DisplayInfo? SelectedDisplay() => _displays.FirstOrDefault(display => display.Id == _draft.SelectedDisplayId);
    private EffectSettings SelectedSettings(Preferences preferences) =>
        preferences.SelectedDisplayId is { } id ? SettingsFor(preferences, id) : EffectSettings.Neutral;
    private string ProfileScope() => _draft.Synchronize ? "synchronized" : _draft.SelectedDisplayId ?? "";

    private static EffectSettings SettingsFor(Preferences preferences, string id) => preferences.Synchronize
        ? preferences.Synchronized : preferences.Displays.GetValueOrDefault(id, EffectSettings.Neutral);

    private static void SetSettings(Preferences preferences, string id, EffectSettings settings)
    {
        if (preferences.Synchronize)
            preferences.Synchronized = settings.Normalize();
        else
            preferences.Displays[id] = settings.Normalize();
    }

    private static Preferences Copy(Preferences preferences) => new()
    {
        Enabled = preferences.Enabled,
        Language = preferences.Language,
        Synchronize = preferences.Synchronize,
        SelectedDisplayId = preferences.SelectedDisplayId,
        Synchronized = preferences.Synchronized,
        Displays = new(preferences.Displays, StringComparer.OrdinalIgnoreCase),
        Profiles = new(preferences.Profiles, StringComparer.OrdinalIgnoreCase),
    };

    internal async Task ExitAsync()
    {
        if (_ending)
            return;
        BeginExit();
        try
        {
            await _worker.InvokeAsync(context => context.RestoreAll(), urgent: true).WaitAsync(TimeSpan.FromSeconds(3));
            await _worker.StopAsync();
        }
        catch (Exception)
        {
            // The write-ahead gamma journal remains available on the next launch.
        }
        _app.Shutdown();
    }

    internal void EndSession()
    {
        if (_ending)
            return;
        BeginExit();
        try
        {
            _worker.InvokeAsync(context => context.RestoreAll(), urgent: true).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Windows may end the session before the driver returns.
        }
    }

    private void BeginExit()
    {
        _ending = true;
        Interlocked.Increment(ref _revision);
        _debounce.Stop();
        _topologyTimer.Stop();
        EndPreview();
    }

    public void Dispose()
    {
        EndSession();
        _keys?.Dispose();
        if (_wtsRegistered && _messages is not null)
            WTSUnRegisterSessionNotification(_messages.Handle);
        _messages?.Dispose();
        _tray?.Dispose();
        _trayMenu?.Dispose();
        _trayIcon?.Dispose();
    }

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint window);

    private sealed record ScreenResult(string Id, DisplayEffectResult Effect, bool? CaptureExcluded);
    private sealed record ApplyResult(List<ScreenResult> Screens, bool Restored, bool Compatible);
}
