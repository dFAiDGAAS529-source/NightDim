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
        private readonly Dictionary<string, AppliedEffect> _applied = new(StringComparer.Ordinal);

        internal void UpdateDisplays(IReadOnlyList<DisplayInfo> current)
        {
            static bool SameSource(DisplayInfo first, DisplayInfo second) =>
                first.Id == second.Id && first.DeviceName == second.DeviceName && first.Hdr == second.Hdr
                && first.CanRecoverGamma == second.CanRecoverGamma
                && first.Width == second.Width && first.Height == second.Height;

            foreach (var old in _displays)
            {
                var updated = current.FirstOrDefault(display => display.Id == old.Id);
                if (updated != old)
                    _applied.Remove(old.Id);
                if (updated is null)
                    Engine.Disconnect(old.Id);
                else if (!SameSource(old, updated))
                    Engine.ForgetGammaState(old.Id);
            }
            foreach (var display in current.Where(display => !_displays.Any(old => SameSource(old, display))))
                Engine.Recover(display);
            _displays = current;
        }

        internal DisplayEffectResult Apply(DisplayInfo display, EffectSettings settings, DimmingMode mode)
        {
            if (_applied.TryGetValue(display.Id, out var prior) && prior.Display == display
                && prior.Settings == settings && prior.Mode == mode)
                return prior.Result;

            _applied.Remove(display.Id);
            var result = Engine.Apply(display, settings, mode);
            if (result.Mechanism != EffectMechanism.Unavailable && result.AdditionalDiagnostic != EffectDiagnostic.OverlayUnavailable)
                _applied[display.Id] = new(display, settings, mode, result);
            return result;
        }

        internal bool ProbeOverlay(DisplayInfo display)
        {
            if (Engine.ProbeOverlay(display))
                return true;
            _applied.Remove(display.Id);
            return false;
        }

        internal void InvalidateEffects() => _applied.Clear();

        internal bool RestoreAll()
        {
            InvalidateEffects();
            Overlay.Clear();
            var restored = true;
            foreach (var display in _displays)
                restored &= Engine.Restore(display);
            return restored;
        }

        private sealed record AppliedEffect(DisplayInfo Display, EffectSettings Settings,
            DimmingMode Mode, DisplayEffectResult Result);
    }
}
