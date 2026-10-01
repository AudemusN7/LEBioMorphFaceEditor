using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

/// <summary>Protects package-qualified LE2 Player alignment texture RON round-trips.</summary>
public static class Le2PlayerMaterialRonIdentityTests
{
    private const string EmissivePath = "BIOG_HMM_HED_Alignment.Emissive.Face_EmisScars_03";
    private const string NormalPath = "BIOG_HMM_HED_Alignment.Scar.Face_NormScars_03";
    private static readonly string DonorPackage = Path.Combine(Path.GetTempPath(), "BIOG_HMM_HED_Alignment.pcc");

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE2 Player material RON retains canonical alignment texture paths and donor identities", RoundTripsAlignmentTextureDonors),
        new("hidden qualified texture from SFXGame keeps its full donor path", HiddenSfxGameDonorKeepsFullPath),
        new("hidden located texture reference retains its exact locator", HiddenLocatedDonorKeepsLocator),
        new("MFE RON preserves already-qualified texture paths without prefix duplication", MfeSerializerDoesNotDoubleQualify)
    ];

    private static void RoundTripsAlignmentTextureDonors()
    {
        var source = new MorphFaceMaterialData([], [],
        [
            new("HED_Face_Alignment_Emis", new(DonorPackage, "Emissive.Face_EmisScars_03", 8, "Texture2D")),
            new("HED_Face_Alignment_Norm", new(DonorPackage, "Scar.Face_NormScars_03", 18, "Texture2D"))
        ]);
        var ron = MaterialRonCodec.WriteTse(source, "None", []);
        TestAssert.True(ron.Contains(EmissivePath, StringComparison.Ordinal) &&
                        ron.Contains(NormalPath, StringComparison.Ordinal),
            "The material RON omitted the package qualification from an alignment texture.");

        var imported = MaterialRonCodec.ReadTse(ron);
        TestAssert.Equal(EmissivePath, imported.Textures.Single(value =>
            value.Name == "HED_Face_Alignment_Emis").TextureReference!.InstancedPath);
        TestAssert.Equal(NormalPath, imported.Textures.Single(value =>
            value.Name == "HED_Face_Alignment_Norm").TextureReference!.InstancedPath);

        var localPath = Path.Combine(Path.GetTempPath(), "LE2-SFXGame.pcc");
        var occurrence = new TextureCatalogOccurrence(DonorPackage, 8, 100, TextureCatalogOrigin.BaseGame,
            1024, 1024, "PF_DXT1", "TEXTUREGROUP_Character", false, null);
        var emissive = new TextureCatalogCandidate(TextureCatalogGame.LE2,
            "Emissive.Face_EmisScars_03", occurrence, [occurrence]) { IsEligibleOfficialDefault = true };
        var normalOccurrence = occurrence with { ExportUIndex = 18 };
        var normal = new TextureCatalogCandidate(TextureCatalogGame.LE2,
            "Scar.Face_NormScars_03", normalOccurrence, [normalOccurrence]) { IsEligibleOfficialDefault = true };
        var loader = new DonorLoader();
        using var editor = CreateEditor(loader, [], [emissive, normal]);

        TestAssert.True(!editor.Textures.Any(value => editor.SourceParameterName(value.Name).StartsWith(
                "HED_Face_Alignment_", StringComparison.OrdinalIgnoreCase)),
            "Runtime alignment parameters became visible as editable controls.");
        var warnings = editor.MergeMaterialDataAsync(imported).GetAwaiter().GetResult();
        TestAssert.Equal(0, warnings.Count);
        var captured = editor.CaptureInterchangeData();
        AssertDonor(captured.Textures.Single(value => value.Name == "HED_Face_Alignment_Emis").TextureReference!,
            EmissivePath, 8);
        AssertDonor(captured.Textures.Single(value => value.Name == "HED_Face_Alignment_Norm").TextureReference!,
            NormalPath, 18);
        TestAssert.True(loader.LoadedPaths.Select(value => TextureCatalogPicker.CanonicalPath(
                    value.TexturePath, value.PackagePath)).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(new[] { EmissivePath, NormalPath }.OrderBy(value => value, StringComparer.Ordinal)),
            "The two canonical alignment donors were not both resolved exactly.");
    }

    private static void HiddenSfxGameDonorKeepsFullPath()
    {
        var sfxGame = Path.Combine(Path.GetTempPath(), "SFXGame.pcc");
        var candidates = new[]
        {
            new PackageAssetListItem(new AssetIdentity(sfxGame, EmissivePath, 39366, "Texture2D")),
            new PackageAssetListItem(new AssetIdentity(sfxGame, NormalPath, 39368, "Texture2D"))
        };
        var loader = new DonorLoader();
        using var editor = CreateEditor(loader, candidates, []);
        var warnings = editor.MergeMaterialDataAsync(AlignmentData(null)).GetAwaiter().GetResult();
        TestAssert.Equal(0, warnings.Count);
        AssertDonor(editor.CaptureInterchangeData().Textures.Single(value =>
            value.Name == "HED_Face_Alignment_Emis").TextureReference!, EmissivePath, 39366, sfxGame);
        AssertDonor(editor.CaptureInterchangeData().Textures.Single(value =>
            value.Name == "HED_Face_Alignment_Norm").TextureReference!, NormalPath, 39368, sfxGame);
        TestAssert.True(loader.LoadedPaths.All(value => value.PackagePath == sfxGame &&
                value.TexturePath.StartsWith("BIOG_HMM_HED_Alignment.", StringComparison.Ordinal)),
            "A qualified SFXGame reference was shortened before decoding.");
    }

    private static void HiddenLocatedDonorKeepsLocator()
    {
        var sfxGame = Path.Combine(Path.GetTempPath(), "SFXGame.pcc");
        var data = AlignmentData(sfxGame);
        data = data with
        {
            Textures = data.Textures.Select(value => value with
            {
                TextureReference = value.TextureReference! with { IsExplicitTextureDonor = true }
            }).ToArray()
        };
        var loader = new DonorLoader();
        using var editor = CreateEditor(loader, [], []);
        var warnings = editor.MergeMaterialDataAsync(data).GetAwaiter().GetResult();
        TestAssert.Equal(0, warnings.Count);
        AssertDonor(editor.CaptureInterchangeData().Textures.Single(value =>
            value.Name == "HED_Face_Alignment_Emis").TextureReference!, EmissivePath, 39366, sfxGame);
        TestAssert.True(loader.LoadedPaths.Any(value => value.PackagePath == sfxGame && value.TexturePath == EmissivePath),
            "A located qualified reference did not reach the loader with its original package and full path.");
        TestAssert.True(editor.CaptureInterchangeData().Textures.All(value => value.TextureReference!.IsExplicitTextureDonor),
            "Hidden located references lost their explicitly selected donor provenance.");
    }

    private static void MfeSerializerDoesNotDoubleQualify()
    {
        var data = new MorphFaceMaterialData([], [],
        [
            new("HED_Face_Alignment_Emis", new(DonorPackage, EmissivePath, 8, "Texture2D")),
            new("HED_Face_Alignment_Norm", new(DonorPackage, NormalPath, 18, "Texture2D"))
        ]);
        var document = new MeshMaterialDocument("LE2", "Player", [],
            new Dictionary<string, MorphFaceMaterialData> { ["face"] = data });
        var text = MaterialRonCodec.WriteMfe(document);
        var imported = MaterialRonCodec.ReadMfe(text);
        foreach (var path in new[] { EmissivePath, NormalPath })
        {
            TestAssert.Equal(1, Count(text, path));
            TestAssert.Equal(path, imported.Parameters["face"].Textures
                .Single(value => value.TextureReference?.InstancedPath == path).TextureReference!.InstancedPath);
        }
    }

    private static MorphFaceMaterialData AlignmentData(string? packagePath) => new([], [],
    [
        new("HED_Face_Alignment_Emis", new(packagePath ?? string.Empty, EmissivePath, packagePath is null ? 0 : 8, "Texture2D")),
        new("HED_Face_Alignment_Norm", new(packagePath ?? string.Empty, NormalPath, packagePath is null ? 0 : 18, "Texture2D"))
    ]);

    private static MaterialEditorViewModel CreateEditor(
        DonorLoader loader,
        IReadOnlyList<PackageAssetListItem> localCandidates,
        IReadOnlyList<TextureCatalogCandidate> registryCandidates)
    {
        var localPath = Path.Combine(Path.GetTempPath(), "LE2-SFXGame.pcc");
        var materialIdentity = new AssetIdentity(localPath, "BIOG_HMM_HED_Player.Material", 1, "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity), materialIdentity, "HMM_HED_PROCustom_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(), new Dictionary<string, Vector4>(),
            new Dictionary<string, MaterialTextureBinding>())
        {
            SupportedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HED_Face_Alignment_Emis", "HED_Face_Alignment_Norm"
            }
        };
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
        return new MaterialEditorViewModel(session, new Colors(), loader, localPath, localCandidates, _ => { },
            new HumanMaleFeatureMetadataCatalog(), registryCandidates,
            new TextureCatalogProfile("le2-human-male", ["HMM_HED"], []), registryCandidates.Count > 0);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static void AssertDonor(AssetIdentity identity, string path, int uindex, string? packagePath = null)
    {
        TestAssert.Equal(path, TextureCatalogPicker.CanonicalPath(identity.InstancedPath, identity.PackagePath));
        TestAssert.Equal(Path.GetFullPath(packagePath ?? DonorPackage), Path.GetFullPath(identity.PackagePath));
        TestAssert.Equal(uindex, identity.UIndex);
        TestAssert.Equal("Texture2D", identity.ClassName);
    }

    private sealed class DonorLoader : ITextureReferenceLoader
    {
        public List<(string PackagePath, string TexturePath)> LoadedPaths { get; } = [];

        public Task<DecodedTextureAsset> LoadTextureAsync(string packagePath, string texturePath,
            MaterialParameterDefinition definition, CancellationToken cancellationToken = default)
        {
            LoadedPaths.Add((packagePath, texturePath));
            var sfxGameDonor = Path.GetFileName(packagePath).Equals("SFXGame.pcc", StringComparison.OrdinalIgnoreCase);
            var uindex = texturePath.EndsWith("Face_NormScars_03", StringComparison.Ordinal)
                ? sfxGameDonor ? 39368 : 18
                : sfxGameDonor ? 39366 : 8;
            return Task.FromResult(new DecodedTextureAsset(
                new AssetIdentity(packagePath, texturePath, uindex, "Texture2D"), 1, 1,
                [255, 255, 255, 255], "PF_B8G8R8A8", definition.TextureRole,
                definition.ColorSpace, definition.AlphaPolicy, false, texturePath));
        }
    }

    private sealed class Colors : IHdrColorDialogService
    {
        public bool ExtendedSliders { get; set; }
        public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview) => null;
        public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview) => null;
    }
}
