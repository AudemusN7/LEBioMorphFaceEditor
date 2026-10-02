using System.Windows;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class AccentColorPickerWindow : Window
{
    private readonly ThemeManager _theme;
    private readonly double? _originalHue;
    private bool _updating;
    private bool _accepted;

    internal AccentColorPickerWindow(ThemeManager theme)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _theme = theme;
        _originalHue = theme.AccentHue;
        theme.Changed += OnThemeChanged;
        UpdateWheel();
        Closed += (_, _) =>
        {
            theme.Changed -= OnThemeChanged;
            if (!_accepted) theme.PreviewAccentHue(_originalHue);
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateWheel();
    private void UpdateWheel()
    {
        _updating = true;
        try
        {
            var hsv = ThemeManager.Hsv(_theme.AuthoredAccent);
            Wheel.Saturation = hsv.Saturation;
            Wheel.Brightness = hsv.Value;
            Wheel.Hue = _theme.AccentHue ?? hsv.Hue;
        }
        finally { _updating = false; }
    }

    private void OnHueChanged(object sender, EventArgs e)
    {
        if (!_updating && _theme is not null) _theme.PreviewAccentHue(Wheel.Hue);
    }
    private void OnReset(object sender, RoutedEventArgs e) => _theme.PreviewAccentHue(null);
    private void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            _theme.CommitAccentHue();
            _accepted = true;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.Format("loc_AccentColorPickerWindow_CouldNotSaveAccentColour", exception.Message);
        }
    }
}
