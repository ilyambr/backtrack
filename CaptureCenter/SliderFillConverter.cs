using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CaptureCenter;

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
