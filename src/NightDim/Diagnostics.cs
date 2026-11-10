using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using NightDim.Core;
using NightDim.Localization;
using NightDim.Windows;

namespace NightDim;

internal static class Diagnostics
{
    internal static async Task RunAsync(Application app, string output)
    {
        var report = new StringBuilder();
        ApplicationHost? host = null;
        var exitCode = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            report.AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
            var startup = Stopwatch.StartNew();
            host = new ApplicationHost(app, output + ".settings");
            await host.StartAsync(show: false);
            report.AppendLine(FormattableString.Invariant($"Host initialized in {startup.Elapsed.TotalMilliseconds:F2} ms"));

            using var process = Process.GetCurrentProcess();
            await Task.Delay(1000);
            process.Refresh();
            var startCpu = process.TotalProcessorTime;
            var idle = Stopwatch.StartNew();
            await Task.Delay(5000);
            process.Refresh();
            var cpuMs = (process.TotalProcessorTime - startCpu).TotalMilliseconds;
            report.AppendLine(FormattableString.Invariant(
                $"Idle tray: {idle.Elapsed.TotalMilliseconds:F0} ms wall, {cpuMs:F2} ms CPU, {process.WorkingSet64 / 1048576.0:F1} MiB working set, {process.PrivateMemorySize64 / 1048576.0:F1} MiB private bytes"));

            var scan = Stopwatch.StartNew();
            var displays = new DisplayManager().Enumerate();
            report.AppendLine(FormattableString.Invariant($"Enumeration: {displays.Count} displays, {scan.Elapsed.TotalMilliseconds:F2} ms"));
            foreach (var display in displays)
            {
                var raw = GdiGammaTransport.TryReadUnverified(display, out _);
                report.AppendLine($"  {display.DeviceName}: {display.Width}x{display.Height} at ({display.X}, {display.Y}), {display.Hdr}, raw LUT read={raw}");
            }

            var primary = displays.FirstOrDefault(display => display.IsPrimary);
            if (primary is not null)
            {
                var worker = new EffectWorker(new FileGammaJournal(output + ".gamma-recovery"));
                try
                {
                    await worker.InvokeAsync(context =>
                    {
                        context.UpdateDisplays(displays);
                        var timer = Stopwatch.StartNew();
                        var applied = context.Engine.Apply(primary, new EffectSettings(Brightness: 96));
                        report.AppendLine(FormattableString.Invariant(
                            $"96% apply: {timer.Elapsed.TotalMilliseconds:F2} ms, {applied.Mechanism}, alpha={applied.OverlayOpacity}. {UiText.Describe(applied)}"));
                        timer.Restart();
                        var restore = context.Engine.Restore(primary);
                        report.AppendLine(FormattableString.Invariant(
                            $"96% restore: {timer.Elapsed.TotalMilliseconds:F2} ms, confirmed={restore}"));
                        timer.Restart();
                        var overlaySet = context.Overlay.TrySetOpacity(primary, 3);
                        var overlay = context.Overlay.Inspect(primary.Id);
                        report.AppendLine(FormattableString.Invariant(
                            $"Overlay alpha=3 create/read: {timer.Elapsed.TotalMilliseconds:F2} ms, success={overlaySet}, read={overlay?.AlphaRead}/{overlay?.Alpha}, affinity={overlay?.AffinityRead}/0x{overlay?.Affinity:X}, style=0x{overlay?.ExtendedStyle:X}"));
                        context.Overlay.Remove(primary.Id);
                        return true;
                    });
                }
                finally
                {
                    await worker.InvokeAsync(context => context.RestoreAll(), urgent: true);
                    await worker.StopAsync();
                }
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            report.AppendLine(ex.ToString());
        }

        try
        {
            File.WriteAllText(output, report.ToString());
        }
        catch (IOException)
        {
            exitCode = 1;
        }
        if (host is not null)
            host.Dispose();
        app.Shutdown(exitCode);
    }
}
