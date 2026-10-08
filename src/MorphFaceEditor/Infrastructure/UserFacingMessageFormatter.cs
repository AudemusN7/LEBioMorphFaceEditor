using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Infrastructure;

internal static class UserFacingMessageFormatter
{
    internal static string Format(UserFacingMessage message) =>
        Loc.Format(message.LocalizationKey, message.Arguments.ToArray());
}
