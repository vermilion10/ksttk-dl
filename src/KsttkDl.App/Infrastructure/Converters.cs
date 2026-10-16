using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KsttkDl.App.Infrastructure;

/// <summary> true when the bound enum's name equals the parameter. checking a nav medallion writes that value back, so a click, the arrow keys and accessibility tools all navigate the same way </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && targetType.IsEnum
            ? Enum.Parse(targetType, name)
            : Binding.DoNothing;
}

/// <summary>Visible for a non-null, non-empty value; "invert" as the parameter flips it.</summary>
public sealed class PresenceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true,
        };

        if (parameter as string == "invert")
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
