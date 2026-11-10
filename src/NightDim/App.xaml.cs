using System;
using System.IO;
using System.Threading;
using System.Windows;
using NightDim.Core;
using NightDim.Localization;

namespace NightDim;

public partial class App : Application
{
    private ApplicationHost? _host;
    private Mutex? _instance;

    public App() => UiText.Apply(this, "ru");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--probe-capture", var capturePath])
        {
            Shutdown(Diagnostics.RunCaptureProbe(capturePath));
            return;
        }

        var defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightDim");
        _instance = new Mutex(true, @"Local\NightDim.ScreenEffects", out var firstInstance);
        if (!firstInstance)
        {
            if (e.Args.Length == 0)
            {
                var preferences = new ProfileManager(Path.Combine(defaultDirectory, "settings.json")).Load();
                UiText.Apply(this, preferences.Language);
                MessageBox.Show(UiText.Get("AlreadyRunning"), "NightDim");
            }
            Shutdown(1);
            return;
        }

        if (e.Args is ["--probe", var path])
        {
            await Diagnostics.RunAsync(this, Path.GetFullPath(path));
            return;
        }

        try
        {
            var directory = e.Args is ["--data-dir", var dataPath] ? Path.GetFullPath(dataPath) : defaultDirectory;
            _host = new ApplicationHost(this, directory);
            await _host.StartAsync(show: e.Args is not ["--autostart"]);
        }
        catch (Exception ex)
        {
            _host?.EndSession();
            MessageBox.Show(UiText.Format("StartupFailed", ex.Message), "NightDim");
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _host?.EndSession();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
