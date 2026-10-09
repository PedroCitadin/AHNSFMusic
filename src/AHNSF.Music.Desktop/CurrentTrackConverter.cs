using System.Globalization;
using AHNSF.Music.Core;
using Avalonia.Data.Converters;

namespace AHNSF.Music.Desktop;
public sealed class CurrentTrackConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count >= 2 && values[0] is Track row && values[1] is Track current && row.Path == current.Path;
}
