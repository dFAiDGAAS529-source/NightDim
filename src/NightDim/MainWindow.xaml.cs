using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NightDim.Core;
using NightDim.Localization;
using NightDim.Windows;
using Screen = System.Windows.Forms.Screen;

namespace NightDim;

public partial class MainWindow : Window
{
    private const double DefaultMinWidth = 410;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const uint MoveWithoutResize = 0x0015; // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    private static readonly (string Id, string Key)[] EditableProfiles =
    [
        ("day", "ProfileDay"), ("evening", "ProfileEvening"), ("night", "ProfileNight"), ("custom", "ProfileCustom"),
    ];

    private bool _rendering;
    private bool _hasDisplay;
    private bool _overlayOnly;
    private bool? _startupEnabled;
    private IInputElement? _previousFocus;

    internal event Action<bool>? EffectToggled;
    internal event Action<bool>? SynchronizeToggled;
    internal event Action<string>? DisplaySelected;
    internal event Action<string>? ProfileSelected;
    internal event Action<string>? ProfileSaved;
    internal event Action<EffectSettings>? SettingsChanged;
    internal event Action? ResetRequested;
    internal event Action<bool>? PreviewAnswered;
    internal event Action? ExitRequested;
    internal event Action<string>? LanguageChanged;
    internal event Action<bool>? StartupChanged;
    internal event Action<bool>? OverlayOnlyChanged;

    internal MainWindow()
    {
        InitializeComponent();
        ProfilesBox.SelectedValuePath = nameof(Choice.Id);
        DisplaysBox.SelectedValuePath = nameof(Choice.Id);
        RefreshLanguage();
        LocationChanged += (_, _) => UpdateWorkArea();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(UpdateWorkArea, DispatcherPriority.Loaded);
        SizeChanged += (_, args) =>
        {
            if (SizeToContent == SizeToContent.Height && args.NewSize.Height > args.PreviousSize.Height)
                Dispatcher.BeginInvoke(KeepAutoSizedWindowOnScreen, DispatcherPriority.Loaded);
        };
    }

    internal void SetDisplays(IReadOnlyList<DisplayInfo> displays)
    {
        _rendering = true;
        DisplaysBox.ItemsSource = displays.Select(display =>
        {
            var mode = display.Hdr switch
            {
                HdrState.Hdr => "HDR",
                HdrState.Sdr => "SDR",
                _ => UiText.Get("HdrUnknown"),
            };
            var primary = display.IsPrimary ? " · " + UiText.Get("Primary") : "";
            return new Choice(display.Id, $"{display.Name} · {mode}{primary}");
        }).ToList();
        _rendering = false;
    }

    internal void RefreshLanguage()
    {
        Language = XmlLanguage.GetLanguage(UiText.Culture.IetfLanguageTag);
        EnglishButton.IsChecked = UiText.Language == "en";
        RussianButton.IsChecked = UiText.Language == "ru";
        var target = ProfilesBox.SelectedValue ?? "custom";
        ProfilesBox.ItemsSource = EditableProfiles.Select(profile => new Choice(profile.Id, UiText.Get(profile.Key))).ToArray();
        ProfilesBox.SelectedValue = target;
        UpdateSettingsLabel();
        UpdateFineHelp();
        UpdateValueLabels();
        SetStartupState(_startupEnabled);
    }

    internal void SetStartupState(bool? enabled)
    {
        _startupEnabled = enabled;
        StartupBox.IsChecked = enabled == true;
        StartupBox.IsEnabled = enabled.HasValue;
        StartupBox.ToolTip = UiText.Get(enabled.HasValue ? "StartupTip" : "StartupUnavailable");
    }

    internal void ShowSettings(EffectSettings settings, bool enabled, bool synchronize,
        string? displayId, string profileId, bool profileMatches = true, bool overlayOnly = false)
    {
        _rendering = true;
        _hasDisplay = displayId is not null;
        SetOverlayOnly(overlayOnly);
        EnabledBox.IsChecked = enabled;
        SyncBox.IsChecked = synchronize;
        DisplaysBox.SelectedValue = displayId;
        SaveButton.IsEnabled = _hasDisplay;
        SetProfileHighlight(profileMatches ? profileId : "custom");
        UpdateControlAvailability();
        BrightnessSlider.Value = settings.Brightness;
        TemperatureSlider.Value = settings.Temperature;
        GammaSlider.Value = settings.Gamma;
        BlueReductionSlider.Value = settings.BlueReduction;
        RedSlider.Value = settings.Red;
        GreenSlider.Value = settings.Green;
        BlueSlider.Value = settings.Blue;
        UpdateValueLabels();
        _rendering = false;
    }

    internal void SetStatus(string title, string detail, string keys, string notice)
    {
        StatusLine.Text = title;
        StatusDot.SetResourceReference(Shape.FillProperty,
            title is "Gamma" or "Overlay" or "Hybrid" ? "GlassAccentBrush" : "GlassMutedBrush");
        DetailLine.Text = detail;
        HotKeyLine.Text = keys.Replace("  ·  ", Environment.NewLine, StringComparison.Ordinal);
        EnabledBox.ToolTip = keys;
        NoticeLine.Text = notice;
        NoticeLine.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetPreview(int? seconds, bool canKeep = false)
    {
        var wasVisible = PreviewPanel.Visibility == Visibility.Visible;
        if (seconds.HasValue)
        {
            PreviewText.Text = UiText.Format("PreviewCountdown", seconds.Value);
            if (!wasVisible)
            {
                _previousFocus = Keyboard.FocusedElement;
                SetSettingsOpen(false);
                Reveal(PreviewPanel);
            }
        }
        else
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
        }
        KeepButton.IsEnabled = canKeep;
        UpdateControlAvailability();
        if (seconds.HasValue && !wasVisible)
            CancelButton.Focus();
        else if (!seconds.HasValue && wasVisible)
        {
            if (IsActive && _previousFocus is not null)
                Keyboard.Focus(_previousFocus);
            _previousFocus = null;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateWorkArea();
        if (SystemParameters.HighContrast)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        // Windows 11 caption colors preserve native nonclient behavior and Snap Layouts.
        SetCaptionColor(handle, DwmCaptionColor, (SolidColorBrush)FindResource("GlassBackgroundBrush"));
        SetCaptionColor(handle, DwmTextColor, (SolidColorBrush)FindResource("GlassTextBrush"));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PreviewPanel.Visibility == Visibility.Visible)
        {
            PreviewAnswered?.Invoke(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SettingsPanel.Visibility == Visibility.Visible)
        {
            SetSettingsOpen(false);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void UpdateWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || WindowState == WindowState.Minimized)
            return;

        var area = Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxWidth = area.Width / dpi.DpiScaleX;
        MinWidth = Math.Min(DefaultMinWidth, MaxWidth);
        MaxHeight = Math.Max(MinHeight, area.Height / dpi.DpiScaleY);
    }

    private void KeepAutoSizedWindowOnScreen()
    {
        if (!IsVisible || WindowState != WindowState.Normal || SizeToContent != SizeToContent.Height)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var window))
            return;

        var area = Screen.FromHandle(handle).WorkingArea;
        if (window.Bottom <= area.Bottom)
            return;

        var top = Math.Max(area.Top, area.Bottom - (window.Bottom - window.Top));
        SetWindowPos(handle, 0, window.Left, top, 0, 0, MoveWithoutResize);
    }

    private static void SetCaptionColor(nint handle, int attribute, SolidColorBrush brush)
    {
        var color = brush.Color;
        var colorRef = (uint)(color.R | color.G << 8 | color.B << 16);
        _ = DwmSetWindowAttribute(handle, attribute, in colorRef, sizeof(uint));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in uint value, uint size);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    private void UpdateControlAvailability()
    {
        var inPreview = PreviewPanel.Visibility == Visibility.Visible;
        ControlsPanel.IsEnabled = !inPreview && _hasDisplay;
        EnabledBox.IsEnabled = !inPreview;
        LanguageSelector.IsEnabled = !inPreview;
        ResetButton.IsEnabled = !inPreview && _hasDisplay;
        SettingsButton.IsEnabled = !inPreview;
    }

    private void SetOverlayOnly(bool enabled)
    {
        OverlayOnlyBox.IsChecked = enabled;
        if (_overlayOnly == enabled)
            return;

        _overlayOnly = enabled;
        TemperatureSlider.IsEnabled = !enabled;
        FineButton.IsEnabled = !enabled;
        FinePanel.IsEnabled = !enabled;
        FineButton.SetResourceReference(ToolTipProperty, enabled ? "FixedDimmingColorDisabled" : "FineTuningTip");
        if (enabled)
            TemperatureSlider.SetResourceReference(ToolTipProperty, "FixedDimmingColorDisabled");
        else
            TemperatureSlider.ClearValue(ToolTipProperty);
    }

    private void SetProfileHighlight(string id)
    {
        DayProfile.IsChecked = id == "day";
        EveningProfile.IsChecked = id == "evening";
        NightProfile.IsChecked = id == "night";
        CustomProfile.IsChecked = id == "custom";
        ProfileCaption.Visibility = id == "neutral" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSettingsOpen(bool open)
    {
        if (open == (SettingsPanel.Visibility == Visibility.Visible))
            return;

        ControlsPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (open)
            Reveal(SettingsPanel);
        else
            SettingsPanel.Visibility = Visibility.Collapsed;

        BodyScroll.ScrollToTop();
        UpdateSettingsLabel();
    }

    private void UpdateSettingsLabel()
    {
        var open = SettingsPanel.Visibility == Visibility.Visible;
        AutomationProperties.SetName(SettingsButton, UiText.Get(open ? "BackToControls" : "Settings"));
        SettingsButton.ToolTip = UiText.Get(open ? "BackToControls" : "SettingsTip");
    }

    private static void Reveal(FrameworkElement panel)
    {
        panel.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    private void EnabledClick(object sender, RoutedEventArgs e) => EffectToggled?.Invoke(EnabledBox.IsChecked == true);
    private void SyncClick(object sender, RoutedEventArgs e) => SynchronizeToggled?.Invoke(SyncBox.IsChecked == true);
    private void ResetClick(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();
    private void KeepClick(object sender, RoutedEventArgs e) => PreviewAnswered?.Invoke(true);
    private void CancelClick(object sender, RoutedEventArgs e) => PreviewAnswered?.Invoke(false);
    private void ExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void StartupClick(object sender, RoutedEventArgs e) => StartupChanged?.Invoke(StartupBox.IsChecked == true);
    private void OverlayOnlyClick(object sender, RoutedEventArgs e) => OverlayOnlyChanged?.Invoke(OverlayOnlyBox.IsChecked == true);
    private void SettingsClick(object sender, RoutedEventArgs e) => SetSettingsOpen(SettingsPanel.Visibility != Visibility.Visible);

    private void FineClick(object sender, RoutedEventArgs e)
    {
        var open = FinePanel.Visibility != Visibility.Visible;
        if (open)
            Reveal(FinePanel);
        else
            FinePanel.Visibility = Visibility.Collapsed;
        FineChevron.RenderTransformOrigin = new Point(0.5, 0.5);
        FineChevron.RenderTransform = open ? new RotateTransform(180) : Transform.Identity;
        UpdateFineHelp();
    }

    private void UpdateFineHelp() => AutomationProperties.SetHelpText(FineButton,
        UiText.Get(FinePanel.Visibility == Visibility.Visible ? "FineExpanded" : "FineCollapsed"));

    private void LanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string language })
            LanguageChanged?.Invoke(language);
    }

    private void DisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rendering && DisplaysBox.SelectedValue is string id)
            DisplaySelected?.Invoke(id);
    }

    private void ProfileClick(object sender, RoutedEventArgs e)
    {
        if (_rendering || sender is not RadioButton { Tag: string id })
            return;
        ProfilesBox.SelectedValue = id;
        ProfileSelected?.Invoke(id);
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (ProfilesBox.SelectedValue is string id)
            ProfileSaved?.Invoke(id);
    }

    private void SettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_rendering || !IsInitialized || BlueSlider is null)
            return;

        UpdateValueLabels();
        SetProfileHighlight("custom");
        SettingsChanged?.Invoke(new EffectSettings(
            (int)BrightnessSlider.Value, (int)TemperatureSlider.Value,
            Math.Round(GammaSlider.Value, 2), (int)BlueReductionSlider.Value,
            Math.Round(RedSlider.Value, 2), Math.Round(GreenSlider.Value, 2), Math.Round(BlueSlider.Value, 2)));
    }

    private void UpdateValueLabels()
    {
        BrightnessValue.Text = $"{BrightnessSlider.Value:0}%";
        TemperatureValue.Text = $"{TemperatureSlider.Value:0} K";
        GammaValue.Text = GammaSlider.Value.ToString("0.00", UiText.Culture);
        BlueReductionValue.Text = $"{BlueReductionSlider.Value:0}%";
        RedValue.Text = $"{RedSlider.Value * 100:0}%";
        GreenValue.Text = $"{GreenSlider.Value * 100:0}%";
        BlueValue.Text = $"{BlueSlider.Value * 100:0}%";
    }

    private sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
