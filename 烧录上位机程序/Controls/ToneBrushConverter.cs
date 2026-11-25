using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace 烧录上位机程序.Controls;

public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = (value as string) switch
        {
            "ok" => Color.FromRgb(0x0E, 0x70, 0x0E),
            "warn" => Color.FromRgb(0xC2, 0x41, 0x0C),
            "bad" => Color.FromRgb(0xC5, 0x0F, 0x1F),
            _ => Color.FromRgb(0x61, 0x61, 0x61)
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TextVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ToneBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = (value as string) switch
        {
            "ok" => Color.FromRgb(0xE7, 0xF5, 0xE7),
            "warn" => Color.FromRgb(0xFF, 0xF4, 0xE5),
            "bad" => Color.FromRgb(0xFD, 0xE7, 0xE9),
            _ => Color.FromRgb(0xF3, 0xF3, 0xF3)
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
