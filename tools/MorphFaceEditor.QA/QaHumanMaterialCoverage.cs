using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

internal sealed record QaHumanMaterialRoute(string Group, MorphFaceGame Game, string Family,
    string? SourceKey = null, bool Player = false)
{
    internal string Id => $"{Group}/{Game.ToString().ToLowerInvariant()}-{Family.ToLowerInvariant()}-" +
        (Player ? "player" : "npc") + (SourceKey is null ? "" : "/" + SourceKey);
}

/// <summary>Required routes shared by the runnable matrix and its lightweight coverage guard.</summary>
internal static class QaHumanMaterialCoverage
{
    internal static IReadOnlyList<QaHumanMaterialRoute> All { get; } = Build();

    private static IReadOnlyList<QaHumanMaterialRoute> Build()
    {
        var routes = new List<QaHumanMaterialRoute>();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            foreach (var family in new[] { "HMM", "HMF" })
            {
                routes.Add(new("custom-human-materials", game, family));
                if (game == MorphFaceGame.LE3) routes.Add(new("cc-scalp-switch", game, family));
            }
            var hairKeys = game == MorphFaceGame.LE3
                ? new[] { "human-hair", "human-advanced-hair", "human-james-hair" }
                : new[] { "human-hair", "human-iconic-femshep-hair" };
            foreach (var sourceKey in hairKeys)
            {
                var family = sourceKey == "human-iconic-femshep-hair" ? "HMF" : "HMM";
                routes.Add(new("custom-human-hair", game, family, sourceKey));
                routes.Add(new("native-human-hair", game, family, sourceKey));
            }
        }
        foreach (var family in new[] { "HMM", "HMF" })
            routes.Add(new("player-cc-scalp", MorphFaceGame.LE3, family, Player: true));
        routes.Add(new("kaidan-preview", MorphFaceGame.LE3, "HMM"));
        return routes.ToArray();
    }
}
