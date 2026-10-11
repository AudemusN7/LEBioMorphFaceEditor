using System.Windows;

namespace MorphFaceEditor.Infrastructure;

/// <summary>Keeps the translated window caption and the built application version together.</summary>
public static class WindowTitle
{
    public static string Version { get; } = typeof(WindowTitle).Assembly.GetName().Version!.ToString(4);

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.RegisterAttached(
        "Caption", typeof(string), typeof(WindowTitle), new PropertyMetadata("", CaptionChanged));

    public static string GetCaption(DependencyObject target) => (string)target.GetValue(CaptionProperty);
    public static void SetCaption(DependencyObject target, string value) => target.SetValue(CaptionProperty, value);

    private static void CaptionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is Window window)
            window.Title = $"{args.NewValue} · {Version}";
    }
}
