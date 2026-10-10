using AHNSF.Music.Core;

namespace AHNSF.Music.Desktop.Theming;

/// <summary>Where a computed theme is written (application resources in the app, a recorder in tests).</summary>
public interface IThemeTarget { void Apply(ThemePalette palette, AppearanceSettings appearance); }
/// <summary>The Windows light/dark preference; null when the platform does not report one.</summary>
public interface ISystemThemeSource { bool? PrefersDark { get; } event EventHandler? Changed; }

/// <summary>
/// Single place that turns persisted <see cref="AppearanceSettings"/> into a live theme. It never touches Settings
/// storage (the view model persists) nor playback/schedule; applying is synchronous and idempotent.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly IThemeTarget? target;
    private readonly ISystemThemeSource? system;
    public AppearanceSettings Current { get; private set; } = new();
    public ThemePalette Palette { get; private set; }
    public bool EffectiveDark => Palette.IsDark;
    /// <summary>Raised after every application (preference change or Windows theme change while following it).</summary>
    public event EventHandler? Applied;

    public ThemeService(IThemeTarget? target = null, ISystemThemeSource? system = null)
    {
        this.target = target; this.system = system;
        Palette = ThemePalette.Create(true, Current.AccentHex);
        if (system is not null) system.Changed += OnSystemChanged;
    }
    public static bool ResolveDark(ThemeMode mode, bool? systemPrefersDark) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.System => systemPrefersDark ?? true,
        _ => true
    };
    public ThemePalette Apply(AppearanceSettings appearance)
    {
        Current = appearance.Normalize();
        Palette = ThemePalette.Create(ResolveDark(Current.Theme, system?.PrefersDark), Current.AccentHex);
        target?.Apply(Palette, Current);
        Applied?.Invoke(this, EventArgs.Empty);
        return Palette;
    }
    private void OnSystemChanged(object? sender, EventArgs e) { if (Current.Theme == ThemeMode.System) Apply(Current); }
    public void Dispose() { if (system is not null) system.Changed -= OnSystemChanged; }
}
