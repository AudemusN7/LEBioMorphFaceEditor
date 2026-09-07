using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

internal enum MaterialisationIssueSeverity { Warning, Fatal }

/// <summary>Evidence emitted for each root and relinker report, including the affected destination state.</summary>
internal sealed record MaterialisationDiagnostic(
    MEGame SourceGame, MEGame TargetGame, string AssetKind, string RootPath,
    string? EntryPath, string? EntryClass, string Message, string ReferenceState,
    MaterialisationIssueSeverity Severity, bool Verified);

/// <summary>Observes materialisation in corpus tooling without coupling package operations to a log file.</summary>
internal static class MaterialisationDiagnostics
{
    internal static readonly AsyncLocal<Action<MaterialisationDiagnostic>?> Observer = new();
}
