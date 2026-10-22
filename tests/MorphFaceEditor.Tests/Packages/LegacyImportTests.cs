using MorphFaceEditor.Core.Services;
using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

using static MorphFaceEditor.Tests.PackageTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class LegacyImportTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("legacy Gibbed ME2 and ME3 head morphs import through the context pipeline", GibbedHeadMorphsImport),
        new("standalone legacy imports reject a destination-game mismatch", StandaloneLegacyImportRejectsMismatch),
    ];

    private static void GibbedHeadMorphsImport()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var source = ReadFaces(path).First(face =>
                face.ProfileKey.Equals("le2-human-female", StringComparison.OrdinalIgnoreCase));
            var expectedMorph = service.CaptureMorphData(path, source.InstancedPath);
            var expectedMaterial = service.CaptureMaterialData(path, source.InstancedPath);
            var legacyMorph = expectedMorph with { BakedLods = [expectedMorph.BakedLods[0]] };

            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(path, source.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var profile = MorphFaceProfileRegistry.CreateDefault().Require(
                loaded.Game,
                loaded.Document.Source.InstancedPath,
                loaded.Document.BaseHeadReference?.InstancedPath);

            foreach (var game in new[] { 2, 3 })
            {
                var extension = $".me{game}headmorph";
                var headMorphPath = Path.Combine(Path.GetTempPath(), $"MFE-{Guid.NewGuid():N}{extension}");
                try
                {
                    WriteGibbedHeadMorph(
                        headMorphPath,
                        game,
                        legacyMorph,
                        expectedMaterial with
                        {
                            Textures = expectedMaterial.Textures.Count == 0
                                ? expectedMaterial.Textures
                                : expectedMaterial.Textures
                                    .Select((value, index) => index == 0
                                        ? value with
                                        {
                                            TextureReference = new AssetIdentity(
                                                string.Empty,
                                                "BIOG_MFE_Missing_Donor.MissingTexture",
                                                0,
                                                "Texture2D")
                                        }
                                        : value)
                                    .ToArray()
                        },
                        formatVersion: game == 2 ? 1 : 0);
                    var legacy = service.ReadLegacyHeadMorph(headMorphPath);
                    TestAssert.Equal(1, legacy.MorphData.BakedLods.Count);

                    var session = new MorphFaceEditingSession(
                        loaded.Document,
                        loaded.BaseHead,
                        new MorphTargetCatalog().Load(profile),
                        profile.MetadataOnlyFeatures,
                        profile.GetDisplayName(),
                        profile.FeatureAliases,
                        profile.RecognizesBaseVariant,
                        profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));
                    session.ApplyMorphData(legacy.MorphData);
                    var draft = session.CreateDraft(
                        loaded.Document.HairMeshReference,
                        loaded.Document.OtherMeshReferences,
                        loaded.Document.MaterialOverrides);
                    var converted = new MorphFaceMorphData(
                        draft.MorphFeatures,
                        draft.FinalSkeleton,
                        draft.BakedLods);
                    TestAssert.Equal(expectedMorph.BakedLods.Count, converted.BakedLods.Count);
                    TestAssert.True(converted.BakedLods.Select(value => value.Length)
                            .SequenceEqual(expectedMorph.BakedLods.Select(value => value.Length)),
                        "Legacy conversion did not rebuild the selected LE profile's LOD topology.");

                    // Conversion and material/attachment import use the same captured head.
                    File.Delete(headMorphPath);
                    var result = service.ImportConvertedLegacyHeadMorph(
                        path,
                        source.InstancedPath,
                        $"MFE_GibbedME{game}_Test",
                        legacy,
                        converted);
                    TestAssert.True(result.Warnings.Any(value =>
                            value.Contains("MissingTexture", StringComparison.OrdinalIgnoreCase) &&
                            value.Contains("template", StringComparison.OrdinalIgnoreCase)),
                        "Legacy texture substitution did not produce an explicit warning.");
                    AssertMorphEqual(converted, service.CaptureMorphData(path, result.FaceInstancedPath));
                    AssertMaterialEqual(expectedMaterial, service.CaptureMaterialData(path, result.FaceInstancedPath));
                }
                finally
                {
                    if (File.Exists(headMorphPath))
                    {
                        File.Delete(headMorphPath);
                    }
                }
            }
        });
    }

    private static void StandaloneLegacyImportRejectsMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-Mismatch-{Guid.NewGuid():N}.me2headmorph");
        try
        {
            File.WriteAllBytes(path, [0]);
            var threw = false;
            try
            {
                _ = new StandaloneLegacyHeadMorphImportService().Import(
                    MorphFaceGame.LE3,
                    path,
                    "Mismatch");
            }
            catch (InvalidDataException exception)
            {
                threw = exception.Message.Contains("LE2", StringComparison.OrdinalIgnoreCase) &&
                        exception.Message.Contains("LE3", StringComparison.OrdinalIgnoreCase);
            }

            TestAssert.True(threw,
                "A .me2headmorph imported as LE3 without a clear destination-game mismatch error.");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteGibbedHeadMorph(
        string path,
        int game,
        MorphFaceMorphData morph,
        MorphFaceMaterialData material,
        int formatVersion = 0)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.Latin1);
        writer.Write(System.Text.Encoding.ASCII.GetBytes($"GIBBEDMASSEFFECT{game}HEADMORPH"));
        writer.Write((byte)formatVersion);
        if (formatVersion == 1)
        {
            writer.Write((byte)0); // little-endian marker used by the modified ME2 editor
        }
        writer.Write(0u);
        if (formatVersion == 1)
        {
            WriteUnrealString(writer, "743.EJM.A11.H8C.GGG.91W.A4P.786.NJ6.E92.9G6.576");
        }
        WriteUnrealString(writer, "None");
        writer.Write(0); // accessory meshes
        WriteMap(writer, morph.MorphFeatures, value => value.Name, value => writer.Write(value.Offset));
        WriteMap(writer, morph.FinalSkeleton, value => value.BoneName, value => WriteVector3(writer, value.Translation));
        for (var lod = 0; lod < 4; lod++)
        {
            var vertices = morph.BakedLods.ElementAtOrDefault(lod) ?? [];
            writer.Write(vertices.Count());
            foreach (var vertex in vertices)
            {
                WriteVector3(writer, vertex);
            }
        }
        WriteMap(writer, material.Scalars, value => value.Name, value => writer.Write(value.Value));
        WriteMap(writer, material.Vectors, value => value.Name, value =>
        {
            writer.Write(value.Value.X);
            writer.Write(value.Value.Y);
            writer.Write(value.Value.Z);
            writer.Write(value.Value.W);
        });
        WriteMap(writer, material.Textures, value => value.Name, value =>
            WriteUnrealString(writer, value.TextureReference?.InstancedPath ?? "None"));
    }

    private static void WriteMap<T>(
        BinaryWriter writer,
        IReadOnlyList<T> values,
        Func<T, string> key,
        Action<T> writeValue)
    {
        writer.Write(values.Count);
        foreach (var value in values)
        {
            WriteUnrealString(writer, key(value));
            writeValue(value);
        }
    }

    private static void WriteUnrealString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(value);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }
}
