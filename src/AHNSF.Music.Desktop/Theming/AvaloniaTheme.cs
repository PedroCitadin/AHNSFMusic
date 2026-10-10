using System.Globalization;
using AHNSF.Music.Core;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AHNSF.Music.Desktop.Theming;

/// <summary>
/// Writes tokens into Application.Resources (which take precedence over the defaults in DesignSystem.axaml).
/// Brush instances are created once and recoloured in place, so every consumer updates without restarting.
/// </summary>
public sealed class ApplicationThemeTarget(Application application) : IThemeTarget
{
    // Names used before 0.8.3; kept as aliases of the semantic tokens.
    private static readonly (string Legacy, string Token)[] aliases =
    [
        ("CanvasBrush", "BackgroundPrimary"), ("SidebarBrush", "BackgroundSecondary"), ("SurfaceBrush", "Surface"), ("RaisedBrush", "SurfaceRaised"),
        ("LineBrush", "BorderSubtle"), ("TextBrush", "TextPrimary"), ("MutedBrush", "TextSecondary"), ("AccentBrush", "AccentText"),
        ("WarningBrush", "Warning"), ("DangerBrush", "Error"), ("OnAccentBrush", "AccentForeground"), ("InputBrush", "SurfaceRaised")
    ];
    private readonly Dictionary<string, SolidColorBrush> brushes = [];
    private LinearGradientBrush? hero;

    public void Apply(ThemePalette palette, AppearanceSettings appearance)
    {
        var resources = application.Resources;
        foreach (var (token, color) in palette.Tokens)
        {
            if (!brushes.TryGetValue(token, out var brush)) { brush = new SolidColorBrush(color); brushes[token] = brush; resources[token + "Brush"] = brush; }
            else brush.Color = color;
            resources[token + "Color"] = color;
        }
        foreach (var (legacy, token) in aliases) resources[legacy] = brushes[token];
        hero ??= CreateHero(resources);
        hero.GradientStops[0].Color = palette["AccentSubtle"]; hero.GradientStops[1].Color = palette["Surface"];
        // Fluent controls (CheckBox, Slider, ProgressBar, ToggleSwitch, focus) read these keys.
        var accent = palette["AccentPrimary"];
        resources["SystemAccentColor"] = accent;
        for (var i = 1; i <= 3; i++)
        {
            resources[$"SystemAccentColorLight{i}"] = ThemePalette.Mix(accent, Colors.White, 0.12 * i);
            resources[$"SystemAccentColorDark{i}"] = ThemePalette.Mix(accent, Colors.Black, 0.12 * i);
        }
        application.RequestedThemeVariant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
    private static LinearGradientBrush CreateHero(Avalonia.Controls.IResourceDictionary resources)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative) };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0)); brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.85));
        resources["HeroBrush"] = brush;
        return brush;
    }
}

/// <summary>Follows the Windows app mode (light/dark) reported by Avalonia's platform settings.</summary>
public sealed class PlatformSystemThemeSource : ISystemThemeSource, IDisposable
{
    private readonly IPlatformSettings? settings;
    public event EventHandler? Changed;
    public PlatformSystemThemeSource(IPlatformSettings? settings)
    {
        this.settings = settings;
        if (settings is not null) settings.ColorValuesChanged += OnColorValuesChanged;
    }
    public bool? PrefersDark
    {
        get
        {
            try { return settings?.GetColorValues().ThemeVariant switch { PlatformThemeVariant.Dark => true, PlatformThemeVariant.Light => false, _ => null }; }
            catch (Exception) { return null; }
        }
    }
    private void OnColorValuesChanged(object? sender, PlatformColorValues e) => Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    public void Dispose() { if (settings is not null) settings.ColorValuesChanged -= OnColorValuesChanged; }
}

/// <summary>
/// Maps a semantic tone name from the view model ("AccentText", "Warning", "Error", "TextSecondary"…) to the live brush,
/// so presentation code never carries hex colours and follows the active theme.
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public static readonly ToneBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value as string is { Length: > 0 } text ? text : parameter as string ?? "TextPrimary";
        return Application.Current is { } app && app.TryGetResource(tone + "Brush", app.ActualThemeVariant, out var brush) ? brush : null;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
