using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;
using MorphFaceEditor.Controls;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class HdrColorPickerWindow : Window
{
    private readonly bool _allowHdr;
    private readonly Vector4? _resetColor;
    private bool _updating;
    private Vector4 _value;

    public HdrColorPickerWindow(
        string title,
        Vector4 initial,
        bool allowHdr = true,
        bool extendedSliders = false,
        Vector4? resetColor = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _allowHdr = allowHdr;
        _resetColor = resetColor;
        _value = allowHdr ? initial : initial with { W = 1 };
        Title = title;
        _updating = true;
        if (!allowHdr)
        {
            Height = 650;
            MinHeight = 620;
            AlphaRow.Height = new GridLength(0);
            HdrIntensityPanel.Visibility = Visibility.Collapsed;
            PreviewCaption.Text = "Preview background colour";
            ResetBackgroundButton.Visibility = resetColor is null ? Visibility.Collapsed : Visibility.Visible;
        }
        var channelMinimum = extendedSliders ? -1 : 0;
        RedSlider.Minimum = channelMinimum;
        GreenSlider.Minimum = channelMinimum;
        BlueSlider.Minimum = channelMinimum;
        AlphaSlider.Minimum = channelMinimum;
        BrightnessSlider.Minimum = channelMinimum;
        LoadRgbControls(_value.X, _value.Y, _value.Z);
        AlphaSlider.Value = Math.Clamp(_value.W, channelMinimum, 1);
        _updating = false;
        UpdateSwatch();
        UpdateHexText();
    }

    public event EventHandler? ValueChanged;

    public Vector4 Value => _value;

    private void OnWheelChanged(object sender, EventArgs e)
    {
        if (_updating || Wheel is null || BrightnessSlider is null || IntensitySlider is null ||
            RedSlider is null || GreenSlider is null || BlueSlider is null || AlphaSlider is null ||
            PreviewSwatch is null)
        {
            return;
        }
        _updating = true;
        if (ReferenceEquals(sender, Wheel))
        {
            var color = ColorWheelControl.FromHsv(Wheel.Hue, Wheel.Saturation, 1);
            RedSlider.Value = color.R / 255d;
            GreenSlider.Value = color.G / 255d;
            BlueSlider.Value = color.B / 255d;
        }
        UpdateWheelBrightness();
        UpdateRgbValue();
        _updating = false;
        Publish();
    }

    private void OnRgbChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || PreviewSwatch is null)
        {
            return;
        }
        _updating = true;
        if (ReferenceEquals(sender, AlphaSlider))
        {
            _value.W = (float)AlphaSlider.Value;
        }
        else
        {
            UpdateWheelFromRgb();
            UpdateRgbValue();
        }
        _updating = false;
        Publish();
    }

    private void LoadRgbControls(float red, float green, float blue)
    {
        var maximum = Math.Max(Math.Abs(red), Math.Max(Math.Abs(green), Math.Abs(blue)));
        var intensity = _allowHdr ? Math.Clamp(maximum, 1, 8) : 1;
        IntensitySlider.Value = intensity;
        BrightnessSlider.Value = Math.Clamp(maximum / intensity, 0, 1);
        UpdateWheelBrightness();
        var divisor = maximum > 0 ? maximum : 1;
        RedSlider.Value = maximum > 0 ? Math.Clamp(red / divisor, RedSlider.Minimum, 1) : 1;
        GreenSlider.Value = maximum > 0 ? Math.Clamp(green / divisor, GreenSlider.Minimum, 1) : 1;
        BlueSlider.Value = maximum > 0 ? Math.Clamp(blue / divisor, BlueSlider.Minimum, 1) : 1;
        UpdateWheelFromRgb();
    }

    private void UpdateRgbValue()
    {
        var multiplier = RgbMultiplier;
        _value.X = (float)(RedSlider.Value * multiplier);
        _value.Y = (float)(GreenSlider.Value * multiplier);
        _value.Z = (float)(BlueSlider.Value * multiplier);
    }

    private double RgbMultiplier => BrightnessSlider.Value * (_allowHdr ? IntensitySlider.Value : 1);

    private void UpdateWheelBrightness() => Wheel.Brightness = Math.Clamp(RgbMultiplier, 0, 1);

    private void UpdateWheelFromRgb()
    {
        var hsv = ColorWheelControl.ToHsv(
            Math.Clamp(RedSlider.Value, 0, 1),
            Math.Clamp(GreenSlider.Value, 0, 1),
            Math.Clamp(BlueSlider.Value, 0, 1));
        Wheel.Hue = hsv.Hue;
        Wheel.Saturation = hsv.Saturation;
    }

    private void Publish()
    {
        UpdateSwatch();
        UpdateHexText();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSwatch()
    {
        var value = Value;
        PreviewSwatch.Background = new SolidColorBrush(Color.FromArgb(
            byte.MaxValue, ToByte(value.X), ToByte(value.Y), ToByte(value.Z)));
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHexOrRestore();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateHexText(force: true);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnHexLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyHexOrRestore();

    private void ApplyHexOrRestore()
    {
        if (!TryParseHex(HexTextBox.Text, out var color))
        {
            UpdateHexText(force: true);
            return;
        }

        _updating = true;
        LoadRgbControls(color.X, color.Y, color.Z);
        if (color.W >= 0)
        {
            AlphaSlider.Value = color.W;
            _value.W = color.W;
        }
        _value.X = color.X;
        _value.Y = color.Y;
        _value.Z = color.Z;
        _updating = false;
        Publish();
    }

    private void UpdateHexText(bool force = false)
    {
        if (!force && HexTextBox.IsKeyboardFocusWithin)
        {
            return;
        }
        var value = Value;
        HexTextBox.Text = _allowHdr
            ? $"#{ToByte(value.X):X2}{ToByte(value.Y):X2}{ToByte(value.Z):X2}{ToByte(value.W):X2}"
            : $"#{ToByte(value.X):X2}{ToByte(value.Y):X2}{ToByte(value.Z):X2}";
    }

    public static bool TryParseHex(string? text, out Vector4 color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 3 or 4 or 6 or 8 } ||
            !hex.All(Uri.IsHexDigit))
        {
            return false;
        }
        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Select(character => new string(character, 2)));
        }
        var red = Convert.ToByte(hex[..2], 16) / 255f;
        var green = Convert.ToByte(hex.Substring(2, 2), 16) / 255f;
        var blue = Convert.ToByte(hex.Substring(4, 2), 16) / 255f;
        var alpha = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) / 255f : -1;
        color = new Vector4(red, green, blue, alpha);
        return true;
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnResetBackground(object sender, RoutedEventArgs e)
    {
        if (_resetColor is not { } color) return;
        _updating = true;
        LoadRgbControls(color.X, color.Y, color.Z);
        _value = color;
        _updating = false;
        Publish();
    }
    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);
}
