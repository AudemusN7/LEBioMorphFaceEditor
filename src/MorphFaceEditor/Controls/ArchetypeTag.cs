using System.Windows;
using System.Windows.Controls;

namespace MorphFaceEditor.Controls;

/// <summary>Links recycled face-list tags to live theme brushes by their displayed code.</summary>
public static class ArchetypeTag
{
    internal sealed record Definition(string Code, string DisplayName)
    {
        public string ColourKey => $"Palette.Tag.{Code}";
        public string BrushKey => $"ArchetypeTag.{Code}";
    }

    internal static readonly IReadOnlyList<Definition> Definitions =
    [
        new("HMM", "Human Male [HMM]"),
        new("HMF", "Human Female [HMF]"),
        new("ASA", "Asari [ASA]"),
        new("SAL", "Salarian [SAL]"),
        new("TUR", "Turian [TUR]"),
        new("TUF", "Female Turian [TUF]"),
        new("KRO", "Krogan [KRO]"),
        new("BAT", "Batarian [BAT]"),
        new("ALN", "Vorcha [ALN]"),
        new("CUSTOM", "Custom [CUSTOM]"),
        new("MESH", "Mesh [MESH]")
    ];

    public static readonly DependencyProperty CodeProperty = DependencyProperty.RegisterAttached(
        "Code", typeof(string), typeof(ArchetypeTag), new PropertyMetadata(null, OnCodeChanged));

    public static string? GetCode(DependencyObject target) => (string?)target.GetValue(CodeProperty);
    public static void SetCode(DependencyObject target, string? value) => target.SetValue(CodeProperty, value);

    private static void OnCodeChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Border border) return;
        var code = (e.NewValue as string)?.Trim().Trim('[', ']').ToUpperInvariant();
        var definition = Definitions.FirstOrDefault(tag => tag.Code == code) ??
                         Definitions.Single(tag => tag.Code == "CUSTOM");
        border.SetResourceReference(Border.BackgroundProperty, definition.BrushKey);
    }
}
