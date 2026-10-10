namespace AHNSF.Music.Core;

public enum ThemeMode { Dark, Light, System }
public enum UiDensity { Comfortable, Compact }
public sealed record AccentPreset(string Key, string Name, string Hex);

/// <summary>Persisted visual preferences (inside the existing Settings JSON). Applying them is the Desktop ThemeService's job.</summary>
public sealed record AppearanceSettings(ThemeMode Theme = ThemeMode.Dark, string AccentPreset = AppearanceSettings.DefaultPreset,
    string? CustomAccent = null, UiDensity Density = UiDensity.Comfortable, bool ReducedMotion = false)
{
    public const string DefaultPreset = "ahnsf";
    public const string CustomPreset = "custom";
    public static IReadOnlyList<AccentPreset> Presets { get; } =
    [
        new("ahnsf", "Verde-água AHNSF", "#3CC8B4"), new("blue", "Azul", "#4C8DF6"), new("purple", "Roxo", "#9B7BF2"),
        new("orange", "Laranja", "#F08A3C"), new("pink", "Rosa", "#EC6AA8"), new("red", "Vermelho", "#E5484D"), new("neutral", "Neutro", "#9AA4AF")
    ];

    /// <summary>Hex chosen by the user, before any contrast adjustment. Invalid custom values fall back to the AHNSF preset.</summary>
    public string AccentHex => AccentPreset == CustomPreset && TryNormalizeHex(CustomAccent, out var custom) ? custom
        : (Presets.FirstOrDefault(p => p.Key == AccentPreset) ?? Presets[0]).Hex;

    /// <summary>Repairs values from older or hand-edited settings: unknown enums, presets or hex become defaults.</summary>
    public AppearanceSettings Normalize()
    {
        var theme = Enum.IsDefined(Theme) ? Theme : ThemeMode.Dark;
        var density = Enum.IsDefined(Density) ? Density : UiDensity.Comfortable;
        var custom = TryNormalizeHex(CustomAccent, out var hex) ? hex : null;
        var preset = AccentPreset == CustomPreset ? (custom is null ? DefaultPreset : CustomPreset)
            : Presets.Any(p => p.Key == AccentPreset) ? AccentPreset : DefaultPreset;
        return this with { Theme = theme, Density = density, AccentPreset = preset, CustomAccent = custom };
    }

    /// <summary>Accepts #RGB or #RRGGBB (the # is optional); returns uppercase #RRGGBB. Alpha and names are rejected.</summary>
    public static bool TryNormalizeHex(string? value, out string normalized)
    {
        normalized = "";
        var text = value?.Trim() ?? "";
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 3) text = string.Concat(text.Select(c => $"{c}{c}"));
        if (text.Length != 6 || !text.All(Uri.IsHexDigit)) return false;
        normalized = "#" + text.ToUpperInvariant();
        return true;
    }
}
