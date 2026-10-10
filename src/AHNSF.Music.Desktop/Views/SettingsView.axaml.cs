using Avalonia.Controls;
using Avalonia.Interactivity;
namespace AHNSF.Music.Desktop.Views;
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    // Section index: scrolls the named section into view (presentation only).
    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && this.FindControl<Control>(name) is { } section)
            SettingsScroll.Offset = new Avalonia.Vector(0, Math.Max(0, Avalonia.VisualExtensions.TranslatePoint(section, default, (Control)SettingsScroll.Content!)?.Y ?? 0));
    }
}
