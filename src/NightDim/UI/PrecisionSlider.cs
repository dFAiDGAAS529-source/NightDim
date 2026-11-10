using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NightDim.UI;

public sealed class PrecisionSlider : Slider
{
    private int _wheelRemainder;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsKeyboardFocusWithin || !IsEnabled)
        {
            base.OnMouseWheel(e);
            return;
        }

        // High-resolution wheels may deliver a single detent as several events.
        _wheelRemainder += e.Delta;
        int steps = _wheelRemainder / Mouse.MouseWheelDeltaForOneLine;
        _wheelRemainder %= Mouse.MouseWheelDeltaForOneLine;
        while (steps > 0)
        {
            OnIncreaseSmall();
            steps--;
        }
        while (steps < 0)
        {
            OnDecreaseSmall();
            steps++;
        }
        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _wheelRemainder = 0;
        base.OnLostKeyboardFocus(e);
    }
}
