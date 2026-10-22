using MorphFaceEditor.Core.Services;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;
internal static class RonCodecTests
{

    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("RON provenance metadata round-trips and rejects partial headers", ProvenanceMetadata),
        new("RON shared syntax retains distinct head and material schemas", SharedSyntaxSchemas),
        new("RON export omits attachment-only Diffuseuse", RonOmitsAttachmentTexture),
        new("RON texture catalogue canonicalises occurrence package paths", CanonicalTextureCataloguePaths)
    ];

    private static void SharedSyntaxSchemas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-RonSyntax-{Guid.NewGuid():N}.ron");
        const string head = """
            (
                hair_mesh: "None"
                accessory_mesh: ["Hair.A" "Gear.B"]
                morph_features: {"UnknownZero": 0 "Smile": 2.5e-2}
                offset_bones: {"root": (z: 3 x: 1 y: 2)}
                lod0_vertices: [(1, 2, 3) (x: 4, y: 5, z: 6)]
                scalar_parameters: {"Scalar": 0.5}
                vector_parameters: {"Tint": (1, 2, 3, 4)}
                texture_parameters: {"Texture": "BIOG_Test.Path\\Leaf\"Quoted"}
            )
            """;
        try
        {
            File.WriteAllText(path, head);
            TestAssert.True(MaterialRonCodec.IdentifyMaterialFile(RonInputSnapshot.Parse(head)) is null,
                "Material classification rejected the full-head omitted-comma dialect.");
            var parsed = TseHeadMorphRon.Read(path);
            TestAssert.True(parsed.AccessoryMeshes.SequenceEqual(["Hair.A", "Gear.B"]), "Head arrays lost omitted-comma support or order.");
            TestAssert.True(parsed.MorphData.MorphFeatures.Select(v => v.Name).SequenceEqual(["UnknownZero", "Smile"]), "Head feature order changed.");
            TestAssert.Equal(0f, parsed.MorphData.MorphFeatures[0].Offset);
            TestAssert.Equal(new Vector3(1, 2, 3), parsed.MorphData.FinalSkeleton[0].Translation);
            TestAssert.Equal(new Vector3(4, 5, 6), parsed.MorphData.BakedLods[0][1]);
            TestAssert.Equal("BIOG_Test.Path\\Leaf\"Quoted", parsed.MaterialData.Textures[0].TextureReference!.InstancedPath);
            File.WriteAllText(path, "/* shared RON comment */" + head);
            TestAssert.Equal(2, TseHeadMorphRon.Read(path).MorphData.BakedLods[0].Length);

            // Full-head, named-vector and positional-tuple restrictions remain independent.
            foreach (var invalid in new[]
            {
                head.Replace("(1, 2, 3)", "[1, 2, 3]"),
                head.Replace("(1, 2, 3)", "(1 2 3)"),
                head.Replace("z: 3 x: 1 y: 2", "z: 3 X: 1 y: 2"),
                head.Replace("lod0_vertices", "LOD0_VERTICES"),
                head.Replace("\"Smile\"", "\"unknownzero\""),
                "(lod0_vertices: [],)",
                "(lod0_vertices: [(0,0,0)], lod2_vertices: [(0,0,0)],)",
                "(lod0_vertices: [(0,0,0)], unknown: 1,)",
                "(game: \"LE2\", lod0_vertices: [(0,0,0)],)",
                head + " unexpected",
                head.Replace("0.5", "1e999")
            })
            {
                File.WriteAllText(path, invalid);
                try { TseHeadMorphRon.Read(path); throw new Exception("Invalid head RON was accepted."); }
                catch (InvalidDataException) { }
            }
            try { MaterialRonCodec.ReadTse(head); throw new Exception("Material RON accepted omitted commas."); }
            catch (InvalidDataException) { }
            var materials = MaterialRonCodec.ReadTse("(/* comment */scalar_parameters: {\"Scalar\": 2.5e-2,},)");
            TestAssert.Equal(0.025f, materials.Scalars.Single().Value);
            // Material-only consumption still ignores head geometry and attachments.
            var maps = MaterialRonCodec.ReadTse("{\"scalar_parameters\":{\"Scalar\":1},\"lod0_vertices\":[],\"hair_mesh\":7}");
            TestAssert.Equal(1f, maps.Scalars.Single().Value);
        }
        finally { File.Delete(path); }
    }

    private static void ProvenanceMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-RonMetadata-{Guid.NewGuid():N}.ron");
        var head = new HeadMorphPayload("None", [], new([], [], [[Vector3.Zero]]), new([], [], []));
        var expected = new RonExportProvenance(RonExportProducer.MFE, MorphFaceGame.LE2, "ASA", false);
        try
        {
            TseHeadMorphRon.Write(path, head, expected);
            var text = File.ReadAllText(path);
            TestAssert.Equal(expected, TseHeadMorphRon.ReadProvenance(path));
            var metadata = MfeRonMetadata.TryRead(RonInputSnapshot.Parse(text))!;
            TestAssert.Equal(MfeRonMetadata.Head, metadata.Format);
            TestAssert.Equal(new MaterialFaceTarget("le2-asari", false), metadata.Target);
            TestAssert.Equal(1, File.ReadLines(path).TakeWhile(line => line.StartsWith("//", StringComparison.Ordinal)).Count());
            TestAssert.True(!text.Contains("// Exported from:") && !text.Contains("// Game:"), "MFE still exports comment metadata.");
            foreach (var invalid in new[]
            {
                text.Replace("version: 1", "version: 2"),
                text.Replace("MorphFaceEditor.Head", "Unknown.Head"),
                text.Replace("game: \"LE2\"", "game: \"LE3\""),
                text.Replace("target_kind: \"NPC\"", "target_kind: \"Unknown\"")
            })
            {
                File.WriteAllText(path, invalid);
                try { _ = TseHeadMorphRon.ReadProvenance(path); throw new Exception("Invalid MFE metadata was accepted."); }
                catch (InvalidDataException) { }
            }
            TestFixtures.WriteLexRon(path, head, MorphFaceGame.LE2, "ASA", false);
            TestAssert.Equal(expected with { Producer = RonExportProducer.LEX }, TseHeadMorphRon.ReadProvenance(path));
            TestAssert.Equal(1, TseHeadMorphRon.Read(path).MorphData.BakedLods.Count);
            var lex = File.ReadAllText(path);
            File.WriteAllText(path, lex.Replace("from: LEX 1", "from : LEX\t1").Replace("Game:", "Game :"));
            TestAssert.Equal(expected with { Producer = RonExportProducer.LEX }, TseHeadMorphRon.ReadProvenance(path));
            foreach (var invalid in new[] { lex.Replace("// Archetype: ASA\n", ""), lex.Replace("// Game: LE2", "// Game: LE2\n// Game: LE3") })
            {
                File.WriteAllText(path, invalid);
                try { _ = TseHeadMorphRon.ReadProvenance(path); throw new Exception("Malformed LEX metadata was accepted."); }
                catch (InvalidDataException) { }
            }
            File.WriteAllText(path, lex.Replace("Exported from: LEX", "Exported from: MFE"));
            TestAssert.True(TseHeadMorphRon.ReadProvenance(path) is null, "Old MFE comments still control routing.");
            TseHeadMorphRon.Write(path, head);
            TestAssert.True(TseHeadMorphRon.ReadProvenance(path) is null, "An untagged TSE head reported MFE metadata.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RonOmitsAttachmentTexture()
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"MFE-RonAttachment-{Guid.NewGuid():N}.ron");
        try
        {
            var source = new HeadMorphPayload("None", [], new MorphFaceMorphData([], [], [[Vector3.Zero]]),
                new MorphFaceMaterialData([], [],
                    [new TextureMaterialOverride("Diffuseuse", null),
                     new TextureMaterialOverride("HED_Diff", null)]));
            TseHeadMorphRon.Write(temporaryPath, source);
            var roundTrip = TseHeadMorphRon.Read(temporaryPath);
            TestAssert.True(roundTrip.MaterialData.Textures.All(value =>
                    !value.Name.Equals("Diffuseuse", StringComparison.OrdinalIgnoreCase)),
                "Attachment Diffuseuse leaked into a full head RON export.");
            TestAssert.True(roundTrip.MaterialData.Textures.Any(value => value.Name == "HED_Diff"),
                "The head texture was removed with the attachment texture.");
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void CanonicalTextureCataloguePaths()
    {
        const string internalPath = "PROCustomFade01.HMM_HIR_PROCustomFade01_Mask";
        const string requestedPath = "BIOG_HMM_HIR_PRO_R.PROCustomFade01.HMM_HIR_PROCustomFade01_Mask";
        var occurrence = new TextureCatalogOccurrence(
            Path.Combine("C:\\Games\\LE1", "BIOG_HMM_HIR_PRO_R.pcc"),
            123,
            0,
            TextureCatalogOrigin.BaseGame,
            512,
            512,
            "PF_DXT1",
            "TEXTUREGROUP_Character",
            false,
            null);
        var candidate = new TextureCatalogCandidate(
            MorphFaceGame.LE1,
            internalPath,
            occurrence,
            [occurrence]);

        TestAssert.True(StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE1, requestedPath),
            "The registry's internal export path was not canonicalised through its occurrence package.");
        TestAssert.True(!StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE2, requestedPath),
            "Cross-game registry occurrence was admitted.");
        TestAssert.True(!StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE1,
                "BIOG_OTHER.PROCustomFade02.HMM_HIR_PROCustomFade01_Mask"),
            "An unrelated package root was admitted by object name.");
        TestAssert.True(!StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE1,
                "BIOG_HMM_HIR_PRO_R.PROCustomFade02.HMM_HIR_PROCustomFade01_Mask"),
            "A malformed RON parent path was silently repaired by the exact catalogue lookup.");
    }
}
