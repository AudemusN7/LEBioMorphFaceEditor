namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// A user-facing message identified by a localisation resource key. The package
/// layer carries the key and arguments; the WPF application supplies the text.
/// </summary>
public sealed record UserFacingMessage(
    string LocalizationKey,
    IReadOnlyList<object?> Arguments,
    string? DiagnosticText = null)
{
    public static UserFacingMessage Create(string key, params object?[] arguments) => new(key, arguments);

    public static UserFacingMessage CreateWarning(string key, string diagnosticText, params object?[] arguments) =>
        new(key, arguments, diagnosticText);
}
