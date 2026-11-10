using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NightDim;
using NightDim.Core;

internal static class Program
{
    private static readonly DisplayInfo[] Displays =
    [
        new("primary", @"\\.\DISPLAY1", "P27FBA-RX", 0, 0, 1920, 1080, true, HdrState.Sdr),
        new("secondary", @"\\.\DISPLAY2", "Дисплей с очень длинным названием модели", -1920, 0,
            1920, 1080, false, HdrState.Hdr),
    ];

    private static readonly string[] MainControls =
    [
        "EnabledBox", "EnglishButton", "RussianButton", "DisplaysBox", "SyncBox",
        "DayProfile", "EveningProfile", "NightProfile", "CustomProfile",
        "BrightnessSlider", "TemperatureSlider", "FineButton", "ResetButton", "SettingsButton",
    ];

    private static readonly string[] FineControls =
    [
        "GammaSlider", "BlueReductionSlider", "RedSlider", "GreenSlider", "BlueSlider",
    ];

    private static readonly string[] SettingsControls =
    [
        "StartupBox", "OverlayOnlyBox", "GammaOnlyBox", "HotKeyLine", "ProfilesBox", "SaveButton", "DetailLine", "ExitButton",
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-screenshots");
        Directory.CreateDirectory(output);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

        // Rendering pumps the Dispatcher, which can raise Application.Startup.
        // Use a plain Application so the production host and display backends never start.
#pragma warning disable WPF0001 // Match the Fluent theme declared in App.xaml.
        _ = new Application { ThemeMode = ThemeMode.Dark, ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning restore WPF0001

        try
        {
            ApplyLanguage("ru");
            AssertWindowResourceUpdate();
            AssertEffectWorkerCache();
            foreach (var percent in new[] { 100, 125, 150, 200 })
                RunScale(percent, output);
            Console.WriteLine($"Rendered UI fixtures: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunScale(int percent, string output)
    {
        double scale = percent / 100.0;
        // A 1920×1080 desktop, minus 56 physical pixels of work-area inset and 40 DIPs of window chrome.
        var maxContentHeight = (1080 - 56) / scale - 40;
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), nonPublic: true)!;
        Invoke(window, "SetDisplays", (object)Displays);
        ShowSettings(window, ProfileManager.DefaultProfiles["evening"], profile: "evening");
        Invoke(window, "SetStatus", "Hybrid", "Контрольное чтение LUT совпало. Захват зависит от API.",
            "Возврат Ctrl+Alt+F10  ·  ±1% Ctrl+Alt+PgUp/PgDn  ·  Эффект Ctrl+Alt+F9", "");

        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var surface = new Border
        {
            Background = window.Background,
            Language = window.Language,
            UseLayoutRounding = window.UseLayoutRounding,
            SnapsToDevicePixels = window.SnapsToDevicePixels,
            Resources = window.Resources,
            ClipToBounds = true,
            Child = content,
        };
        TextElement.SetFontFamily(surface, window.FontFamily);
        TextElement.SetFontSize(surface, window.FontSize);
        TextElement.SetForeground(surface, window.Foreground);
        var layout = new FixtureLayout(window, surface, window.Width - 16, maxContentHeight, scale);
        layout.Update();
        Require(Math.Abs(VisualTreeHelper.GetDpi(layout.Find<Slider>("BrightnessSlider")).DpiScaleX - scale) < 0.01,
            $"WPF did not apply DPI {percent}% to the brightness slider.");
        Render(layout, percent, output, "main");

        var scrolled = MainControls.Count(name => layout.RequireReachable(name));
        AssertState(layout);
        AssertLocalization(layout);
        AssertControlEvents(layout);

        var fine = layout.Find<FrameworkElement>("FinePanel");
        Click(layout.Find<ButtonBase>("FineButton"));
        layout.Update();
        Require(fine.Visibility == Visibility.Visible, "Fine controls did not expand.");
        var fineScrolled = FineControls.Count(name => layout.RequireReachable(name));
        if (percent is 100 or 200)
            Render(layout, percent, output, "fine");
        Click(layout.Find<ButtonBase>("FineButton"));
        layout.Update();
        Require(fine.Visibility == Visibility.Collapsed, "Fine controls did not collapse.");

        var settings = layout.Find<FrameworkElement>("SettingsPanel");
        Click(layout.Find<ButtonBase>("SettingsButton"));
        layout.Update();
        Require(settings.Visibility == Visibility.Visible, "Settings did not open.");
        if (percent is 100 or 125)
            Render(layout, percent, output, "settings");
        foreach (var name in SettingsControls)
            layout.RequireReachable(name);
        Require(Equals(layout.Find<CheckBox>("StartupBox").Content, "Запускать с Windows"),
            "The startup label did not switch back to Russian.");
        AssertOverlayOnly(layout, "Фиксированное затемнение");
        AssertGammaOnly(layout, "Без экранного слоя");
        string? savedProfile = null;
        Subscribe(window, "ProfileSaved", (Action<string>)(id => savedProfile = id));
        layout.Find<ComboBox>("ProfilesBox").SelectedValue = "night";
        Click(layout.Find<Button>("SaveButton"));
        Require(savedProfile == "night", "Save did not request the selected profile.");
        var exits = 0;
        Subscribe(window, "ExitRequested", (Action)(() => exits++));
        Click(layout.Find<Button>("ExitButton"));
        Require(exits == 1, "Exit did not request host shutdown.");
        Click(layout.Find<ButtonBase>("SettingsButton"));
        layout.Update();
        Require(settings.Visibility == Visibility.Collapsed, "Settings did not close.");

        foreach (int brightness in new[] { 10, 5, 1 })
        {
            ShowSettings(window, new EffectSettings(brightness, 3200), profile: brightness == 10 ? "night" : "custom");
            Invoke(window, "SetPreview", 15, false);
            Require(!layout.Find<Button>("KeepButton").IsEnabled && layout.Find<Button>("CancelButton").IsEnabled,
                "The preview can be accepted before validation or cannot be canceled.");
            Invoke(window, "SetPreview", 15, true);
            layout.Update();
            var slider = layout.Find<Slider>("BrightnessSlider");
            Require(slider.Value == brightness && !slider.IsEnabled, $"Preview guard failed at {brightness}%.");
            Require(!layout.Find<Button>("ResetButton").IsEnabled, "Reset is enabled during preview.");
            Require(layout.Find<Button>("KeepButton").IsEnabled && layout.Find<Button>("CancelButton").IsEnabled,
                "Preview actions are not enabled.");
            Require(!layout.RequireReachable("CancelButton") && !layout.RequireReachable("KeepButton"),
                "Preview actions must stay visible without scrolling.");
            bool? answer = null;
            Action<bool> onAnswer = value => answer = value;
            Subscribe(window, "PreviewAnswered", onAnswer);
            Click(layout.Find<Button>("KeepButton"));
            Require(answer == true, "The keep action did not report acceptance.");
            Click(layout.Find<Button>("CancelButton"));
            Require(answer == false, "The cancel action did not report rejection.");
            Unsubscribe(window, "PreviewAnswered", onAnswer);
            if (brightness == 10 && percent is 100 or 200)
                Render(layout, percent, output, "preview");
            Invoke(window, "SetPreview", null, false);
            layout.Update();
            Require(slider.IsEnabled, "Sliders did not unlock when preview closed.");
        }

        Console.WriteLine($"PASS {percent}% | RU/EN, power, startup, dimming modes, fine tuning and 10/5/1% preview | " +
            $"{scrolled} main and {fineScrolled} fine controls needed scrolling");
        if (percent == 200)
            RunCompactLayout(layout, output);
        window.Close();
    }

    private static void RunCompactLayout(FixtureLayout layout, string output)
    {
        var compact = layout with { Width = layout.Window.MinWidth - 16, MaxHeight = (768 - 56) / 2.0 - 40 };
        ShowSettings(compact.Window, ProfileManager.DefaultProfiles["evening"], profile: "evening");
        compact.Update();
        AssertLocalization(compact);
        foreach (var name in MainControls)
            compact.RequireReachable(name);
        ShowSettings(compact.Window, new EffectSettings(1, 3200), profile: "custom");
        Invoke(compact.Window, "SetPreview", 15, true);
        compact.Update();
        Require(!compact.RequireReachable("CancelButton") && !compact.RequireReachable("KeepButton"),
            "Preview buttons are clipped in the compact viewport.");
        Render(compact, 200, output, "compact-preview");
        Console.WriteLine("PASS 200% | compact 1366×768 work-area fixture at minimum window width");
    }

    private static void AssertState(FixtureLayout layout)
    {
        var brightness = layout.Find<Slider>("BrightnessSlider");
        Require(brightness.Minimum == 1 && brightness.Maximum == 100 && brightness.SmallChange == 1,
            "Brightness range or step changed.");
        Require(layout.Find<Slider>("TemperatureSlider") is { Minimum: 2000, Maximum: 8000, SmallChange: 100 },
            "Temperature range or step changed.");
        Require(layout.Find<ComboBox>("DisplaysBox").Items.Count == 2, "Monitor selector did not show both displays.");
        Require(layout.Find<RadioButton>("EveningProfile").IsChecked == true, "Evening profile was not selected.");
    }

    private static void AssertWindowResourceUpdate()
    {
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), nonPublic: true)!;
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Require(handle != 0 && !window.IsVisible, "The resource check must run with a hidden WPF window.");
            var profile = (RadioButton)window.FindName("EveningProfile");
            Require(Equals(profile.Content, "Вечер"), "The hidden window did not start in Russian.");
            ApplyLanguage("en");
            Invoke(window, "RefreshLanguage");
            Require(Equals(profile.Content, "Evening"), "English resources did not propagate to the existing WPF window.");
            ApplyLanguage("ru");
            Invoke(window, "RefreshLanguage");
            Require(Equals(profile.Content, "Вечер"), "Russian resources did not propagate back to the existing WPF window.");
            Console.WriteLine("PASS hidden WPF window | runtime resource invalidation RU → EN → RU");
        }
        finally
        {
            ApplyLanguage("ru");
            window.Close();
        }
    }

    private static void AssertLocalization(FixtureLayout layout)
    {
        var window = layout.Window;
        var english = layout.Find<RadioButton>("EnglishButton");
        var russian = layout.Find<RadioButton>("RussianButton");
        var evening = layout.Find<RadioButton>("EveningProfile");
        var gamma = layout.Find<TextBlock>("GammaValue");
        var settings = layout.Find<Button>("SettingsButton");
        var profiles = layout.Find<ComboBox>("ProfilesBox");
        profiles.SelectedValue = "night";

        string? selectedLanguage = null;
        ResourceDictionary? languageResources = null;
        Action<string> changeLanguage = language =>
        {
            selectedLanguage = language;
            ApplyLanguage(language);
            // Application resource changes invalidate its windows. This offscreen tree is detached,
            // so also attach the production dictionary to its root to refresh DynamicResource values.
            var dictionaries = layout.Surface.Resources.MergedDictionaries;
            if (languageResources is not null)
                dictionaries.Remove(languageResources);
            languageResources = Application.Current.Resources.MergedDictionaries[^1];
            dictionaries.Add(languageResources);
            Invoke(window, "RefreshLanguage");
        };
        Subscribe(window, "LanguageChanged", changeLanguage);

        Require(russian.IsChecked == true && Equals(evening.Content, "Вечер") && gamma.Text == "1,00",
            $"The Russian window did not show localized state. RU={russian.IsChecked}, profile={evening.Content}, gamma={gamma.Text}.");
        AssertPowerLabels(layout, "ВКЛ", "ВЫКЛ");
        Click(english);
        layout.Update();
        Require(selectedLanguage == "en" && english.IsChecked == true && russian.IsChecked == false &&
            window.Language.IetfLanguageTag == "en-us" && Equals(evening.Content, "Evening") && gamma.Text == "1.00",
            $"The English selector did not refresh the window. Language={selectedLanguage}, EN={english.IsChecked}, " +
            $"RU={russian.IsChecked}, locale={window.Language.IetfLanguageTag}, profile={evening.Content}, gamma={gamma.Text}.");
        Require(profiles.SelectedValue is "night", "Changing language lost the selected save profile.");
        AssertPowerLabels(layout, "ON", "OFF");
        foreach (var name in MainControls)
            layout.RequireReachable(name);

        Click(settings);
        layout.Update();
        foreach (var name in SettingsControls)
            layout.RequireReachable(name);
        AssertStartup(layout);
        AssertOverlayOnly(layout, "Fixed dimming");
        AssertGammaOnly(layout, "Without screen overlay");
        Click(settings);

        Invoke(window, "SetPreview", 15, true);
        layout.Update();
        Require(Equals(layout.Find<Button>("CancelButton").Content, "Restore brightness") &&
            Equals(layout.Find<Button>("KeepButton").Content, "Keep") && !english.IsEnabled && !russian.IsEnabled,
            "The English preview did not localize recovery actions or lock language switching.");
        Require(!layout.RequireReachable("CancelButton") && !layout.RequireReachable("KeepButton"),
            "English preview buttons must remain visible without scrolling.");
        Invoke(window, "SetPreview", null, false);

        Click(russian);
        layout.Update();
        Require(selectedLanguage == "ru" && russian.IsChecked == true && english.IsChecked == false &&
            window.Language.IetfLanguageTag == "ru-ru" && Equals(evening.Content, "Вечер") && gamma.Text == "1,00",
            "Switching back to Russian did not refresh the existing window.");
        AssertPowerLabels(layout, "ВКЛ", "ВЫКЛ");
        Unsubscribe(window, "LanguageChanged", changeLanguage);
        layout.Surface.Resources.MergedDictionaries.Remove(languageResources!);
    }

    private static void AssertPowerLabels(FixtureLayout layout, string on, string off)
    {
        var toggle = layout.Find<CheckBox>("EnabledBox");
        var label = (TextBlock)toggle.Template.FindName("State", toggle);
        Require(label.Text == on, $"Expected power label {on} when enabled.");
        toggle.IsChecked = false;
        layout.Update();
        Require(label.Text == off, $"Expected power label {off} when disabled.");
        toggle.IsChecked = true;
        layout.Update();
    }

    private static void AssertStartup(FixtureLayout layout)
    {
        var startup = layout.Find<CheckBox>("StartupBox");
        Require(Equals(startup.Content, "Start with Windows"), "The startup label did not switch to English.");
        Invoke(layout.Window, "SetStartupState", (object?)null);
        Require(!startup.IsEnabled && startup.IsChecked == false, "Unavailable startup should disable its switch.");
        Invoke(layout.Window, "SetStartupState", false);
        Require(startup.IsEnabled && startup.IsChecked == false, "Startup should display the unregistered state.");
        Invoke(layout.Window, "SetStartupState", true);
        Require(startup.IsEnabled && startup.IsChecked == true, "Startup should display the registered state.");

        bool? reported = null;
        Action<bool> onStartup = enabled => reported = enabled;
        Subscribe(layout.Window, "StartupChanged", onStartup);
        startup.IsChecked = false;
        Click(startup);
        Require(reported == false, "The startup switch did not report the changed value.");
        Unsubscribe(layout.Window, "StartupChanged", onStartup);
        Invoke(layout.Window, "SetStartupState", false);
    }

    private static void AssertOverlayOnly(FixtureLayout layout, string label)
    {
        var window = layout.Window;
        var toggle = layout.Find<CheckBox>("OverlayOnlyBox");
        var brightness = layout.Find<Slider>("BrightnessSlider");
        var temperature = layout.Find<Slider>("TemperatureSlider");
        var fine = layout.Find<Button>("FineButton");
        var settings = ProfileManager.DefaultProfiles["evening"];
        Require(Equals(toggle.Content, label) && toggle.IsEnabled && toggle.IsChecked == false,
            $"The fixed dimming switch did not show its localized default state ({label}).");

        var changes = new List<bool>();
        Action<bool> onChange = changes.Add;
        Subscribe(window, "OverlayOnlyChanged", onChange);
        ShowSettings(window, settings, "evening", DimmingMode.OverlayOnly);
        Require(changes.Count == 0 && toggle.IsChecked == true,
            "Rendering the fixed dimming preference should not emit a user action.");
        ShowSettings(window, settings, "evening");
        Require(changes.Count == 0 && toggle.IsChecked == false,
            "Rendering the standard preference should not emit a user action.");

        toggle.IsChecked = true;
        Click(toggle);
        ShowSettings(window, settings, "evening", DimmingMode.OverlayOnly);
        Require(changes.SequenceEqual(new[] { true }) && toggle.IsChecked == true,
            "The fixed dimming switch did not report one enable action.");
        Require(brightness.IsEnabled && !temperature.IsEnabled && !fine.IsEnabled &&
            FineControls.All(name => !layout.Find<Slider>(name).IsEnabled),
            "Fixed dimming should keep brightness enabled and disable color controls.");
        Require(brightness.Value == settings.Brightness && temperature.Value == settings.Temperature,
            "Switching to fixed dimming should preserve stored slider values.");

        toggle.IsChecked = false;
        Click(toggle);
        ShowSettings(window, settings, "evening");
        Require(changes.SequenceEqual(new[] { true, false }) && toggle.IsChecked == false &&
            brightness.IsEnabled && temperature.IsEnabled && fine.IsEnabled,
            "Switching back to the standard mode did not restore color controls.");
        Unsubscribe(window, "OverlayOnlyChanged", onChange);
    }

    private static void AssertGammaOnly(FixtureLayout layout, string label)
    {
        var window = layout.Window;
        var toggle = layout.Find<CheckBox>("GammaOnlyBox");
        var fixedToggle = layout.Find<CheckBox>("OverlayOnlyBox");
        var brightness = layout.Find<Slider>("BrightnessSlider");
        var temperature = layout.Find<Slider>("TemperatureSlider");
        var fine = layout.Find<Button>("FineButton");
        var settings = ProfileManager.DefaultProfiles["night"];
        Require(Equals(toggle.Content, label) && toggle.IsEnabled && toggle.IsChecked == false,
            $"The gamma-only switch did not show its localized default state ({label}).");

        var changes = new List<bool>();
        Action<bool> onChange = changes.Add;
        var settingsChanges = 0;
        Action<EffectSettings> onSettingsChange = _ => settingsChanges++;
        Subscribe(window, "GammaOnlyChanged", onChange);
        Subscribe(window, "SettingsChanged", onSettingsChange);
        ShowSettings(window, settings, "night", DimmingMode.GammaOnly);
        Require(changes.Count == 0 && toggle.IsChecked == true && fixedToggle.IsChecked == false,
            "Rendering gamma-only mode should set the switch without emitting a user action.");
        Require(brightness.IsEnabled && brightness.Minimum == BrightnessEngine.MinimumGammaOnlyBrightness &&
            brightness.Value == brightness.Minimum && temperature.Value == settings.Temperature,
            "Gamma-only mode should clamp the brightness control without changing the color settings.");
        Require(layout.Find<TextBlock>("BrightnessMinimumText").Text == $"{brightness.Minimum:0}%" &&
            layout.Find<TextBlock>("BrightnessValue").Text == $"{brightness.Value:0}%",
            "The gamma-only labels should show the effective brightness and lower limit.");
        Require(temperature.IsEnabled && fine.IsEnabled && FineControls.All(name => layout.Find<Slider>(name).IsEnabled),
            "Gamma-only mode should keep all color controls enabled.");

        ShowSettings(window, settings, "night", DimmingMode.OverlayOnly);
        Require(changes.Count == 0 && toggle.IsChecked == false && fixedToggle.IsChecked == true &&
            brightness.Minimum == 1 && brightness.Value == settings.Brightness,
            "Switching to fixed dimming should select its switch and restore the full brightness range.");
        ShowSettings(window, settings, "night");
        Require(changes.Count == 0 && toggle.IsChecked == false && fixedToggle.IsChecked == false,
            "Rendering hybrid mode should clear both switches without emitting a user action.");

        toggle.IsChecked = true;
        Click(toggle);
        ShowSettings(window, settings, "night", DimmingMode.GammaOnly);
        toggle.IsChecked = false;
        Click(toggle);
        ShowSettings(window, ProfileManager.DefaultProfiles["evening"], "evening");
        Require(changes.SequenceEqual(new[] { true, false }) && toggle.IsChecked == false && brightness.Minimum == 1,
            "The gamma-only switch did not report both user actions.");
        Require(settingsChanges == 0, "Rendering the gamma-only range should not overwrite stored settings.");
        Unsubscribe(window, "SettingsChanged", onSettingsChange);
        Unsubscribe(window, "GammaOnlyChanged", onChange);
    }

    private static void AssertEffectWorkerCache()
    {
        var gamma = new CountingGamma();
        var overlay = new CountingOverlay();
        var engine = new BrightnessEngine(gamma, overlay, new MemoryGammaJournal());
        // RestoreAll can clear this empty backend. All effects use the fake engine backends.
        using var inactiveOverlay = new NightDim.Windows.OverlayBackend();
        var type = typeof(App).Assembly.GetType("NightDim.EffectWorker+Context", throwOnError: true)!;
        var context = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args: [Dispatcher.CurrentDispatcher, engine, inactiveOverlay], culture: null)!;
        var display = Displays[0];
        var settings = ProfileManager.DefaultProfiles["evening"];
        Invoke(context, "UpdateDisplays", (object)new[] { display });
        Invoke(context, "Apply", display, settings, DimmingMode.Hybrid);
        Require(gamma.Writes == 1 && overlay.Sets == 1, "The first standard effect did not apply through both backends.");

        var reads = gamma.Reads;
        var unchanged = display with { };
        Invoke(context, "UpdateDisplays", (object)new[] { unchanged });
        Invoke(context, "Apply", unchanged, settings with { }, DimmingMode.Hybrid);
        Require(gamma.Reads == reads && gamma.Writes == 1 && overlay.Sets == 1,
            "An identical standard effect touched the backends again.");

        Invoke(context, "Apply", display, settings, DimmingMode.OverlayOnly);
        Require(gamma.Current.Matches(GammaRamp.Identity) && gamma.Writes == 2 && overlay.Sets == 2,
            "Switching to fixed dimming should restore the owned LUT and update the overlay.");
        reads = gamma.Reads;
        Invoke(context, "Apply", display, settings, DimmingMode.OverlayOnly);
        Require(gamma.Reads == reads && overlay.Sets == 2, "An identical fixed effect touched the backends again.");

        var changed = settings with { Brightness = settings.Brightness + 1 };
        Invoke(context, "Apply", display, changed, DimmingMode.OverlayOnly);
        Require(overlay.Sets == 3, "Changing brightness should invalidate the worker cache.");
        var moved = display with { X = display.X + 1, Width = display.Width - 1 };
        var removals = overlay.Removes;
        Invoke(context, "UpdateDisplays", (object)new[] { moved });
        Require(overlay.Removes == removals && overlay.ActiveDisplayId == moved.Id,
            "Changing geometry should keep the same display's overlay.");
        Invoke(context, "Apply", moved, changed, DimmingMode.OverlayOnly);
        Require(overlay.Sets == 4, "Changing monitor geometry should invalidate the worker cache.");
        Invoke(context, "RestoreAll");
        Invoke(context, "Apply", moved, changed, DimmingMode.OverlayOnly);
        Require(overlay.Sets == 5, "Restoring effects should invalidate the worker cache.");

        overlay.Available = false;
        var retry = changed with { Brightness = changed.Brightness + 1 };
        Invoke(context, "Apply", moved, retry, DimmingMode.OverlayOnly);
        overlay.Available = true;
        Invoke(context, "Apply", moved, retry, DimmingMode.OverlayOnly);
        Require(overlay.Sets == 7, "A failed overlay application should be retried.");
        Require(gamma.Writes == 2, "Fixed dimming should not write additional gamma ramps after the initial restore.");

        Invoke(context, "Apply", moved, retry, DimmingMode.GammaOnly);
        Require(gamma.Writes == 3 && overlay.Sets == 7 && overlay.ActiveDisplayId is null,
            "Gamma-only mode should apply gamma and remove the previous overlay.");
        reads = gamma.Reads;
        Invoke(context, "Apply", moved, retry, DimmingMode.GammaOnly);
        Require(gamma.Reads == reads && gamma.Writes == 3 && overlay.Sets == 7,
            "An identical gamma-only effect touched the backends again.");

        Invoke(context, "Apply", moved, retry, DimmingMode.OverlayOnly);
        var hdr = moved with { Hdr = HdrState.Hdr };
        removals = overlay.Removes;
        Invoke(context, "UpdateDisplays", (object)new[] { hdr });
        Require(overlay.Removes == removals && overlay.ActiveDisplayId == hdr.Id,
            "Changing HDR should keep the same display's overlay.");
        Invoke(context, "Apply", hdr, retry, DimmingMode.OverlayOnly);
        Require(overlay.Sets == 9, "Changing HDR should invalidate the worker cache.");
        Invoke(context, "UpdateDisplays", (object)Array.Empty<DisplayInfo>());
        Require(overlay.Removes == removals + 1 && overlay.ActiveDisplayId is null,
            "Disconnecting the display should remove its overlay.");
        Console.WriteLine("PASS effect worker | deduplication, mode changes, display lifecycle, and retry after failure");
    }

    private static void ApplyLanguage(string language)
    {
        var uiText = typeof(App).Assembly.GetType("NightDim.Localization.UiText", throwOnError: true)!;
        uiText.GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Application.Current, language]);
    }

    private static void AssertControlEvents(FixtureLayout layout)
    {
        var window = layout.Window;
        var displays = layout.Find<ComboBox>("DisplaysBox");
        string? selectedDisplay = null;
        Subscribe(window, "DisplaySelected", (Action<string>)(id => selectedDisplay = id));
        displays.SelectedValue = Displays[1].Id;
        Require(selectedDisplay == Displays[1].Id, "Selecting the second monitor did not report its ID.");
        displays.SelectedValue = Displays[0].Id;
        Require(selectedDisplay == Displays[0].Id, "Selecting the first monitor did not report its ID.");

        foreach (var (name, eventName) in new[] { ("EnabledBox", "EffectToggled"), ("SyncBox", "SynchronizeToggled") })
        {
            var toggle = layout.Find<ToggleButton>(name);
            bool? reported = null;
            Subscribe(window, eventName, (Action<bool>)(enabled => reported = enabled));
            var original = toggle.IsChecked;
            toggle.IsChecked = original != true;
            Click(toggle);
            Require(reported == toggle.IsChecked, $"{name} did not report the new value.");
            toggle.IsChecked = original;
        }

        string? selectedProfile = null;
        Subscribe(window, "ProfileSelected", (Action<string>)(id => selectedProfile = id));
        foreach (var (control, profile) in new[]
        {
            ("DayProfile", "day"), ("EveningProfile", "evening"), ("NightProfile", "night"), ("CustomProfile", "custom"),
        })
        {
            var radio = layout.Find<RadioButton>(control);
            radio.IsChecked = true;
            Click(radio);
            Require(selectedProfile == profile, $"{control} did not report its profile.");
        }

        EffectSettings? updated = null;
        Subscribe(window, "SettingsChanged", (Action<EffectSettings>)(settings => updated = settings));
        layout.Find<Slider>("BrightnessSlider").Value = 5;
        layout.Find<Slider>("TemperatureSlider").Value = 3000;
        layout.Find<Slider>("GammaSlider").Value = 0.75;
        layout.Find<Slider>("BlueReductionSlider").Value = 26;
        layout.Find<Slider>("RedSlider").Value = 0.75;
        layout.Find<Slider>("GreenSlider").Value = 0.56;
        layout.Find<Slider>("BlueSlider").Value = 0.37;
        Require(updated == new EffectSettings(5, 3000, 0.75, 26, 0.75, 0.56, 0.37),
            "The sliders did not report the expected settings.");
        Require(layout.Find<RadioButton>("CustomProfile").IsChecked == true,
            "Editing settings did not highlight the custom profile.");
        var resets = 0;
        Subscribe(window, "ResetRequested", (Action)(() => resets++));
        Click(layout.Find<Button>("ResetButton"));
        Require(resets == 1, "The reset action did not request the neutral profile.");
        ShowSettings(window, ProfileManager.DefaultProfiles["evening"], profile: "evening");
    }

    private static void Subscribe(MainWindow window, string name, Delegate handler)
        => FindEvent(name).GetAddMethod(nonPublic: true)!.Invoke(window, [handler]);

    private static void Unsubscribe(MainWindow window, string name, Delegate handler)
        => FindEvent(name).GetRemoveMethod(nonPublic: true)!.Invoke(window, [handler]);

    private static EventInfo FindEvent(string name)
        => typeof(MainWindow).GetEvent(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing UI event {name}.");

    private static void ShowSettings(MainWindow window, EffectSettings settings, string profile,
        DimmingMode mode = DimmingMode.Hybrid)
        => Invoke(window, "ShowSettings", settings, true, false, "primary", profile, true, mode);

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing fixture method {name}.");
        var parameters = method.GetParameters();
        var actual = parameters.Select((parameter, index) =>
            index < arguments.Length ? arguments[index] : parameter.DefaultValue).ToArray();
        method.Invoke(target, actual);
    }

    private static void Render(FixtureLayout layout, int percent, string output, string state)
    {
        // Snapshots show settled 150 ms panel transitions.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        layout.Update();

        double scale = percent / 100.0;
        var size = layout.Surface.RenderSize;
        var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale),
            (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(layout.Surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(output, $"{state}-{percent}.png"));
        encoder.Save(file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class CountingGamma : IGammaTransport
    {
        internal GammaRamp Current { get; private set; } = GammaRamp.Identity;
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }

        public bool TryRead(DisplayInfo display, out GammaRamp? ramp)
        {
            Reads++;
            ramp = Current;
            return true;
        }

        public bool TryWrite(DisplayInfo display, GammaRamp ramp)
        {
            Writes++;
            Current = ramp;
            return true;
        }
    }

    private sealed class CountingOverlay : IOverlayBackend
    {
        internal bool Available { get; set; } = true;
        internal int Sets { get; private set; }
        internal int Removes { get; private set; }
        internal string? ActiveDisplayId { get; private set; }

        public bool TrySetOpacity(DisplayInfo display, byte opacity)
        {
            Sets++;
            if (!Available)
                return false;
            ActiveDisplayId = opacity == 0 ? null : display.Id;
            return true;
        }

        public void Remove(string displayId)
        {
            Removes++;
            if (ActiveDisplayId == displayId)
                ActiveDisplayId = null;
        }
    }

    private sealed record FixtureLayout(MainWindow Window, Border Surface, double Width, double MaxHeight, double Scale)
    {
        internal T Find<T>(string name) where T : FrameworkElement => Window.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing UI element {name} ({typeof(T).Name}).");

        internal void Update()
        {
            // Detached templates can create visuals during measure. Apply DPI after that pass too.
            Arrange();
            if (Scale != 1)
                VisualTreeHelper.SetRootDpi(Surface, new DpiScale(1, 1));
            VisualTreeHelper.SetRootDpi(Surface, new DpiScale(Scale, Scale));
            Arrange();
        }

        private void Arrange()
        {
            // This offscreen tree has no Window to invalidate the viewport for us.
            var scroll = Find<ScrollViewer>("BodyScroll");
            if (scroll.Content is UIElement content)
                content.InvalidateMeasure();
            (scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as UIElement)?.InvalidateMeasure();
            scroll.InvalidateMeasure();
            Surface.InvalidateMeasure();
            Surface.Measure(new Size(Width, MaxHeight));
            var height = Math.Min(MaxHeight, Surface.DesiredSize.Height);
            Surface.Arrange(new Rect(0, 0, Width, height));
            Surface.UpdateLayout();
        }

        // Scroll an out-of-viewport control through WPF's normal BringIntoView path.
        internal bool RequireReachable(string name)
        {
            var element = Find<FrameworkElement>(name);
            if (Fits(element))
                return false;
            element.BringIntoView();
            Update();
            var scroll = Find<ScrollViewer>("BodyScroll");
            var info = (IScrollInfo?)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            Require(Fits(element), $"{name} is clipped or unreachable after scrolling. " +
                $"Element {element.TransformToAncestor(Surface).TransformBounds(new Rect(element.RenderSize))}; " +
                $"surface {Surface.RenderSize}; scroll {scroll.VerticalOffset:F1}/{scroll.ExtentHeight:F1}, viewport {scroll.ViewportHeight:F1}; " +
                $"presenter {info?.VerticalOffset:F1}/{info?.ExtentHeight:F1}, vertical {info?.CanVerticallyScroll}.");
            return true;
        }

        private bool Fits(FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;
            var full = element.TransformToAncestor(Surface).TransformBounds(new Rect(element.RenderSize));
            var visible = Rect.Intersect(full, new Rect(Surface.RenderSize));
            for (DependencyObject? node = element; node is Visual visual && node != Surface;
                node = VisualTreeHelper.GetParent(node))
            {
                var transform = visual.TransformToAncestor(Surface);
                if (VisualTreeHelper.GetClip(visual) is { } clip)
                    visible.Intersect(transform.TransformBounds(clip.Bounds));
                if (node is FrameworkElement { ClipToBounds: true } ancestor)
                    visible.Intersect(transform.TransformBounds(new Rect(ancestor.RenderSize)));
            }

            const double tolerance = 1.0;
            return visible.Width >= full.Width - tolerance && visible.Height >= full.Height - tolerance;
        }
    }
}
