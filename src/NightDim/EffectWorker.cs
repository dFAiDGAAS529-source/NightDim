using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NightDim.Core;
using NightDim.Windows;

namespace NightDim;

internal sealed class EffectWorker
{
    private readonly TaskCompletionSource<Context> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal EffectWorker(IGammaJournal journal)
    {
        var thread = new Thread(() => Run(journal))
        {
            IsBackground = true,
            Name = "NightDim display effects",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal async Task<T> InvokeAsync<T>(Func<Context, T> action, bool urgent = false)
    {
        var context = await _ready.Task.ConfigureAwait(false);
        return await context.Dispatcher.InvokeAsync(() => action(context),
            urgent ? DispatcherPriority.Send : DispatcherPriority.Normal).Task.ConfigureAwait(false);
    }

    internal async Task StopAsync()
    {
        var context = await _ready.Task.ConfigureAwait(false);
        context.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }

    private void Run(IGammaJournal journal)
    {
        try
        {
            using var overlay = new OverlayBackend();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var engine = new BrightnessEngine(new GdiGammaTransport(), overlay, journal);
            _ready.SetResult(new Context(dispatcher, engine, overlay));
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    internal sealed record Context(Dispatcher Dispatcher, BrightnessEngine Engine, OverlayBackend Overlay)
    {
        private IReadOnlyList<DisplayInfo> _displays = [];

        internal void UpdateDisplays(IReadOnlyList<DisplayInfo> current)
        {
            static bool SameSource(DisplayInfo first, DisplayInfo second) =>
                first.Id == second.Id && first.DeviceName == second.DeviceName && first.Hdr == second.Hdr
                && first.CanRecoverGamma == second.CanRecoverGamma
                && first.Width == second.Width && first.Height == second.Height;

            foreach (var old in _displays.Where(old => !current.Any(display => SameSource(old, display))))
                Engine.Disconnect(old.Id);
            foreach (var display in current.Where(display => !_displays.Any(old => SameSource(old, display))))
                Engine.Recover(display);
            _displays = current;
        }

        internal bool RestoreAll()
        {
            Overlay.Clear();
            var restored = true;
            foreach (var display in _displays)
                restored &= Engine.Restore(display);
            return restored;
        }
    }
}
