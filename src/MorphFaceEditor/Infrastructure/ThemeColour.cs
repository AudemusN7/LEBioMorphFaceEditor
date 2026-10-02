using System.Windows;
using System.Windows.Media;

namespace MorphFaceEditor.Infrastructure;

// Explicit binding sources keep shared semantic brushes live, even when they are
// used by multiple windows or native title bars without an inheritance context.
public sealed class ThemeColour : DependencyObject
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(Color), typeof(ThemeColour), new PropertyMetadata(Colors.Transparent));

    public Color Value
    {
        get => (Color)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
