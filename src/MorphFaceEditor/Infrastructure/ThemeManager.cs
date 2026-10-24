using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MorphFaceEditor.Controls;

namespace MorphFaceEditor.Infrastructure;

internal sealed class ThemeManager : IDisposable
{
    private readonly ResourceDictionary _palette;
    private readonly ApplicationPreferencesStore _preferences;
    private readonly IWindowsThemeSource _system;
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyDictionary<string, Color> _dark;
    private readonly IReadOnlyDictionary<string, Color> _light;
    private bool _disposed;
    public ThemeMode Mode { get; private set; }
    public ThemeMode EffectiveMode { get; private set; }
    public double? AccentHue { get; private set; }
    public event EventHandler? Changed;

    public ThemeManager(ResourceDictionary resources, ApplicationPreferencesStore preferences, IWindowsThemeSource system)
    {
        _palette = ThemeResources.Palette(resources);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _preferences = preferences;
        _system = system;
        _dark = ReadColours("Dark");
        _light = ReadColours("Light");
        if (Enum.IsDefined(preferences.Current.Mode) && ApplicationPreferencesStore.ValidHue(preferences.Current.AccentHue))
        {
            Mode = preferences.Current.Mode;
            AccentHue = preferences.Current.AccentHue;
        }
        _system.Changed += OnSystemChanged;
        Apply();
    }

    public static ThemeManager CreateDefault(ResourceDictionary resources) =>
        new(resources, ApplicationPreferencesStore.CreateDefault(), new WindowsThemeSource());

    internal static IReadOnlyDictionary<string, Color> ReadColours(string mode)
    {
        var resources = new ResourceDictionary
        {
            Source = new Uri($"/MorphFaceEditor;component/Themes/{mode}Colours.xaml", UriKind.Relative)
        };
        return resources.Keys.Cast<object>().OfType<string>()
            .ToDictionary(key => key.Replace("Default.", "Palette.", StringComparison.Ordinal),
                key => (Color)resources[key]);
    }

    public Color AuthoredAccent => Defaults["Palette.Accent"];
    public Color ViewportBackground => Defaults["Palette.PreviewHost"];
    private IReadOnlyDictionary<string, Color> Defaults => EffectiveMode == ThemeMode.Light ? _light : _dark;

    public void SetMode(ThemeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _preferences.Update(preferences => preferences with { Mode = mode, AccentHue = AccentHue });
        Mode = mode;
        Apply();
    }

    public void PreviewAccentHue(double? hue)
    {
        if (hue is { } value && !double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(hue));
        AccentHue = hue is { } angle ? NormalizeHue(angle) : null;
        Apply();
    }

    public void CommitAccentHue() => _preferences.Update(preferences => preferences with { Mode = Mode, AccentHue = AccentHue });

    private void OnSystemChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && Mode == ThemeMode.System) Apply();
        });
    }

    private void Apply()
    {
        EffectiveMode = Mode == ThemeMode.System ? _system.Mode : Mode;
        var defaults = Defaults;
        var delta = AccentHue is { } hue ? hue - Hsv(defaults["Palette.Accent"]).Hue : 0;
        foreach (var (key, colour) in defaults)
        {
            var result = AccentHue is not null && key is "Palette.Accent" or "Palette.SelectedRow"
                ? ShiftHue(colour, delta) : colour;
            ((ThemeColour)_palette[key]).Value = result;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static (double Hue, double Saturation, double Value) Hsv(Color colour) =>
        ColorWheelControl.ToHsv(colour.R / 255d, colour.G / 255d, colour.B / 255d);
    internal static double NormalizeHue(double hue) => (hue % 360 + 360) % 360;
    internal static Color ShiftHue(Color colour, double delta)
    {
        var hsv = Hsv(colour);
        var shifted = ColorWheelControl.FromHsv(NormalizeHue(hsv.Hue + delta), hsv.Saturation, hsv.Value);
        shifted.A = colour.A;
        return shifted;
    }

    public void Dispose()
    {
        _disposed = true;
        _system.Changed -= OnSystemChanged;
        _system.Dispose();
    }
}
