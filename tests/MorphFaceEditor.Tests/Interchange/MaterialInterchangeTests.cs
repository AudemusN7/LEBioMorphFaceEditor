using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

/// <summary>Exercises material files against real detached editing/history composition, without game scanning.</summary>
public static class MaterialInterchangeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MFE material files preserve eight sparse slots and reject a ninth", EightSlotMaterialFile),
        new("C3 material import rejects malformed and incompatible files before editing", RejectsInvalidFiles),
        new("C3 texture import resolves exact registry paths without name substitution", ResolvesExactTexturePaths),
        new("C3 material export writes atomically and protects the source mesh", ExportProtectsSource),
        new("D3 face material import separates tagged heads from untagged TSE", FaceMaterialImport)
    ];

    private static void EightSlotMaterialFile()
    {
        var slots = Enumerable.Range(0, 8).Select(index => new MeshMaterialSlotData(index * 2,
            $"Slot {index}", "skin", "Skin", "human", HeadMaterialFamily.Skin)).ToArray();
        var document = new MeshMaterialDocument("LE3", "eight-slots", slots,
            new Dictionary<string, MorphFaceMaterialData> { ["human"] = new([new("Shared", 0.25f)], [], []) });
        var loaded = MaterialRonCodec.ReadMfe(MaterialRonCodec.WriteMfe(document));
        TestAssert.True(loaded.Slots.SequenceEqual(slots), "Eight-slot RON changed slot indices or assignments.");
        TestAssert.Equal(0.25f, loaded.Parameters["human"].Scalars.Single().Value);
        Reject(() => MaterialRonCodec.ReadMfe(MaterialRonCodec.WriteMfe(document with
            { Slots = [.. slots, slots[0] with { Index = 16 }] })));
        Reject(() => MaterialRonCodec.ReadMfe(MaterialRonCodec.WriteMfe(document with
            { Slots = [.. slots.Take(7), slots[0]] })));
    }

    private static void RejectsInvalidFiles()
    {
        using var fixture = new Fixture();
        fixture.Workspace.Assign(0, fixture.Human);
        var before = fixture.Export();
        Reject(() => MaterialRonCodec.ReadMfe(before.Replace("version: 1", "version: 50")));
        Reject(() => MaterialRonCodec.ReadTse("(scalar_parameters: {\"Shared\": 1, \"Shared\": 2})"));
        Reject(() => MaterialRonCodec.ReadTse("(vector_parameters: {\"SkinTone\": (1, 2, 3)})"));
        Reject(() => MaterialRonCodec.ReadTse("(scalar_parameters: {\"Shared\": 1e99})"));
        Reject(() => MaterialRonCodec.ReadTse("(scalar_parameters: {\"@human/Shared\": 1})"));
        Reject(() => MaterialRonCodec.ReadTse("(texture_parameters: {\"\": \"None\"})"));
        var file = MaterialRonCodec.ReadMfe(before);
        Reject(() => MaterialRonCodec.ReadMfe(MaterialRonCodec.WriteMfe(file with { Slots = [file.Slots[0], file.Slots[0]] })));
        Reject(() => new MeshMaterialFileService(fixture.References).PrepareAsync(
            MaterialRonCodec.WriteMfe(file with { Game = "LE2" }), false, fixture.Workspace,
            MorphFaceGame.LE1).GetAwaiter().GetResult());
        Reject(() => fixture.Editor.ApplyMaterialImport(new(
            new([new(Key("krogan", "Shared"), float.NaN)], [], []),
            new Dictionary<string, DecodedTextureAsset?>(), [])));
        TestAssert.Equal(before, fixture.Export());
    }

    private static void ResolvesExactTexturePaths()
    {
        using var fixture = new Fixture();
        fixture.Workspace.Assign(0, fixture.Human);
        const string path = "BIOG_External.Diff.Test";
        var seekFreeOccurrence = new TextureCatalogOccurrence("BIOG_External.pcc", 17, 0, TextureCatalogOrigin.BaseGame,
            1, 1, "PF_B8G8R8A8", "TEXTUREGROUP_Character", false, null);
        var characterCreatorOccurrence = new TextureCatalogOccurrence("EntryMenu.pcc", 18, 0, TextureCatalogOrigin.BaseGame,
            1, 1, "PF_B8G8R8A8", "TEXTUREGROUP_Character", false, null);
        var seekFreeCandidate = new TextureCatalogCandidate(MorphFaceGame.LE1, "Diff.Test",
            seekFreeOccurrence, [seekFreeOccurrence]) { IsEligibleOfficialDefault = true };
        var characterCreatorCandidate = new TextureCatalogCandidate(MorphFaceGame.LE1, path,
            characterCreatorOccurrence, [characterCreatorOccurrence]) { IsEligibleOfficialDefault = true };
        var loader = new Loader();
        PreparedMaterialImport Prepare(string texturePath, IReadOnlyList<TextureCatalogCandidate>? sources = null) =>
            MeshMaterialFileService.PrepareTexturesAsync(
            new([], [], [new(Key("human", "HED_Diff"), new("", texturePath, 0, "Texture2D"))]),
            fixture.Workspace, sources ?? [characterCreatorCandidate, seekFreeCandidate], loader).GetAwaiter().GetResult();
        var prepared = Prepare(path);
        TestAssert.Equal(1, prepared.Textures.Count);
        TestAssert.Equal(path, loader.LoadedPath);
        TestAssert.Equal("BIOG_External.pcc", loader.LoadedPackage);
        TestAssert.Equal(0, prepared.Warnings.Count);
        fixture.Editor.ApplyMaterialImport(prepared);
        TestAssert.Equal(path, fixture.Session.GetPreviewTexture(Key("human", "HED_Diff"))!.Source.InstancedPath);
        TestAssert.True(fixture.Export().Contains(path, StringComparison.Ordinal),
            "The seek-free package-local texture identity was not requalified on export.");
        var fallback = Prepare(path, [characterCreatorCandidate]);
        TestAssert.Equal(path, loader.LoadedPath);
        TestAssert.Equal("EntryMenu.pcc", loader.LoadedPackage);
        TestAssert.Equal(0, fallback.Warnings.Count);
        var unmatched = Prepare("BIOG_Other.Diff.Test");
        TestAssert.Equal(0, unmatched.Textures.Count);
        TestAssert.Equal(1, unmatched.Warnings.Count);
        TestAssert.Equal(path, loader.LoadedPath);
    }

    private static void ExportProtectsSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFE-C3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "mesh.gltf");
        var output = Path.Combine(directory, "mesh.materials.ron");
        try
        {
            File.WriteAllText(source, "source-mesh");
            MeshMaterialFileService.WriteAsync(output, "first", source).GetAwaiter().GetResult();
            MeshMaterialFileService.WriteAsync(output, "second", source).GetAwaiter().GetResult();
            TestAssert.Equal("second", File.ReadAllText(output));
            Reject(() => MeshMaterialFileService.WriteAsync(source, "bad", source).GetAwaiter().GetResult());
            TestAssert.Equal("source-mesh", File.ReadAllText(source));
            TestAssert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void FaceMaterialImport()
    {
        var targeted = MaterialRonCodec.WriteMfe(FaceMaterialFileService.Capture(
            MorphFaceGame.LE3, "NPC", new([], [], [])) with { DefaultFace = new("le3-salarian", false) });
        TestAssert.Equal(new MaterialFaceTarget("le3-salarian", false), MaterialRonCodec.ReadMfe(targeted).DefaultFace);
        TestAssert.Equal("LE3", MaterialRonCodec.ReadDefaultFace(targeted)!.Value.Game);
        var targetedTse = MaterialRonCodec.WriteTse(new([], [], []), "None", [], "LE2", new("le2-human-female", true));
        TestAssert.Equal(new MaterialFaceTarget("le2-human-female", true), MaterialRonCodec.ReadDefaultFace(targetedTse)!.Value.Target);
        TestAssert.Equal("LE2", MaterialRonCodec.ReadDefaultFace(targetedTse)!.Value.Game);
        TestAssert.Equal(0, MaterialRonCodec.ReadTseDocument(targetedTse).Parameters.Scalars.Count);
        const string untagged = "(morph_features: {}, offset_bones: {}, " +
            "lod0_vertices: [(x: 0, y: 0, z: 0)], scalar_parameters: {\"Shared\": 0.25})";
        TestAssert.Equal<MaterialRonCodec.MaterialFileKind?>(null, MaterialRonCodec.IdentifyMaterialFile(untagged));
        var ownTse = MaterialRonCodec.WriteTse(new([new("Shared", 0.25f)], [], []), "None", []);
        TestAssert.Equal(MaterialRonCodec.MaterialFileKind.Tse, MaterialRonCodec.IdentifyMaterialFile(ownTse)!.Value);
        TestAssert.Equal(MaterialRonCodec.MaterialFileKind.Tse,
            MaterialRonCodec.IdentifyMaterialFile(ownTse.Replace("// MFE RON Type: TSE Materials\n", ""))!.Value);
        TestAssert.Equal(0.25f, FaceMaterialFileService.Read(ownTse, allowTse: false).Parameters.Scalars.Single().Value);
        Reject(() => FaceMaterialFileService.Read(untagged, allowTse: false));
        var tse = FaceMaterialFileService.Read(untagged, allowTse: true);
        TestAssert.True(tse.IsTse && tse.Parameters.Scalars.Single().Value == 0.25f,
            "Player TSE material import was not accepted.");
        Reject(() => FaceMaterialFileService.Read(
            "(hair_mesh: 42, accessory_mesh: [7], scalar_parameters: {\"Shared\": 0.25})", allowTse: true));
        var attachments = FaceMaterialFileService.Read(MaterialRonCodec.WriteMfe(
            FaceMaterialFileService.Capture(MorphFaceGame.LE2, "face", new([], [], [])) with
            { HairMesh = "BIOG_Hair.Hair", AccessoryMeshes = ["BIOG_Gear.Gear"] }), allowTse: true);
        TestAssert.Equal("BIOG_Hair.Hair", attachments.HairMesh);
        TestAssert.Equal("BIOG_Gear.Gear", attachments.AccessoryMeshes!.Single());
        TestAssert.True(FaceMaterialFileService.Read(targeted, false).HairMesh is null,
            "An older MFE file invented an attachment clear.");

        var tagged = "(format: \"MorphFaceEditor.Head\", version: 1, game: \"LE2\", " +
            "target_profile: \"le2-human-female\", target_kind: \"NPC\", " + untagged[1..];
        var head = FaceMaterialFileService.Read(tagged, allowTse: false);
        TestAssert.True(!head.IsTse && head.Parameters.Scalars.Single().Value == 0.25f,
            "A tagged MFE head was not accepted as material-only input.");
        Reject(() => FaceMaterialFileService.Read(tagged.Replace("game: \"LE2\"", "game: \"unknown\""), allowTse: true));
        Reject(() => FaceMaterialFileService.Read(tagged.Replace("target_kind: \"NPC\", ", ""), allowTse: true));

        const string lexTagged = "// Exported from: LEX 1\n// Game: LE2\n// Archetype: HMF\n// Player Morph: NO\n" + untagged;
        Reject(() => FaceMaterialFileService.Read(lexTagged, allowTse: false));
        var ignoredMetadata = FaceMaterialFileService.Read(
            "(format: \"MorphFaceEditor.Materials\", version: 1, game: \"LE2\", " +
            "mesh_name: \"face\", slots: [\"irrelevant\"], " +
            "parameters: {\"face\": (scalar_parameters: {\"Shared\": 0.5})})", allowTse: false);
        TestAssert.Equal(0.5f, ignoredMetadata.Parameters.Scalars.Single().Value);

        var mixed = FaceMaterialFileService.Read(MaterialRonCodec.WriteMfe(new MeshMaterialDocument(
            "LE3", "mixed", [], new Dictionary<string, MorphFaceMaterialData>
            {
                ["human"] = new([new("Shared", 0.1f), new("HumanOnly", 0.2f)], [], []),
                ["krogan"] = new([new("Shared", 0.9f), new("KroganOnly", 0.8f)], [], [])
            })), allowTse: false);
        TestAssert.True(mixed.Parameters.Scalars.Select(value => value.Name).Order().SequenceEqual(
                new[] { "HumanOnly", "KroganOnly" }) && mixed.Warnings.Count == 1,
            "Mixed MESH scopes blocked unique material values or guessed a conflicting value.");
    }

    private static string Key(string scope, string name) => MaterialParameterControlKey.Create(scope, name);
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException) { return; }
        throw new Exception("An invalid operation was accepted.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MorphFacePackageReader _reader = new();
        public CustomMaterialAssignmentOption Human { get; } = Option("human", HeadMaterialFamily.Skin);
        public CustomMaterialAssignmentOption Krogan { get; } = Option("krogan", HeadMaterialFamily.KroganSkin);
        public CustomMaterialAssignmentOption[] Options => [Human, Krogan];
        public CustomMaterialWorkspace Workspace { get; }
        public MaterialEditingSession Session { get; }
        public FaceEditorViewModel Editor { get; }
        public PackageReferenceService References { get; }
        public Fixture()
        {
            var source = new ImportedMeshAsset("fixture.psk", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], null, null,
                [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2],
                [new(0, "Face \"quoted\"", 0, 3), new(1, "Eye", 0, 3), new(2, "Face B", 0, 3)], [], null, null, [0, 1, 2]);
            var loaded = new DetachedMeshPreviewLoadService(new HeadPreviewSceneFactory(), materialCatalog: new Catalogue(Options))
                .Load(MorphFaceGame.LE1, source);
            Workspace = loaded.CustomMaterials!;
            Session = loaded.Preview.MaterialEditingSession;
            References = new PackageReferenceService(_reader, TestFixtures.CreateMissingTextureCatalogService());
            Editor = new FaceEditorViewModel(loaded.Preview.EditingSession, loaded.Preview.Profile.GetUiProfile(Workspace), Session,
                new Colours(), References,
                source.SourcePath, [], [], null, [], error => throw new Exception(error),
                customMaterialWorkspace: Workspace, customMaterialOptions: Options, previewOnlyAttachments: true);
        }
        public string Export() => MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(
            Workspace, MorphFaceGame.LE1, "MyMesh", Session.CaptureInterchangeData()));
        public PreparedMaterialImport Prepare(MeshMaterialDocument document)
        {
            return new MeshMaterialFileService(References).PrepareAsync(MaterialRonCodec.WriteMfe(document), false,
                Workspace, MorphFaceGame.LE1).GetAwaiter().GetResult();
        }
        public void Dispose() { Editor.Dispose(); _reader.Dispose(); }
    }

    private static CustomMaterialAssignmentOption Option(string scope, HeadMaterialFamily family)
    {
        var identity = new AssetIdentity("fixture.pcc", scope + ".Material", 1, "MaterialInstanceConstant");
        var texture = Texture("BIOG_Fixture.Diff." + scope);
        var template = new ResolvedHeadMaterial(scope, identity, scope + "Master", family, HeadMaterialBlendMode.Opaque,
            false, new Dictionary<string, float> { ["Shared"] = 0.5f },
            new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One },
            new Dictionary<string, MaterialTextureBinding> { ["HED_Diff"] = new("HED_Diff", texture) });
        return new(scope, scope, scope, "head", family, template) { ParameterScopeKey = scope, ParameterScopeLabel = scope };
    }
    private static DecodedTextureAsset Texture(string path) => new(new("fixture.pcc", path, 1, "Texture2D"),
        1, 1, [255, 255, 255, 255], "PF_B8G8R8A8", TextureRole.Diffuse, TextureColorSpace.Srgb,
        TextureAlphaPolicy.Ignore, false, path);
    private sealed class Loader : ITextureReferenceLoader
    {
        public string? LoadedPath { get; private set; }
        public string? LoadedPackage { get; private set; }
        public Task<DecodedTextureAsset> LoadTextureAsync(string packagePath, string texturePath,
            MaterialParameterDefinition definition, CancellationToken cancellationToken = default)
        {
            LoadedPackage = packagePath;
            LoadedPath = texturePath;
            var texture = Texture(texturePath) with
            {
                Source = new AssetIdentity(packagePath, texturePath, 1, "Texture2D")
            };
            return Task.FromResult(texture);
        }
    }
    private sealed class Catalogue(IReadOnlyList<CustomMaterialAssignmentOption> options) : ICustomMaterialTemplateCatalog
    {
        public CustomMaterialTemplateCatalogResult Load(MorphFaceGame game) => new(options, [], []);
    }
    private sealed class Colours : IHdrColorDialogService
    {
        public bool ExtendedSliders { get; set; }
        public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview) => null;
        public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview) => null;
    }
}
