using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Backtrack;

/// <summary>
/// Splits a Slider's track into two Star-weighted grid columns (played / remaining)
/// so the "filled up to the thumb" look doesn't need the track's rendered width.
/// </summary>
public sealed class SliderFillConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double value = System.Convert.ToDouble(values[0]);
        double max = System.Convert.ToDouble(values[1]);
        double fraction = max > 0 ? Math.Clamp(value / max, 0, 1) : 0;
        if (parameter as string == "remain")
            fraction = 1 - fraction;
        return new GridLength(fraction, GridUnitType.Star);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ComboBoxItemTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return "";
        if (value is System.Windows.Controls.ComboBoxItem cbi)
            return cbi.Content?.ToString() ?? "";

        var nameProp = value.GetType().GetProperty("Name");
        if (nameProp != null)
        {
            var val = nameProp.GetValue(value);
            if (val != null) return val.ToString() ?? "";
        }

        var contentProp = value.GetType().GetProperty("Content");
        if (contentProp != null)
        {
            var val = contentProp.GetValue(value);
            if (val != null) return val.ToString() ?? "";
        }

        return value.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

