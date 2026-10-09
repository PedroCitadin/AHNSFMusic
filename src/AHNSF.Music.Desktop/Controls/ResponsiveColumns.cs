using Avalonia;
using Avalonia.Controls;

namespace AHNSF.Music.Desktop.Controls;

// Grid.ColumnDefinitions is a CLR property; this attached property makes breakpoints styleable.
public sealed class ResponsiveColumns : AvaloniaObject
{
    public static readonly AttachedProperty<string?> ValueProperty = AvaloniaProperty.RegisterAttached<ResponsiveColumns, Grid, string?>("Value");
    static ResponsiveColumns() => ValueProperty.Changed.AddClassHandler<Grid>((grid, args) =>
    { if (args.NewValue is string columns) grid.ColumnDefinitions = new ColumnDefinitions(columns); });
    public static string? GetValue(Grid grid) => grid.GetValue(ValueProperty);
    public static void SetValue(Grid grid, string? value) => grid.SetValue(ValueProperty, value);
}
