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
    private bool _updating;

    public HdrColorPickerWindow(
        string title,
        Vector4 initial,
        bool allowHdr = true,
        bool extendedSliders = false)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _allowHdr = allowHdr;
        Title = title;
        if (!allowHdr)
        {
            Height = 550;
            MinHeight = 520;
            IntensityRow.Height = new GridLength(0);
            AlphaRow.Height = new GridLength(0);
            RedSlider.Maximum = 1;
            GreenSlider.Maximum = 1;
            BlueSlider.Maximum = 1;
            PreviewCaption.Text = "Preview background colour";
        }
        ConfigureChannelRange(RedSlider, initial.X, allowHdr, extendedSliders);
        ConfigureChannelRange(GreenSlider, initial.Y, allowHdr, extendedSliders);
        ConfigureChannelRange(BlueSlider, initial.Z, allowHdr, extendedSliders);
        ConfigureChannelRange(AlphaSlider, allowHdr ? initial.W : 1, allowHdr, extendedSliders);
        _updating = true;
        RedSlider.Value = initial.X;
        GreenSlider.Value = initial.Y;
        BlueSlider.Value = initial.Z;
        AlphaSlider.Value = allowHdr ? initial.W : 1;
        UpdateWheelFromRgb();
        _updating = false;
        UpdateSwatch();
        UpdateHexText();
    }

    private static void ConfigureChannelRange(
        System.Windows.Controls.Slider slider,
        float initial,
        bool allowHdr,
        bool extendedSliders)
    {
        var authoredMaximum = allowHdr ? 8d : 1d;
        if (extendedSliders)
        {
            var extent = Math.Max(authoredMaximum, Math.Abs(initial));
            slider.Minimum = -extent;
            slider.Maximum = extent;
            return;
        }

        // Keep legacy ranges by default without silently changing an existing
        // out-of-range value when an old extended edit is opened again.
        slider.Minimum = Math.Min(0, initial);
        slider.Maximum = Math.Max(authoredMaximum, initial);
    }

    public event EventHandler? ValueChanged;

    public Vector4 Value => new(
        (float)RedSlider.Value,
        (float)GreenSlider.Value,
        (float)BlueSlider.Value,
        (float)AlphaSlider.Value);

    private void OnWheelChanged(object sender, EventArgs e)
    {
        if (_updating || Wheel is null || ValueSlider is null || IntensitySlider is null ||
            RedSlider is null || GreenSlider is null || BlueSlider is null || AlphaSlider is null ||
            PreviewSwatch is null)
        {
            return;
        }
        _updating = true;
        var color = ColorWheelControl.FromHsv(Wheel.Hue, Wheel.Saturation, ValueSlider.Value);
        var intensity = _allowHdr ? IntensitySlider.Value : 1;
        RedSlider.Value = color.R / 255d * intensity;
        GreenSlider.Value = color.G / 255d * intensity;
        BlueSlider.Value = color.B / 255d * intensity;
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
        UpdateWheelFromRgb();
        _updating = false;
        Publish();
    }

    private void UpdateWheelFromRgb()
    {
        var maximum = Math.Max(RedSlider.Value, Math.Max(GreenSlider.Value, BlueSlider.Value));
        var intensity = _allowHdr ? Math.Clamp(maximum, 1, 8) : 1;
        IntensitySlider.Value = intensity;
        var hsv = ColorWheelControl.ToHsv(
            Math.Clamp(RedSlider.Value / intensity, 0, 1),
            Math.Clamp(GreenSlider.Value / intensity, 0, 1),
            Math.Clamp(BlueSlider.Value / intensity, 0, 1));
        Wheel.Hue = hsv.Hue;
        Wheel.Saturation = hsv.Saturation;
        ValueSlider.Value = Math.Clamp(hsv.Value, 0, 1);
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
        RedSlider.Value = color.X;
        GreenSlider.Value = color.Y;
        BlueSlider.Value = color.Z;
        if (color.W >= 0)
        {
            AlphaSlider.Value = color.W;
        }
        UpdateWheelFromRgb();
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
    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);
}
