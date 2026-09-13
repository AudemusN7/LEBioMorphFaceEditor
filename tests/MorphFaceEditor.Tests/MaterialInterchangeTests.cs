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
        new("C3 MFE material round-trip restores scopes slots None and unresolved paths with one undo", MfeRoundTrip),
        new("C3 TSE material import preserves alien values and export qualifies attachments", TsePolicy),
        new("C3 material import rejects malformed and incompatible files before editing", RejectsInvalidFiles),
        new("C3 texture import resolves exact registry paths without name substitution", ResolvesExactTexturePaths),
        new("C3 material export writes atomically and protects the source mesh", ExportProtectsSource)
    ];

    private static void MfeRoundTrip()
    {
        using var source = new Fixture();
        source.Workspace.Assign(0, source.Human);
        source.Workspace.Assign(2, source.Human);
        source.Workspace.Assign(1, source.Krogan);
        source.Session.SetScalar(Key("human", "Shared"), 0.875f);
        source.Session.SetVector(Key("krogan", "SkinTone"), new(2, 0.15f, 0.25f, 0.7f));
        source.Session.SetTextureReference(Key("human", "HED_Diff"), null);
        source.Session.MergeMaterialData(new(
            [new(Key("krogan", "UnrecognisedScalar"), 4.25f)], [],
            [new(Key("krogan", "HED_Diff"), new("", "BIOG_Missing.Diff.KeepMe", 0, "Texture2D"))]),
            new Dictionary<string, DecodedTextureAsset?>());
        var text = source.Export();
        TestAssert.True(!text.Contains("@human/") && !text.Contains("@krogan/"), "Internal control keys leaked into the file.");
        var file = MaterialRonCodec.ReadMfe(text);
        TestAssert.Equal(3, file.Slots.Count);
        TestAssert.Equal(2, file.Slots.Count(value => value.MaterialId == source.Human.Id));
        TestAssert.Equal<AssetIdentity?>(null, file.Parameters["human"].Textures.Single().TextureReference);
        TestAssert.Equal("BIOG_Missing.Diff.KeepMe", file.Parameters["krogan"].Textures.Single().TextureReference!.InstancedPath);

        using var target = new Fixture();
        var originalGeometry = target.Editor.CreateDraft();
        var prepared = target.Prepare(file);
        TestAssert.True(prepared.Warnings.Count == 2 && prepared.Warnings.Any(value => value.Contains("UnrecognisedScalar")) &&
            prepared.Warnings.Any(value => value.Contains("BIOG_Missing.Diff.KeepMe")),
            "The unsupported parameter and missing texture were not reported.");
        target.Editor.ApplyMeshMaterialImport(prepared);
        TestAssert.Equal(text, target.Export());
        TestAssert.True(target.Editor.IsDirty, "Import was treated as a saved state.");
        var missingControl = target.Editor.Material.Textures.Single(value => value.Name == Key("krogan", "HED_Diff"));
        TestAssert.True(missingControl.SourceName == "BIOG_Missing.Diff.KeepMe" &&
                        missingControl.SelectedTexture?.InstancedPath == "BIOG_Missing.Diff.KeepMe",
            "The unresolved texture was shown as None instead of its retained reference.");
        TestAssert.True(target.Session.CreateOverrides().Textures.Any(value =>
                value.TextureReference?.InstancedPath == "BIOG_Missing.Diff.KeepMe"), "Unresolved authored texture was discarded.");
        TestAssert.Equal(originalGeometry.FinalSkeleton.Count, target.Editor.CreateDraft().FinalSkeleton.Count);
        TestAssert.True(originalGeometry.BakedLods[0].SequenceEqual(target.Editor.CreateDraft().BakedLods[0]), "Import changed geometry.");
        target.Editor.UndoCommand.Execute(null);
        TestAssert.Equal(0, target.Workspace.Assignments.Count);
        TestAssert.Equal(0, target.Session.CreateOverrides().Scalars.Count);
        TestAssert.True(!target.Editor.UndoCommand.CanExecute(null), "Import needed more than one Undo.");
        target.Editor.RedoCommand.Execute(null);
        TestAssert.Equal(text, target.Export());
        // A later assignment rebuild must still retain the unresolved selection.
        target.Workspace.Unassign(2);
        TestAssert.Equal("BIOG_Missing.Diff.KeepMe", target.Editor.Material.Textures
            .Single(value => value.Name == Key("krogan", "HED_Diff")).SelectedTexture!.InstancedPath);
        target.Session.SetTextureReference(Key("krogan", "HED_Diff"), null);
        target.Editor.UndoCommand.Execute(null);
        TestAssert.Equal("BIOG_Missing.Diff.KeepMe", target.Session.GetTextureReference(Key("krogan", "HED_Diff"))!.InstancedPath);
    }

    private static void TsePolicy()
    {
        using var fixture = new Fixture();
        fixture.Workspace.Assign(0, fixture.Human);
        fixture.Workspace.Assign(1, fixture.Krogan);
        fixture.Session.SetVector(Key("krogan", "SkinTone"), new Vector4(0.65f));
        var alienBefore = MeshMaterialInterchange.Split(fixture.Session.CaptureInterchangeData())["krogan"];
        TestAssert.True(!fixture.Editor.CanExportTseMaterials && fixture.Editor.CanImportTseMaterials, "Mixed TSE capabilities were wrong.");
        Reject(() => MeshMaterialFileService.ExportTse(fixture.Workspace, fixture.Session.CaptureInterchangeData(), null, []));
        const string tseText = "(hair_mesh: \"BIOG_HMF_HIR_PRO.Hair.Test\", accessory_mesh: [\"BIOG_ACC.Accessory.Test\"], " +
                               "scalar_parameters: {\"Shared\": 0.125}, lod0_vertices: [(x: 9.0, y: 2.0, z: 1.0)], offset_bones: {})";
        var document = MaterialRonCodec.ReadTseDocument(tseText);
        TestAssert.Equal("BIOG_HMF_HIR_PRO.Hair.Test", document.HairMesh);
        TestAssert.Equal("BIOG_ACC.Accessory.Test", document.AccessoryMeshes!.Single());
        // Import Materials auto-detects TSE when no MFE format marker is present.
        var autoDetected = new MeshMaterialFileService(fixture.References).PrepareAsync(
            "(scalar_parameters: {\"Shared\": 0.125})", false, fixture.Workspace,
            fixture.Options, MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(autoDetected.IsTse && autoDetected.Assignments is null,
            "Import Materials did not auto-detect a TSE RON.");
        fixture.Editor.ApplyMeshMaterialImport(autoDetected);
        TestAssert.Near(0.125f, fixture.Session.GetScalar(Key("human", "Shared")), 0);
        var alienAfter = MeshMaterialInterchange.Split(fixture.Session.CaptureInterchangeData())["krogan"];
        TestAssert.True(alienBefore.Scalars.SequenceEqual(alienAfter.Scalars) &&
                        alienBefore.Vectors.SequenceEqual(alienAfter.Vectors) &&
                        alienBefore.Textures.SequenceEqual(alienAfter.Textures), "TSE import changed alien state.");
        fixture.Editor.UndoCommand.Execute(null);
        TestAssert.Near(0.5f, fixture.Session.GetScalar(Key("human", "Shared")), 0);

        var hair = new AssetIdentity("hair.pcc", "BIOG_HMF_HIR_PRO.Hair.Test", 10, "SkeletalMesh");
        var accessory = new AssetIdentity("accessory.pcc", "BIOG_ACC.Accessory.Test", 20, "SkeletalMesh");
        fixture.Editor.ApplyMeshMaterialImport(new(null, new([], [], []),
            new Dictionary<string, DecodedTextureAsset?>(), [],
            new TsePreviewAttachmentImport(true, hair, true, accessory), true));
        TestAssert.Equal(hair, fixture.Editor.HairMesh.Value);
        TestAssert.Equal(accessory, fixture.Editor.OtherMeshes.Single().Value);
        TestAssert.True(fixture.Editor.HairMesh.Options.Any(value => value.Identity == hair) &&
                        fixture.Editor.OtherMeshes.Single().Options.Any(value => value.Identity == accessory),
            "Imported preview attachments were not added to their selectors.");
        fixture.Editor.UndoCommand.Execute(null);
        TestAssert.Equal<AssetIdentity?>(null, fixture.Editor.HairMesh.Value);
        TestAssert.Equal<AssetIdentity?>(null, fixture.Editor.OtherMeshes.Single().Value);

        fixture.Workspace.Unassign(1);
        TestAssert.True(fixture.Editor.CanExportTseMaterials, "Removing the alien assignment did not enable TSE export.");
        var text = MeshMaterialFileService.ExportTse(fixture.Workspace, fixture.Session.CaptureInterchangeData(),
            new("BIOG_HMF_HIR_PRO.pcc", "Hair_PROShepard.HMF_HIR_PROShepard_MDL", 1, "SkeletalMesh"), []);
        TestAssert.True(text.Contains("BIOG_HMF_HIR_PRO.Hair_PROShepard.HMF_HIR_PROShepard_MDL") &&
                        text.Contains("lod0_vertices: []") && text.Contains("offset_bones: {}") && !text.Contains("@human"),
            "The TSE material export included geometry or a relative attachment/control path.");
        TestAssert.Equal(1, MaterialRonCodec.ReadTse(text).Scalars.Count);
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
        Reject(() => MeshMaterialFileService.ResolveAssignments(file with { Slots = file.Slots.Take(1).ToArray() },
            fixture.Workspace, fixture.Options));
        Reject(() => MeshMaterialFileService.ResolveAssignments(file with
        {
            Slots = file.Slots.Select(slot => slot.MaterialId.Length == 0 ? slot : slot with { MaterialId = "missing" }).ToArray()
        }, fixture.Workspace, fixture.Options));
        Reject(() => new MeshMaterialFileService(fixture.References).PrepareAsync(
            MaterialRonCodec.WriteMfe(file with { Game = "LE2" }), false, fixture.Workspace,
            fixture.Options, MorphFaceGame.LE1).GetAwaiter().GetResult());
        Reject(() => fixture.Editor.ApplyMeshMaterialImport(new(
            new Dictionary<int, CustomMaterialAssignmentOption> { [0] = fixture.Krogan },
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
        var seekFreeCandidate = new TextureCatalogCandidate(TextureCatalogGame.LE1, "Diff.Test",
            seekFreeOccurrence, [seekFreeOccurrence]);
        var characterCreatorCandidate = new TextureCatalogCandidate(TextureCatalogGame.LE1, path,
            characterCreatorOccurrence, [characterCreatorOccurrence]);
        var loader = new Loader();
        PreparedMeshMaterialImport Prepare(string texturePath, IReadOnlyList<TextureCatalogCandidate>? sources = null) =>
            MeshMaterialFileService.PrepareTexturesAsync(null,
            new([], [], [new(Key("human", "HED_Diff"), new("", texturePath, 0, "Texture2D"))]),
            fixture.Workspace, sources ?? [characterCreatorCandidate, seekFreeCandidate], loader).GetAwaiter().GetResult();
        var prepared = Prepare(path);
        TestAssert.Equal(1, prepared.Textures.Count);
        TestAssert.Equal("Diff.Test", loader.LoadedPath);
        TestAssert.Equal("BIOG_External.pcc", loader.LoadedPackage);
        TestAssert.Equal(0, prepared.Warnings.Count);
        fixture.Editor.ApplyMeshMaterialImport(prepared);
        TestAssert.Equal("Diff.Test", fixture.Session.GetPreviewTexture(Key("human", "HED_Diff"))!.Source.InstancedPath);
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
            Editor = new FaceEditorViewModel(loaded.Preview.EditingSession, loaded.Preview.Profile.UiProfile, Session,
                new Colours(), References,
                source.SourcePath, [], [], null, [], error => throw new Exception(error),
                customMaterialWorkspace: Workspace, customMaterialOptions: Options, previewOnlyAttachments: true);
        }
        public string Export() => MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(
            Workspace, MorphFaceGame.LE1, "MyMesh", Session.CaptureInterchangeData()));
        public PreparedMeshMaterialImport Prepare(MeshMaterialDocument document)
        {
            var assignments = MeshMaterialFileService.ResolveAssignments(document, Workspace, Options);
            var target = new CustomMaterialWorkspace(Workspace.Source);
            target.ReplaceAssignments(assignments);
            return MeshMaterialFileService.PrepareTexturesAsync(assignments, MeshMaterialInterchange.Flatten(document.Parameters),
                target, [], new Loader()).GetAwaiter().GetResult();
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
