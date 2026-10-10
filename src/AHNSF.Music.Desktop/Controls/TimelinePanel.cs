using Avalonia;
using Avalonia.Controls;

namespace AHNSF.Music.Desktop.Controls;

// Places each child horizontally by its Start/End hour on a 0–24 h scale; used by the weekly schedule view only.
public sealed class TimelinePanel : Panel
{
    public static readonly AttachedProperty<double> StartProperty = AvaloniaProperty.RegisterAttached<TimelinePanel, Control, double>("Start");
    public static readonly AttachedProperty<double> EndProperty = AvaloniaProperty.RegisterAttached<TimelinePanel, Control, double>("End");
    public static double GetStart(Control c) => c.GetValue(StartProperty);
    public static void SetStart(Control c, double v) => c.SetValue(StartProperty, v);
    public static double GetEnd(Control c) => c.GetValue(EndProperty);
    public static void SetEnd(Control c, double v) => c.SetValue(EndProperty, v);
    static TimelinePanel() => AffectsParentArrange<TimelinePanel>(StartProperty, EndProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (var child in Children) { child.Measure(new Size(double.PositiveInfinity, availableSize.Height)); height = Math.Max(height, child.DesiredSize.Height); }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var start = Math.Clamp(GetStart(child), 0, 24); var end = Math.Clamp(GetEnd(child), start, 24);
            var x = finalSize.Width * start / 24; var width = Math.Max(2, finalSize.Width * (end - start) / 24 - 2);
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
        }
        return finalSize;
    }
}
