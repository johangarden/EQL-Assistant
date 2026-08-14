using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EQLOverlay;

/// <summary>Fraction (0..1) -> proportional star width for the filled part of a bar.</summary>
public sealed class FillStarConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double f = value is double d ? Math.Clamp(d, 0, 1) : 0;
        return new GridLength(f, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Fraction (0..1) -> proportional star width for the empty remainder.</summary>
public sealed class RestStarConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double f = value is double d ? Math.Clamp(d, 0, 1) : 0;
        return new GridLength(1 - f, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Multiplies its inputs (doubles) — bar height × the bar's own
/// height scale (HoT bars render taller than the rest).</summary>
public sealed class MultiplyConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        double result = 1;
        foreach (var v in values)
        {
            if (v is not double d || double.IsNaN(d)) return DependencyProperty.UnsetValue;
            result *= d;
        }
        return result;
    }

    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Spacing value (double) -> a bottom-only Thickness for gaps between bars.</summary>
public sealed class BottomSpacingConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        return new Thickness(0, 0, 0, v);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
