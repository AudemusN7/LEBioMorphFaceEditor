using System.Reflection;
using System.Text.Json;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

internal static class CrossGameAssetReconciliationCatalog
{
    private const string ResourceName = "MorphFaceEditor.CrossGameAssetReconciliation.json";
    private static readonly Lazy<IReadOnlyDictionary<string, Decision>> Decisions = new(Load);

    internal static bool TryResolve(
        MEGame sourceGame,
        MEGame targetGame,
        string kind,
        string? parameter,
        string sourcePath,
        out string? targetPath)
    {
        if (Decisions.Value.TryGetValue(Key(
                sourceGame.ToString(), targetGame.ToString(), kind, parameter, sourcePath), out var decision))
        {
            targetPath = NormalizeTargetPath(targetGame, decision.TargetPath);
            return true;
        }
        targetPath = null;
        return false;
    }

    internal static string? NormalizeTargetPath(MEGame targetGame, string? path)
    {
        if (path?.StartsWith(
                "BIOG_HMM_HED_PROMorph_R.", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "BIOG_HMM_HED_PROMorph." + path["BIOG_HMM_HED_PROMorph_R.".Length..];
        }
        return path;
    }

    internal static bool IsReviewedTexturePath(string path) => Decisions.Value.Values.Any(decision =>
        decision.Kind.Equals("Texture", StringComparison.OrdinalIgnoreCase) &&
        (decision.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
         decision.TargetPath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true));

    private static IReadOnlyDictionary<string, Decision> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidDataException(
                               $"Embedded cross-game reconciliation catalogue '{ResourceName}' is missing.");
        var decisions = JsonSerializer.Deserialize<Decision[]>(stream)
                        ?? throw new InvalidDataException("The cross-game reconciliation catalogue is empty.");
        return decisions.ToDictionary(
            value => Key(value.SourceGame, value.TargetGame, value.Kind, value.Parameter, value.SourcePath),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(
        string sourceGame,
        string targetGame,
        string kind,
        string? parameter,
        string sourcePath) =>
        $"{sourceGame}\u001f{targetGame}\u001f{kind}\u001f{parameter}\u001f{sourcePath}";

    private sealed record Decision(
        string SourceGame,
        string TargetGame,
        string Kind,
        string? Parameter,
        string SourcePath,
        string? TargetPath,
        int EvidenceCount);
}
