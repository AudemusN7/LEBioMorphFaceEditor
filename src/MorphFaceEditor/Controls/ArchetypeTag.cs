using System.Windows;
using System.Windows.Controls;
using MorphFaceEditor.Infrastructure;

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
        new("HMM", Loc.Text("loc_ArchetypeTag_HumanMale")),
        new("HMF", Loc.Text("loc_ArchetypeTag_HumanFemale")),
        new("ASA", Loc.Text("loc_ArchetypeTag_Asari")),
        new("SAL", Loc.Text("loc_ArchetypeTag_Salarian")),
        new("TUR", Loc.Text("loc_ArchetypeTag_Turian")),
        new("TUF", Loc.Text("loc_ArchetypeTag_FemaleTurian")),
        new("KRO", Loc.Text("loc_ArchetypeTag_Krogan")),
        new("BAT", Loc.Text("loc_ArchetypeTag_Batarian")),
        new("ALN", Loc.Text("loc_ArchetypeTag_Vorcha")),
        new("CUSTOM", Loc.Text("loc_ArchetypeTag_Custom")),
        new("MESH", Loc.Text("loc_ArchetypeTag_Mesh"))
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
