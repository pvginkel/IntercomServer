using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IntercomTest;

internal class CustomSlider : Slider
{
    public bool IsTracking { get; set; }

    protected override void OnThumbDragStarted(DragStartedEventArgs e)
    {
        IsTracking = true;

        base.OnThumbDragStarted(e);
    }

    protected override void OnThumbDragCompleted(DragCompletedEventArgs e)
    {
        IsTracking = false;

        base.OnThumbDragCompleted(e);

        OnValueChanged(Value, Value);
    }
}
