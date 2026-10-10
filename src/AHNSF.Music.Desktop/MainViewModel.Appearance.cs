using AHNSF.Music.Core;
using AHNSF.Music.Desktop.Theming;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

// Fase 0.8.3: appearance preferences. Persisted in the existing Settings JSON; applied by ThemeService.
// Never calls ManualIntervention and never touches playback, queue, devices or schedule.
public partial class MainViewModel
{
    private ThemeService? theme;
    private bool loadingAppearance;
    public sealed record AccentChoice(string Key, string Name, string Hex)
    {
        public ImmutableSolidColorBrush Swatch { get; } = new(Avalonia.Media.Color.Parse(Hex));
        public bool IsCustom => Key == AppearanceSettings.CustomPreset;
    }
    public IReadOnlyList<string> ThemeModeOptions { get; } = ["Escuro", "Claro", "Seguir o tema do Windows"];
    public IReadOnlyList<string> DensityOptions { get; } = ["Confortável", "Compacta"];
    public IReadOnlyList<AccentChoice> AccentChoices { get; } = AppearanceSettings.Presets.Select(p => new AccentChoice(p.Key, p.Name, p.Hex))
        .Append(new AccentChoice(AppearanceSettings.CustomPreset, "Personalizada", "#808890")).ToArray();
    [ObservableProperty] private int themeModeIndex;
    [ObservableProperty] private AccentChoice? selectedAccent;
    [ObservableProperty] private string customAccentText = "";
    [ObservableProperty] private int densityIndex;
    [ObservableProperty] private bool reducedMotion;
    [ObservableProperty] private string appearanceFeedback = "";
    [ObservableProperty] private bool appearanceNeedsAttention;
    public AppearanceSettings Appearance => (settings.Appearance ?? new AppearanceSettings()).Normalize();
    public bool IsCustomAccent => SelectedAccent?.Key == AppearanceSettings.CustomPreset;
    public bool CompactDensity => DensityIndex == (int)UiDensity.Compact;
    public string EffectiveThemeLabel => (theme?.EffectiveDark ?? Appearance.Theme != ThemeMode.Light) ? "Escuro" : "Claro";
    public string AppearanceSummary => $"{ThemeModeOptions[Math.Clamp(ThemeModeIndex, 0, 2)]}{(Appearance.Theme == ThemeMode.System ? $" (agora: {EffectiveThemeLabel.ToLowerInvariant()})" : "")} • {SelectedAccent?.Name ?? "Verde-água AHNSF"} • densidade {DensityOptions[Math.Clamp(DensityIndex, 0, 1)].ToLowerInvariant()}{(ReducedMotion ? " • animações reduzidas" : "")}";

    /// <summary>Called once by the App before initialization; tests may attach a ThemeService with a recording target.</summary>
    public void AttachTheme(ThemeService service)
    {
        if (theme is not null) theme.Applied -= OnThemeApplied;
        theme = service; theme.Applied += OnThemeApplied;
    }
    private void OnThemeApplied(object? sender, EventArgs e) { OnPropertyChanged(nameof(EffectiveThemeLabel)); OnPropertyChanged(nameof(AppearanceSummary)); }

    private void LoadAppearance()
    {
        var appearance = Appearance;
        loadingAppearance = true;
        try
        {
            ThemeModeIndex = (int)appearance.Theme;
            DensityIndex = (int)appearance.Density;
            ReducedMotion = appearance.ReducedMotion;
            CustomAccentText = appearance.CustomAccent ?? "";
            SelectedAccent = AccentChoices.First(c => c.Key == appearance.AccentPreset);
        }
        finally { loadingAppearance = false; }
        ApplyAppearance(appearance, persist: false);
    }
    partial void OnThemeModeIndexChanged(int value) => AppearanceEdited();
    partial void OnSelectedAccentChanged(AccentChoice? value) { OnPropertyChanged(nameof(IsCustomAccent)); AppearanceEdited(); }
    partial void OnCustomAccentTextChanged(string value) { if (IsCustomAccent) AppearanceEdited(); }
    partial void OnDensityIndexChanged(int value) { OnPropertyChanged(nameof(CompactDensity)); AppearanceEdited(); }
    partial void OnReducedMotionChanged(bool value) => AppearanceEdited();
    private void AppearanceEdited()
    {
        if (loadingAppearance || disposed) return;
        var current = Appearance;
        var key = SelectedAccent?.Key ?? AppearanceSettings.DefaultPreset;
        string? custom = current.CustomAccent;
        if (key == AppearanceSettings.CustomPreset)
        {
            if (!AppearanceSettings.TryNormalizeHex(CustomAccentText, out var hex))
            {
                AppearanceNeedsAttention = true;
                AppearanceFeedback = CustomAccentText.Trim().Length == 0 ? "Digite uma cor hexadecimal, por exemplo #3CC8B4." : "Cor inválida. Use o formato #RRGGBB ou #RGB; a cor anterior foi mantida.";
                return;
            }
            custom = hex;
        }
        var next = new AppearanceSettings((ThemeMode)Math.Clamp(ThemeModeIndex, 0, 2), key, custom, (UiDensity)Math.Clamp(DensityIndex, 0, 1), ReducedMotion);
        ApplyAppearance(next, persist: true);
    }
    private void ApplyAppearance(AppearanceSettings appearance, bool persist)
    {
        appearance = appearance.Normalize();
        settings = settings with { Appearance = appearance };
        var palette = theme?.Apply(appearance) ?? ThemePalette.Create(ThemeService.ResolveDark(appearance.Theme, null), appearance.AccentHex);
        AppearanceNeedsAttention = false;
        AppearanceFeedback = palette.AccentAdjusted
            ? $"Cor ajustada automaticamente no tema {(palette.IsDark ? "escuro" : "claro")} para manter textos e botões legíveis."
            : "Alterações aplicadas e salvas automaticamente.";
        OnPropertyChanged(nameof(EffectiveThemeLabel)); OnPropertyChanged(nameof(AppearanceSummary));
        if (persist && initialized) TrackBackground(SaveAsync());
    }
    [RelayCommand] private void SelectAccent(AccentChoice? choice) { if (choice is not null) SelectedAccent = choice; }
    [RelayCommand] private void RestoreAppearance()
    {
        loadingAppearance = true;
        try { ThemeModeIndex = 0; DensityIndex = 0; ReducedMotion = false; CustomAccentText = ""; SelectedAccent = AccentChoices[0]; }
        finally { loadingAppearance = false; }
        ApplyAppearance(new AppearanceSettings(), persist: true);
    }
}
