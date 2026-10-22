using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;
using System.Text.Json.Serialization;

namespace MorphFaceEditor.QA;

internal sealed record QaFaceSpec(MorphFaceGame Game, string Family, string? FacePath = null,
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
    internal static QaFaceSpec DefaultFace { get; } = new(MorphFaceGame.LE2, "SAL");
    internal static QaFaceSpec MeshFace { get; } = new(MorphFaceGame.LE2, "HMM", "HMM.BioFace_Security2");
    internal static IReadOnlyList<QaFaceSpec> Archetypes { get; } = BuildArchetypes();

    private static IReadOnlyList<QaFaceSpec> BuildArchetypes()
    {
        var result = new List<QaFaceSpec>();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            foreach (var family in new[] { "HMM", "HMF", "ASA", "SAL", "TUR", "KRO", "BAT", "ALN", "TUF", "BAF" })
                if (family != "ALN" || game != MorphFaceGame.LE1)
                    result.Add(new(game, family));
            result.Add(new(game, "HMM", PlayerSex: StandalonePlayerSex.Male));
            result.Add(new(game, "HMF", PlayerSex: StandalonePlayerSex.Female));
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
        // Source() samples by family, so repeated fixed-donor labels tested the same
        // route several times without actually selecting those named donors.
        foreach (var face in Archetypes.Where(face => !face.IsPlayer && face.Family != "TUF"))
            foreach (var target in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }
                         .Where(game => game != face.Game && (face.Family != "ALN" || game != MorphFaceGame.LE1)))
                Add("conversion", $"{face.Family} conversion {face.Game} → {target}", face,
                    c => Conversion(c, target), target.ToString().ToLowerInvariant());
        foreach (var face in Archetypes.Where(face => !face.IsPlayer))
            Add("npc-randomisation", "Dedicated 100-roll production stress, export/reopen/render", face, NpcRandomisation);
        foreach (var face in Archetypes.Where(face => face.IsPlayer))
            Add("player-randomisation", "Standalone Player RON import, dedicated 100-roll stress, export/reopen/render", face, PlayerRandomisation);
        foreach (var face in Archetypes)
        {
            Add("ron-full", "Full RON export/import/save/reopen, or explicit unsupported capability", face, c => RonRoundTrip(c, false));
            Add("ron-morph", "Morph-only RON export/import/save/reopen, or explicit unsupported capability", face, c => RonRoundTrip(c, true));
            if (!face.IsPlayer)
                Add("donor-sweep", "Every eligible corpus donor: load/export/reopen/value preservation", face, DonorSweep);
            if (face.Game is MorphFaceGame.LE1 or MorphFaceGame.LE2 && face.Family is not "ALN" and not "TUF" and not "BAF")
            {
                var target = face.Game == MorphFaceGame.LE1 ? MorphFaceGame.LE2 : MorphFaceGame.LE1;
                Add("ron-cross-game", "Full RON between compatible LE1/LE2 games", face, c => CrossGameRon(c, target));
            }
        }
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            foreach (var career in new[] { "Male-Custom", "Female-Custom", "Default" })
            {
                var name = $"{game}-{career}";
                var face = Archetypes.Single(value => value.Game == game && value.IsPlayer &&
                    value.PlayerSex == (career == "Female-Custom" ? StandalonePlayerSex.Female : StandalonePlayerSex.Male));
                // PCSAV owns its native/default fixture, so it needs no pre-randomised Player PCC.
                cases.Add(new("pcsav/" + name.ToLowerInvariant(), "Career RON/randomisation/save/reopen: " + name,
                    async c => { c.PreparedPlayer = null; c.Face = face; await PcsavJourney(c, name + ".pcsav"); }, "pcsav"));
            }
            var fixtures = game == MorphFaceGame.LE3
                ? new[] { "LE3_Customizable_Sheploo", "LE3_HMF_HED_Iconic_HairIconic" }
                : new[] { "LE1-2_Customizable_Sheploo", "LE1-2_Default_Jane_ashleyhair", "LE1-2_Default_Jane_iconichair" };
            foreach (var fixture in fixtures)
            {
                var face = Archetypes.Single(value => value.Game == game && value.IsPlayer &&
                    value.PlayerSex == (fixture.Contains("Sheploo") ? StandalonePlayerSex.Male : StandalonePlayerSex.Female));
                cases.Add(new($"tse-ron/{game.ToString().ToLowerInvariant()}/{fixture}", "External TSE RON import/export/persistence",
                    async c => { c.PreparedPlayer = null; c.Face = face; await c.WarmGame(game); await TseRonJourney(c, fixture); }, "tse-ron"));
            }
            foreach (var kind in new[] { "hair", "accessory", "texture" })
                Add("ron-missing-assets", "NPC RON missing " + kind,
                    Archetypes.Single(value => value.Game == game && value.Family == "HMF" && !value.IsPlayer),
                    c => MissingRonAssets(c, kind), kind);
        }
        foreach (var extra in new[]
        {
            (File: "LE3_HMM_Key.pcsav", Reason: "LE3 male authored head with 32 rather than 33 bone entries"),
            (File: "LE3_HMF_Commander.pcsav", Reason: "LE3 female single-LOD, zero-rich authored feature map"),
            (File: "LE2_HMF_Dahlia.pcsav", Reason: "LE2 female single-LOD head with explicit zero features"),
            (File: "LE1_HMF_Jane.pcsav", Reason: "LE1 female default head; curated LE1 default is male"),
            (File: "LE3_HMF_Jane.pcsav", Reason: "LE3 female default head; curated LE3 default is male"),
            (File: "LE1_HMM_Alistair.pcsav", Reason: "Incomplete native LOD payload: controlled refusal boundary")
        })
        {
            var path = extra.File;
            cases.Add(new("pcsav-extra/" + path, extra.Reason, async c =>
            {
                c.PreparedPlayer = null;
                c.Current.Evidence["additionalCoverageReason"] = extra.Reason;
                var input = Path.Combine(c.Root, "fixtures", "PCSAV_Test", path);
                if (path == "LE1_HMM_Alistair.pcsav") await RejectedCareer(c, input);
                else await PcsavJourney(c, path);
            }, "pcsav-extra"));
        }
        // Each interchange and Custom case builds its own seeded input.
        foreach (var format in new[] { MorphMeshFormat.Psk, MorphMeshFormat.Gltf })
            Add("mesh-" + format.ToString().ToLowerInvariant(), "Edited HMM baked mesh export and Custom reimport",
                MeshFace, c => MeshRoundTrip(c, format));
        Add("custom-randomisation", "Four-slot HMM custom head with random material families", MeshFace, CustomRandomisation);
        foreach (var route in QaHumanMaterialCoverage.All)
        {
            var face = Archetypes.Single(value => value.Game == route.Game && value.Family == route.Family &&
                value.IsPlayer == route.Player);
            var description = route.Group switch
            {
                "custom-human-materials" => "Every Human Face/Scalp NPC/Player choice, edit/RON/history/PCC",
                "custom-human-hair" => "Explicit hair graph with shared scalp controls, textures, 3 rolls and PCC readback",
                "native-human-hair" => "Native attachment graph, BMO hair controls, history, 4 strength/variation rolls and PCC readback",
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
