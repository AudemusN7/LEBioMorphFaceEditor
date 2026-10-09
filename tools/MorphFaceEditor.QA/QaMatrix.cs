using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.TestData;
using System.Text.Json.Serialization;

namespace MorphFaceEditor.QA;

internal sealed record QaFaceSpec(MorphFaceGame Game, string Family, string? FacePath,
    StandalonePlayerSex? PlayerSex = null)
{
    public bool IsPlayer => PlayerSex is not null;
    public string Id => $"{Game.ToString().ToLowerInvariant()}-{Family.ToLowerInvariant()}" +
        (Family is "HMM" or "HMF" ? IsPlayer ? "-player" : "-npc" : "");
    public MorphFaceGeometryMode? GeometryMode => IsPlayer ? MorphFaceGeometryMode.RelativeBake : null;
    public string ProfileSuffix => Family switch
    {
        "HMM" => "human-male", "HMF" => "human-female", "ASA" => "asari", "SAL" => "salarian",
        "TUR" => "turian", "TUF" => "female-turian", "KRO" => "krogan", "BAT" => "batarian", "BAF" => "female-batarian", "ALN" => "vorcha",
        _ => throw new ArgumentException("Unknown QA archetype: " + Family)
    };
    public string Package(QaContext c)
    {
        var path = IsPlayer ? StandalonePlayerMorphImportService.ResolveInstalledSeed(Game)
            : Family == "TUF" ? Path.Combine(c.Corpus, "TUF", $"{Game} TUF.pcc")
            : Family == "BAF" ? Path.Combine(c.Corpus, "BAF", $"BAF {Game}.pcc") : c.CorpusPackage(Game);
        c.TrackInput(path);
        return path;
    }
    [JsonIgnore]
    public string Selector => IsPlayer ? StandalonePlayerMorphImportService.ResolvePlayerTemplate(Game, PlayerSex!.Value)
        : FacePath ?? throw new InvalidOperationException("This fixture selects its exact face from its dedicated PCC.");
}

internal static partial class QaCases
{
    internal static QaFaceSpec DefaultFace { get; } = new(MorphFaceGame.LE2, "SAL", "SAL.BioFace_male_extra2");
    internal static QaFaceSpec MeshFace { get; } = new(MorphFaceGame.LE2, "HMM", "HMM.BioFace_Security2");
    internal static IReadOnlyList<QaFaceSpec> Archetypes { get; } = BuildArchetypes();

    private static IReadOnlyList<QaFaceSpec> BuildArchetypes()
    {
        var result = ConversionMatrix.Sources.GroupBy(source => (source.SourceGame, source.Family))
            .Select(group => group.First()).Select(source => new QaFaceSpec(source.SourceGame, source.Family, source.FacePath)).ToList();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            result.Add(new(game, "TUF", null));
            result.Add(new(game, "BAF", null));
            result.Add(new(game, "HMM", null, StandalonePlayerSex.Male));
            result.Add(new(game, "HMF", null, StandalonePlayerSex.Female));
        }
        return result.OrderBy(face => face.Game).ThenBy(face => face.Id).ToArray();
    }

    private static IReadOnlyList<QaCase> BuildMatrix()
    {
        var cases = new List<QaCase>();
        void Add(string group, string description, QaFaceSpec face, Func<QaContext, Task> run,
            string? variant = null) =>
            cases.Add(new(group + "/" + face.Id + (variant is null ? "" : "/" + variant),
                description + " — " + face.Id, async c =>
            {
                c.Face = face;
                c.PreparedPlayer = null;
                c.Current.Evidence["archetype"] = face;
                await c.WarmGame(face.Game);
                if (face.IsPlayer) await PreparePlayer(c);
                await run(c);
            }, group));
        foreach (var group in new[] { "load", "material-ron", "clipboard", "undo-redo", "benchmark" })
            foreach (var face in Archetypes)
                Add(group, group, face, group switch
                {
                    "load" => Load, "material-ron" => MaterialRon, "clipboard" => Clipboard,
                    "undo-redo" => UndoRedo, _ => Benchmark
                });
        foreach (var source in ConversionMatrix.Sources)
            foreach (var target in ConversionMatrix.TargetGames(source.SourceGame, source.Family))
            {
                var face = new QaFaceSpec(source.SourceGame, source.Family, source.FacePath);
                var id = $"conversion/{source.SourceGame}-to-{target}/{source.FacePath}";
                cases.Add(new(id, $"Corpus conversion {source.SourceGame} → {target} / {source.Family}", async c =>
                {
                    c.Face = face;
                    c.PreparedPlayer = null;
                    c.Current.Evidence["archetype"] = face;
                    await c.WarmGame(face.Game);
                    await Conversion(c, target);
                }, "conversion"));
            }
        if (cases.Count(item => item.Group == "conversion") != ConversionMatrix.ExpectedCount)
            throw new InvalidOperationException("Shared conversion matrix no longer contains 68 directions.");
        foreach (var face in Archetypes.Where(face => face.Family == "BAF"))
            foreach (var target in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }.Where(game => game != face.Game))
                Add("conversion", $"BAF conversion {face.Game} → {target}", face,
                    c => Conversion(c, target), target.ToString().ToLowerInvariant());
        foreach (var face in Archetypes.Where(face => !face.IsPlayer))
            Add("npc-randomisation", "One 100% production roll, export/reopen/render", face, NpcRandomisation);
        foreach (var face in Archetypes.Where(face => face.IsPlayer))
            Add("player-randomisation", "Standalone Player RON import, 100% roll, export/reopen/render", face, PlayerRandomisation);
        // Each interchange and Custom case builds its own seeded input.
        foreach (var format in new[] { MorphMeshFormat.Psk, MorphMeshFormat.Gltf })
            Add("mesh-" + format.ToString().ToLowerInvariant(), "Randomised HMM baked mesh export and Custom reimport",
                MeshFace, c => MeshRoundTrip(c, format));
        Add("custom-randomisation", "Four-slot HMM custom head with random material families", MeshFace, CustomRandomisation);
        foreach (var route in QaHumanMaterialCoverage.All)
        {
            var face = Archetypes.Single(value => value.Game == route.Game && value.Family == route.Family &&
                value.IsPlayer == route.Player);
            var description = route.Group switch
            {
                "custom-human-materials" => "Every Human Face/Scalp NPC/Player choice, edit/RON/history/PCC",
                "custom-human-hair" => "Explicit hair graph with shared scalp controls, textures, 100 rolls and PCC readback",
                "native-human-hair" => "Native attachment graph, BMO hair controls, history, 100 rolls and PCC readback",
                "cc-scalp-switch" => "ShortAfro paired texture/material transaction, existing save and fresh export",
                "player-cc-scalp" => "Official LE3 Player scalp defaults to the CC graph",
                "kaidan-preview" => "Native Kaidan graph renders while remaining outside authoring capabilities",
                _ => throw new InvalidOperationException("Unknown Human material QA group: " + route.Group)
            };
            Func<QaContext, Task> run = route.Group switch
            {
                "custom-human-materials" => c => HumanCustomMaterials(c,
                    route.Family == "HMM" ? "human-male" : "human-female"),
                "custom-human-hair" => c => HumanCustomHair(c, route.SourceKey!),
                "native-human-hair" => c => NativeHairMaterials(c, route.SourceKey!),
                "cc-scalp-switch" => NativeCcScalpSwitch,
                "player-cc-scalp" => PlayerCcScalpDefault,
                "kaidan-preview" => KaidanPreviewBoundary,
                _ => throw new InvalidOperationException("Unknown Human material QA group: " + route.Group)
            };
            Add(route.Group, description, face, run, route.SourceKey);
            if (cases[^1].Id != route.Id)
                throw new InvalidOperationException("Human material QA route registration changed its stable ID.");
        }
        cases.Add(new("pcc-audit", "Fresh structural/reference/installed-identity audit of every output PCC", c =>
        {
            c.Face = DefaultFace;
            c.PreparedPlayer = null;
            return PackageAudit(c);
        }, "pcc-audit"));
        return cases;
    }
}
