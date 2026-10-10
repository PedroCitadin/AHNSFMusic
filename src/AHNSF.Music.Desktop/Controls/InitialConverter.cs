using Avalonia.Data.Converters;

namespace AHNSF.Music.Desktop.Controls;

/// <summary>First letter of a name, for playlist cover placeholders.</summary>
public static class TextConverters
{
    public static readonly IValueConverter Initial = new FuncValueConverter<string?, string>(text =>
        string.IsNullOrWhiteSpace(text) ? "♫" : text.Trim()[..1].ToUpperInvariant());
}
