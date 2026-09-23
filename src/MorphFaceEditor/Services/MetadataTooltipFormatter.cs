namespace MorphFaceEditor.Services;

/// <summary>Builds the common tooltip shown for catalogue-backed editor controls.</summary>
public static class MetadataTooltipFormatter
{
    public static string Format(string name, string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? name
            : $"{name}\n{description.Trim()}";
}
