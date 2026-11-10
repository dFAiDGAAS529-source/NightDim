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
    internal static int RunCaptureProbe(string output)
    {
        const int size = 24;
        const byte alpha = 3;
        var report = new StringBuilder()
            .AppendLine("Offscreen NightDim overlay affinity probe.")
            .AppendLine("No pixels captured or gamma ramps changed.");
        var exitCode = 0;
        try
        {
            var displays = new DisplayManager().Enumerate();
            if (displays.Count == 0)
                throw new InvalidOperationException("No active displays to establish safe offscreen bounds.");

            var x = checked(displays.Min(display => display.X) - 2 * size);
            var y = checked(displays.Min(display => display.Y) - 2 * size);
            var fixture = new DisplayInfo("capture-probe", "", "Capture probe", x, y, size, size,
                false, HdrState.Unknown, CanRecoverGamma: false);
            using var overlay = new OverlayBackend();
            if (!overlay.TrySetOpacity(fixture, alpha) || overlay.Inspect(fixture.Id) is not { } readback)
                throw new InvalidOperationException("Could not create or inspect the offscreen overlay.");

            report.AppendLine(FormattableString.Invariant(
                $"Bounds=({readback.X}, {readback.Y}, {readback.Width}x{readback.Height}), alpha={readback.AlphaRead}/{readback.Alpha}, affinity={readback.AffinityRead}/0x{readback.Affinity:X}, style=0x{readback.ExtendedStyle:X}"));
            if (readback.X != x || readback.Y != y || readback.Width != size || readback.Height != size ||
                !readback.AlphaRead || readback.Alpha != alpha || !readback.AffinityRead || readback.Affinity != 0)
                throw new InvalidOperationException("Expected offscreen bounds, test alpha and WDA_NONE.");

            report.AppendLine("PASS: the active overlay does not restrict capture (WDA_NONE).");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            report.AppendLine(ex.Message);
        }

        try
        {
            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 1;
        }
        return exitCode;
    }

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
