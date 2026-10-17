using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Infrastructure;

internal static class UserFacingMessageFormatter
{
    internal static string Format(UserFacingMessage message) =>
        Loc.Format(message.LocalizationKey, message.Arguments.ToArray());

    internal static IReadOnlyList<string> FormatWarnings(MorphFaceSaveResult result)
    {
        var localized = result.UserWarnings.Where(message => message.DiagnosticText is not null)
            .GroupBy(message => message.DiagnosticText!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Format(group.First()), StringComparer.Ordinal);
        return result.Warnings.Select(warning => localized.GetValueOrDefault(warning, warning)).ToArray();
    }

    internal static string Format(Exception exception) =>
        UserFacingMessage.FromException(exception) is { } message ? Format(message) : exception.Message;
}
