using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PortableDev.Gui.Infrastructure;

/// <summary>true -> visible, false -> collapsed. Pass "invert" as parameter to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility vis && vis == Visibility.Visible;
}

/// <summary>true -> ok green, false -> warn/bad red. Used for status glyphs.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ok = value is bool b && b;
        var key = ok ? "OkBrush" : (parameter as string == "warn" ? "WarnBrush" : "BadBrush");
        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true -> soft tinted background for badges.</summary>
public sealed class BoolToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ok = value is bool b && b;
        var key = ok ? "OkSoftBrush" : "BadSoftBrush";
        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>ChangeKind name -> accent colour for the diff marker.</summary>
public sealed class ChangeGlyphBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value?.ToString();
        var key = kind switch
        {
            "Add" => "OkBrush",
            "Update" => "WarnBrush",
            "Remove" => "BadBrush",
            _ => "TextFaintBrush",
        };
        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverse boolean, for "IsEnabled when not busy".</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>Empty or whitespace string -> collapsed; anything else -> visible.</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
