using System.Globalization;
using System.Windows.Data;

namespace Switch2ProManager.Services;

/// <summary>
/// Formats a bound number with a localized format string. The parameter is the resource key, e.g.
/// ConverterParameter=Lab.HighFreq, so the text follows the language without a DynamicResource inside the binding.
/// </summary>
public sealed class FormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string key ? AppText.Format(key, value) : value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
