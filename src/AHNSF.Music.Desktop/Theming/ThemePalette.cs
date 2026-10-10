using AHNSF.Music.Core;
using Avalonia.Media;

namespace AHNSF.Music.Desktop.Theming;

/// <summary>
/// Semantic colour tokens for one theme variant and accent. Pure computation (no Avalonia application needed),
/// so contrast guarantees are unit-tested: the user may pick any accent, but text and controls stay legible.
/// </summary>
public sealed class ThemePalette
{
    public const double TextContrast = 4.5, ComponentContrast = 3.0;
    public static readonly string[] TokenNames =
    [
        "BackgroundPrimary", "BackgroundSecondary", "Surface", "SurfaceHover", "SurfaceSelected", "SurfaceRaised",
        "TextPrimary", "TextSecondary", "TextDisabled", "BorderSubtle", "BorderStrong",
        "AccentPrimary", "AccentHover", "AccentPressed", "AccentForeground", "AccentText", "AccentSubtle", "FocusRing",
        "Success", "Warning", "Error", "SuccessSubtle", "WarningSubtle", "ErrorSubtle", "Overlay"
    ];
    private readonly Dictionary<string, Color> tokens;
    public bool IsDark { get; }
    /// <summary>Accent as requested by the user (normalized hex).</summary>
    public string RequestedAccent { get; }
    /// <summary>True when the accent had to be lightened/darkened to keep the minimum contrast.</summary>
    public bool AccentAdjusted { get; }
    public IReadOnlyDictionary<string, Color> Tokens => tokens;
    public Color this[string token] => tokens[token];

    private ThemePalette(bool dark, string requested, bool adjusted, Dictionary<string, Color> values)
    { IsDark = dark; RequestedAccent = requested; AccentAdjusted = adjusted; tokens = values; }

    public static ThemePalette Create(bool dark, string? accentHex)
    {
        if (!AppearanceSettings.TryNormalizeHex(accentHex, out var requested)) requested = AppearanceSettings.Presets[0].Hex;
        var t = new Dictionary<string, Color>();
        if (dark)
        {
            Set(t, "BackgroundPrimary", "#121417"); Set(t, "BackgroundSecondary", "#0B0C0E"); Set(t, "Surface", "#1A1D21");
            Set(t, "SurfaceHover", "#24282D"); Set(t, "SurfaceRaised", "#202327"); Set(t, "TextPrimary", "#F2F4F6");
            Set(t, "TextSecondary", "#A6AEB8"); Set(t, "TextDisabled", "#626A74"); Set(t, "BorderSubtle", "#2A2F35");
            Set(t, "BorderStrong", "#3C434B"); Set(t, "Success", "#5FD49A"); Set(t, "Warning", "#F2B962"); Set(t, "Error", "#F38B8F");
            t["Overlay"] = Color.FromArgb(0xB8, 0x05, 0x06, 0x08);
        }
        else
        {
            Set(t, "BackgroundPrimary", "#F5F6F8"); Set(t, "BackgroundSecondary", "#EBEDF0"); Set(t, "Surface", "#FFFFFF");
            Set(t, "SurfaceHover", "#EEF0F3"); Set(t, "SurfaceRaised", "#FFFFFF"); Set(t, "TextPrimary", "#15181C");
            Set(t, "TextSecondary", "#535B65"); Set(t, "TextDisabled", "#9AA1A9"); Set(t, "BorderSubtle", "#DCE0E5");
            Set(t, "BorderStrong", "#C3C9D0"); Set(t, "Success", "#1D7A4C"); Set(t, "Warning", "#8F5600"); Set(t, "Error", "#BF2F37");
            t["Overlay"] = Color.FromArgb(0x80, 0x10, 0x14, 0x18);
        }
        var backgrounds = new[] { t["BackgroundPrimary"], t["BackgroundSecondary"], t["Surface"] };
        var original = Color.Parse(requested);
        // Dark theme: dark text on a light-enough accent. Light theme: white text on a dark-enough accent.
        var foreground = dark ? Color.Parse("#0B0F14") : Colors.White;
        var accent = Adjust(original, lighten: dark, c => Contrast(c, foreground) >= TextContrast && backgrounds.All(b => Contrast(c, b) >= ComponentContrast));
        t["AccentPrimary"] = accent; t["AccentForeground"] = foreground;
        t["AccentHover"] = Shift(accent, dark ? 0.06 : -0.06);
        t["AccentPressed"] = Shift(accent, dark ? -0.05 : 0.05);
        if (Contrast(t["AccentHover"], foreground) < TextContrast) t["AccentHover"] = accent;
        if (Contrast(t["AccentPressed"], foreground) < TextContrast) t["AccentPressed"] = accent;
        // Tinted surfaces: the tint is reduced (never removed) until regular text keeps its contrast on top of them.
        bool Readable(Color c) => Contrast(t["TextPrimary"], c) >= 7 && Contrast(t["TextSecondary"], c) >= TextContrast;
        t["SurfaceSelected"] = Tint(t["Surface"], accent, dark ? 0.20 : 0.14, Readable);
        t["AccentSubtle"] = Tint(t["BackgroundPrimary"], accent, dark ? 0.16 : 0.12, Readable);
        var textBackgrounds = backgrounds.Concat([t["SurfaceHover"], t["SurfaceSelected"], t["AccentSubtle"]]).ToArray();
        t["AccentText"] = Adjust(original, lighten: dark, c => textBackgrounds.All(b => Contrast(c, b) >= TextContrast));
        t["FocusRing"] = t["AccentText"];
        foreach (var tone in new[] { "Success", "Warning", "Error" })
        {
            var subtle = Mix(t["Surface"], t[tone], 0.14);
            t[tone + "Subtle"] = subtle;
            var surfaces = backgrounds.Append(subtle).ToArray();
            t[tone] = Adjust(t[tone], lighten: dark, c => surfaces.All(b => Contrast(c, b) >= TextContrast));
        }
        return new ThemePalette(dark, requested, accent != original, t);
    }

    private static void Set(Dictionary<string, Color> t, string key, string hex) => t[key] = Color.Parse(hex);

    public static double Luminance(Color c)
    {
        static double Channel(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
    /// <summary>WCAG 2.x contrast ratio, 1–21. Alpha is ignored (tokens used for text are opaque).</summary>
    public static double Contrast(Color a, Color b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
    public static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>Moves HSL lightness in one direction until the condition holds; black/white is the guaranteed end point.</summary>
    private static Color Adjust(Color color, bool lighten, Func<Color, bool> ok)
    {
        if (ok(color)) return color;
        var hsl = color.ToHsl();
        for (var l = hsl.L; l is >= 0 and <= 1; l += lighten ? 0.01 : -0.01)
        {
            var candidate = new HslColor(1, hsl.H, hsl.S, Math.Clamp(l, 0, 1)).ToRgb();
            if (ok(candidate)) return candidate;
        }
        return lighten ? Colors.White : Colors.Black;
    }
    private static Color Tint(Color surface, Color accent, double amount, Func<Color, bool> ok)
    {
        for (var a = amount; a > 0.04; a -= 0.02) { var candidate = Mix(surface, accent, a); if (ok(candidate)) return candidate; }
        return Mix(surface, accent, 0.04);
    }
    private static Color Shift(Color color, double delta)
    {
        var hsl = color.ToHsl();
        return new HslColor(1, hsl.H, hsl.S, Math.Clamp(hsl.L + delta, 0, 1)).ToRgb();
    }
}
