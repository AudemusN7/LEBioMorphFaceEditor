using System.IO;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Infrastructure;

internal static class UserFacingMessageFormatter
{
    internal static string Format(UserFacingMessage message) =>
        Loc.Format(message.LocalizationKey, message.Arguments.Select(argument =>
            argument is UserFacingMessage nested ? Format(nested) : argument).ToArray());

    internal static IReadOnlyList<string> FormatWarnings(MorphFaceSaveResult result)
    {
        var localized = result.UserWarnings.Where(message => message.DiagnosticText is not null)
            .GroupBy(message => message.DiagnosticText!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Format(group.First()), StringComparer.Ordinal);
        return result.Warnings.Select(warning => localized.TryGetValue(warning, out var value)
            ? value : FormatDiagnostic(warning)).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string Format(Exception exception)
    {
        if (UserFacingMessage.FromException(exception) is { } message) return Format(message);
        if (Loc.IsLocalisedError(exception)) return exception.Message;
        AppLog.Error("UI:TECHNICAL-FAILURE", exception);
        return Loc.Text(exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => "loc_Error_FileUnavailable",
            UnauthorizedAccessException => "loc_Error_AccessDenied",
            InvalidDataException or FormatException => "loc_Error_InvalidData",
            IOException => "loc_Error_FileOperationFailed",
            _ => "loc_Error_UnexpectedFailure"
        });
    }

    internal static string FormatDiagnostic(string diagnostic)
    {
        AppLog.Warning("UI:TECHNICAL-WARNING " + diagnostic);
        return Loc.Text("loc_Error_TechnicalWarning");
    }

    internal static string FormatEditBlockReason(MorphEditBlockReason? reason)
    {
        if (reason is null) return string.Empty;
        AppLog.Warning("GEOMETRY:EDIT-BLOCKED " + reason.Diagnostic);
        return reason.Kind switch
        {
            MorphEditBlockKind.ProfilePolicy => Loc.Text("loc_Editor_EditBlockedByProfile"),
            MorphEditBlockKind.UnresolvedFeatures => Loc.Format("loc_Editor_UnresolvedFeatures",
                string.Join(", ", reason.FeatureNames ?? [])),
            MorphEditBlockKind.ImportedTopology => Loc.Text("loc_Editor_ImportedTopologyMismatch"),
            MorphEditBlockKind.InvalidTargets => Loc.Text("loc_Editor_InvalidTargetTopology"),
            MorphEditBlockKind.OracleUnavailable => Loc.Text("loc_Editor_GeometryVerificationUnavailable"),
            MorphEditBlockKind.OracleMismatch => Loc.Format("loc_Editor_GeometryVerificationMismatch", reason.MaximumError),
            _ => Loc.Text("loc_Editor_ProfileValidationFailed")
        };
    }
}
