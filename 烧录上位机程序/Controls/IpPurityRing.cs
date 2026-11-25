using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace 烧录上位机程序.Controls;

/// <summary>纯净度评分环。Progress 为 0–100；Tone 取 none / ok / warn / bad。</summary>
public sealed class IpPurityRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(IpPurityRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(string), typeof(IpPurityRing), new FrameworkPropertyMetadata("none", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PrimaryTextProperty = DependencyProperty.Register(
        nameof(PrimaryText), typeof(string), typeof(IpPurityRing), new FrameworkPropertyMetadata("—", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryTextProperty = DependencyProperty.Register(
        nameof(SecondaryText), typeof(string), typeof(IpPurityRing), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public string Tone
    {
        get => (string)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public string PrimaryText
    {
        get => (string)GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    public string SecondaryText
    {
        get => (string)GetValue(SecondaryTextProperty);
        set => SetValue(SecondaryTextProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(120, 120);

    protected override void OnRender(DrawingContext context)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) size = 120;
        var center = new Point(size / 2, size / 2);
        var radius = size / 2 - 8;
        var track = new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        track.Freeze();
        context.DrawEllipse(null, track, center, radius, radius);

        var sweep = Math.Clamp(Progress, 0, 100) / 100d * 359.99;
        if (!string.Equals(Tone, "none", StringComparison.Ordinal) && sweep > 0.5)
        {
            var pen = new Pen(ToneBrush(Tone), 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            context.DrawGeometry(null, pen, Arc(center, radius, sweep));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var primary = new FormattedText(
            PrimaryText ?? "—", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            PrimaryText is { Length: > 4 } ? 16 : 28, ToneBrush(Tone == "none" ? "text" : Tone), dpi);
        var secondary = new FormattedText(
            SecondaryText ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)), dpi);
        context.DrawText(primary, new Point(center.X - primary.Width / 2, center.Y - primary.Height / 2 - 8));
        context.DrawText(secondary, new Point(center.X - secondary.Width / 2, center.Y + 10));
    }

    private static StreamGeometry Arc(Point center, double radius, double sweepDegrees)
    {
        var start = new Point(center.X, center.Y - radius);
        var radians = sweepDegrees * Math.PI / 180d;
        var end = new Point(center.X + radius * Math.Sin(radians), center.Y - radius * Math.Cos(radians));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Brush ToneBrush(string? tone)
    {
        var color = tone switch
        {
            "ok" => Color.FromRgb(0x0E, 0x70, 0x0E),
            "warn" => Color.FromRgb(0xC2, 0x41, 0x0C),
            "bad" => Color.FromRgb(0xC5, 0x0F, 0x1F),
            "text" => Color.FromRgb(0x1B, 0x1B, 0x1B),
            _ => Color.FromRgb(0x61, 0x61, 0x61)
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
