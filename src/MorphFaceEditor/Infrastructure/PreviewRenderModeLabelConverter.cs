using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Infrastructure;

public sealed class PreviewRenderModeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        HeadPreviewRenderMode.Diagnostic => Loc.Text("loc_RenderMode_Diagnostic"),
        HeadPreviewRenderMode.Shaded => Loc.Text("loc_RenderMode_Shaded"),
        HeadPreviewRenderMode.Unlit => Loc.Text("loc_RenderMode_Unlit"),
        _ => DependencyProperty.UnsetValue
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
