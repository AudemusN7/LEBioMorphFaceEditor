using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MorphFaceEditor.Controls;

public sealed class ColorWheelControl : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue), typeof(double), typeof(ColorWheelControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnColourChanged, CoerceHue));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation), typeof(double), typeof(ColorWheelControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnColourChanged, CoerceUnit));

    public event EventHandler? ColourChanged;

    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(size)) size = 220;
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 3);
        for (var degree = 0; degree < 360; degree += 2)
        {
            var start = PointOnCircle(center, radius, degree - 1);
            var end = PointOnCircle(center, radius, degree + 1.2);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(center, true, true);
                context.LineTo(start, true, false);
                context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            var edge = FromHsv(degree, 1, 1);
            var brush = new LinearGradientBrush(Colors.White, edge, center, PointOnCircle(center, radius, degree))
            {
                // The points above are device coordinates. Relative mapping interprets
                // values such as 122 as 12,200%, producing the white pinwheel wedges.
                MappingMode = BrushMappingMode.Absolute
            };
            brush.Freeze();
            drawingContext.DrawGeometry(brush, null, geometry);
        }

        drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1), center, radius, radius);
        var selected = PointOnCircle(center, radius * Saturation, Hue);
        drawingContext.DrawEllipse(Brushes.Transparent, new Pen(Brushes.White, 2), selected, 6, 6);
        drawingContext.DrawEllipse(null, new Pen(Brushes.Black, 1), selected, 7, 7);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            SetFromPoint(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            SetFromPoint(e.GetPosition(this));
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void SetFromPoint(Point point)
    {
        var x = point.X - ActualWidth / 2;
        var y = point.Y - ActualHeight / 2;
        var radius = Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 3);
        Saturation = Math.Clamp(Math.Sqrt(x * x + y * y) / radius, 0, 1);
        var degrees = Math.Atan2(y, x) * 180 / Math.PI;
        Hue = degrees < 0 ? degrees + 360 : degrees;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    public static Color FromHsv(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var match = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb(ToByte(r + match), ToByte(g + match), ToByte(b + match));
    }

    public static (double Hue, double Saturation, double Value) ToHsv(double red, double green, double blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == red
            ? 60 * (((green - blue) / delta) % 6)
            : max == green
                ? 60 * (((blue - red) / delta) + 2)
                : 60 * (((red - green) / delta) + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static void OnColourChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((ColorWheelControl)sender).ColourChanged?.Invoke(sender, EventArgs.Empty);

    private static object CoerceHue(DependencyObject sender, object value) => Math.Clamp((double)value, 0, 359.999);
    private static object CoerceUnit(DependencyObject sender, object value) => Math.Clamp((double)value, 0, 1);
    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);
}
