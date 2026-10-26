using MorphFaceEditor.Core.Deformation;
using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Core.Randomisation;
using System.Windows.Threading;

namespace MorphFaceEditor.Tests;

internal static class UiTestFixtures
{
    internal static void RunWithDispatcher(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = action();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    internal static MainWindowViewModel CreateMainWindowViewModel(
        MorphFacePackageReader reader,
        StubEditorDialogs? dialogs = null,
        MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureCatalogService? textureCatalog = null,
        MorphFaceProfileRegistry? previewProfiles = null,
        ICustomMaterialTemplateCatalog? customMaterials = null)
    {
        var scenes = new HeadPreviewSceneFactory();
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var targets = new MorphTargetCatalog();
        var context = new MorphFacePackageContextService();
        textureCatalog ??= TestFixtures.CreateMissingTextureCatalogService();
        return new MainWindowViewModel(
            dialogs ?? new StubEditorDialogs(),
            new MorphFaceCatalogService(profiles),
            new MorphFacePreviewLoadService(scenes, targets, previewProfiles ?? profiles, reader),
            scenes, new StubColorDialog(), new PackageReferenceService(reader, textureCatalog),
            new MorphFacePackageWriter(), context,
            new MorphFaceConversionService(profiles, targets, context, textureCatalog),
            new MorphFaceInterchangeService(), new StubClipboard(),
            detachedMeshPreviewLoadService: customMaterials is null ? null :
                new DetachedMeshPreviewLoadService(scenes, materialCatalog: customMaterials));
    }

    internal static FaceEditorViewModel CreateEditor(
        MorphFacePackageReader reader,
        IHeadEditorUiProfile? metadataCatalog = null,
        string profileKey = "le1-human-male",
        bool ignoresAuthoredGeometry = false)
    {
        var mesh = TestFixtures.CreateMesh();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [new MorphFeatureValue("Target", 0)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var morphSession = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(
            document,
            mesh,
            ignoresAuthoredGeometry ? [] : [TestFixtures.CreateTarget()],
            metadataOnlyFeatures: ignoresAuthoredGeometry
                ? MorphFaceProfileFeatures.TurianAllFeatureNames
                : null,
            geometryEditBlockReason: ignoresAuthoredGeometry ? "Material-only test profile." : null,
            ignoreAuthoredGeometry: ignoresAuthoredGeometry);
        // Supply a shared material control even when the profile has a valid rig.
        var materialIdentity = TestFixtures.CreateIdentity("SharedSkinToneMaterial", "MaterialInstanceConstant");
        var resolvedMaterials = new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [MaterialIdentityKey.Create(materialIdentity)] = new ResolvedHeadMaterial(
                    MaterialIdentityKey.Create(materialIdentity),
                    materialIdentity,
                    "HMM_HED_PRONPC_MASTER_FACE_MAT",
                    HeadMaterialFamily.Skin,
                    HeadMaterialBlendMode.Opaque,
                    false,
                    new Dictionary<string, float>(),
                    new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One },
                    new Dictionary<string, MaterialTextureBinding>())
            });
        var materialSession = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            resolvedMaterials);
        return new FaceEditorViewModel(
            morphSession,
            metadataCatalog ?? new HumanMaleFeatureMetadataCatalog(),
            materialSession,
            new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc",
            [],
            [],
            null,
            [],
            _ => { },
            profileKey,
            randomisationCatalog: ignoresAuthoredGeometry ? MorphRandomisationCatalog.LoadEmbedded() : null,
            ignoresAuthoredGeometry: ignoresAuthoredGeometry);
    }

    internal static FaceEditorViewModel CreateRandomisationEditor(
        MorphFacePackageReader reader,
        bool extremeBoneOffset = false,
        Func<int>? randomSeedFactory = null,
        bool includeUnusedMaterialParameters = false)
    {
        const string profileKey = "le1-human-male";
        var source = TestFixtures.CreateMesh();
        var mesh = source with
        {
            Lods = [source.Lods[0] with { Topology = source.Topology with
            {
                ReferenceSkeleton =
                [
                    new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity),
                    new ReferenceBone("nose_tip", 0, new Vector3(2, 4, 6), Quaternion.Identity),
                    new ReferenceBone("eye_new", 0, new Vector3(1, 3, 5), Quaternion.Identity)
                ],
                ActiveBones = [0, 1, 2],
                RequiredBones = [0, 1, 2]
            }}]
        };
        MorphTargetAsset Target(string name) => new(
            TestFixtures.CreateIdentity($"Set.{name}", "MorphTarget"),
            [new MorphTargetLod(0, 3, [])],
            name == "nose_BridgeIn"
                ? [new MorphTargetBoneOffset("nose_tip", new Vector3(0, extremeBoneOffset ? float.MaxValue : 100, 0))]
                : [new MorphTargetBoneOffset("eye_new", new Vector3(0, 100, 0))]);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)), mesh.Source, null,
            [new MorphFeatureValue("nose_BridgeIn", 0), new MorphFeatureValue("eyes_Big", 0)],
            [new BoneTranslation("root", Vector3.Zero), new BoneTranslation("nose_tip", new Vector3(2, 4, 6))],
            MorphFaceMaterialOverrides.Empty, [mesh.Positions.ToArray()], []);
        var session = new MorphFaceEditingSession(document, mesh, [Target("nose_BridgeIn"), Target("eyes_Big")]);
        var materialIdentity = TestFixtures.CreateIdentity("HeadMaterial", "MaterialInstanceConstant");
        var scalars = new Dictionary<string, float> { ["HED_Norm_Blend"] = 2, ["Emis_Scalar"] = 1 };
        var vectors = new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One, ["Emis_Color"] = Vector4.One };
        if (includeUnusedMaterialParameters)
        {
            scalars["HED_Scalp_Mask_OverlayKill_Scalar"] = 1;
            scalars["HED_Scalp_BuzzCut_Alpha_Scalar"] = 0;
            vectors["HED_Scar_Vector"] = new(0.2f, 0.3f, 0.4f, 0.6f);
        }
        var material = new ResolvedHeadMaterial(MaterialIdentityKey.Create(materialIdentity), materialIdentity,
            "BIOG_HMM_HED_PROMorph", HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            scalars, vectors, new Dictionary<string, MaterialTextureBinding>());
        var materials = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
        var donor = new MorphRandomisationDonor("LE1:Seed", profileKey,
            new HashSet<string>(["nose_BridgeIn", "eyes_Big"], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                { ["nose_BridgeIn"] = 0.6f, ["eyes_Big"] = 0.7f })
        {
            MaterialScalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                { ["HED_Norm_Blend"] = 4, ["Emis_Scalar"] = 2 },
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["SkinTone"] = new(0.4f, 0.2f, 0.1f, 1),
                ["Emis_Color"] = new(0.4f, 0.3f, 0.2f, 1)
            }
        };
        var profile = new MaterialRandomisationProfile(profileKey,
            new Dictionary<string, MaterialScalarStatistics>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Norm_Blend"] = new("HED_Norm_Blend", 1, 6, 2, 5),
                ["Emis_Scalar"] = new("Emis_Scalar", 0, 4, 1, 3)
            },
            new Dictionary<string, MaterialVectorStatistics>(StringComparer.OrdinalIgnoreCase)
            {
                ["SkinTone"] = new("SkinTone", MaterialVectorRandomisationKind.PerceptualColour,
                    new Vector4(0.1f, 0.05f, 0.02f, 1), new Vector4(0.8f, 0.6f, 0.4f, 1), []),
                ["Emis_Color"] = new("Emis_Color", MaterialVectorRandomisationKind.PerceptualColour,
                    Vector4.Zero, new Vector4(4), [])
            }, new HashSet<string>());
        var corpus = new MorphRandomisationCorpus(MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
                { [MorphRandomisationPoolKey.HumanMaleLe12] = [donor] })
        {
            MaterialProfiles = new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase)
                { [profileKey] = profile }
        };
        return new FaceEditorViewModel(session, new HumanMaleFeatureMetadataCatalog(), materials,
            new StubColorDialog(), new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc", [], [], null, [], _ => { }, profileKey, new MorphRandomisationCatalog(corpus),
            randomSeedFactory ?? (() => 123));
    }

    internal sealed class StubColorDialog : IHdrColorDialogService
    {
        public bool ExtendedSliders { get; set; }
        public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview) => null;
        public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview) => null;
    }

    internal sealed class StubClipboard : IMorphFaceClipboardService
    {
        public void Set(MorphFaceClipboardPayload payload) => throw new NotSupportedException();
        public MorphFaceClipboardPayload Get(MorphFaceClipboardKind expectedKind) => throw new NotSupportedException();
        public bool Contains(MorphFaceClipboardKind expectedKind) => false;
    }

    internal sealed class StubEditorDialogs : IEditorDialogService
    {
        public int StandaloneGameChoiceCount { get; private set; }
        public int RonImportDestinationChoiceCount { get; private set; }
        public string? RonExportPath { get; set; }
        public string? MorphImportPath { get; set; }
        public string? SaveAsPath { get; set; }
        public MorphFaceGame? StandaloneGameChoiceResult { get; init; }
        public Action? StandaloneGameChoiceAction { get; init; }
        public string? StandaloneNameChoiceResult { get; set; }
        public string? LastInformation { get; private set; }
        public RonImportDestinationChoice? RonImportDestinationChoiceResult { get; set; }
        public UnsavedChangesChoice ConfirmUnsavedChangesResult { get; set; } = UnsavedChangesChoice.Cancel;
        public UnsavedChangesScope? LastUnsavedChangesScope { get; private set; }
        public string? CloneNameChoiceResult { get; set; }
        public bool ConfirmDeleteMorphResult { get; set; }
        public bool ConfirmCreateCareerMorph(string name) => true;
        public string? ChooseSaveAsFile(string sourcePath) => SaveAsPath;
        public NewPlayerMorphChoice? ChooseNewPlayerMorph() => null;
        public NewNpcMorphChoice? NewNpcMorphChoiceResult { get; set; }
        public NewNpcMorphChoice? ChooseNewNpcMorph() => NewNpcMorphChoiceResult;
        public string? ChooseNewPlayerMorphName(string suggestedName, IReadOnlyCollection<string> existingObjectNames) => null;
        public string? ChoosePackage(string? initialDirectory = null) => null;
        public MorphPackageSaveRequest? ChooseMorphPackageDestination(string suggestedFileName, string sourcePackagePath) => null;
        public MorphConversionSaveRequest? ChooseMorphConversionDestination(MorphFaceGame sourceGame, string suggestedFileName, string sourcePackagePath) => null;
        public string? ChooseCloneName(string suggestedName, IReadOnlyCollection<string> existingObjectNames) => CloneNameChoiceResult;
        public string? ChooseMorphImportFile(string? initialDirectory = null) => MorphImportPath;
        public string? ChooseMaterialImportFile(bool tse, string? initialDirectory = null) => null;
        public MorphFaceGame? ChooseStandaloneImportGame()
        {
            StandaloneGameChoiceCount++;
            StandaloneGameChoiceAction?.Invoke();
            return StandaloneGameChoiceResult;
        }
        public RonImportDestinationChoice? ChooseRonImportDestination(MorphFaceGame targetGame,
            IReadOnlyList<RonNpcArchetypeOption> archetypes, bool allowPlayer)
        {
            RonImportDestinationChoiceCount++;
            return RonImportDestinationChoiceResult;
        }
        public string? ChooseStandaloneMorphName(string suggestedName, IReadOnlyCollection<string> existingObjectNames) => StandaloneNameChoiceResult;
        public string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null) => RonExportPath;
        public string? ChooseMeshExportDirectory(string? initialDirectory = null) => null;
        public ActorAssignmentCandidate? ChooseActorAssignment(ActorAssignmentInventory inventory, ActorAssignmentMode mode) => null;
        public bool ConfirmDeleteMorph(string facePath, bool hasReferences = false) => ConfirmDeleteMorphResult;
        public UnsavedChangesChoice ConfirmUnsavedChanges(string assetPath, UnsavedChangesScope scope = UnsavedChangesScope.Package)
        {
            LastUnsavedChangesScope = scope;
            return ConfirmUnsavedChangesResult;
        }
        public void ShowInformation(string title, string message) => LastInformation = message;
        public void ShowTextureRegistrySettings() { }
    }

    internal static DecodedTextureAsset CreateTestTexture(string packagePath, string path, string parameterName) =>
        new(new AssetIdentity(packagePath, path, 42, "Texture2D"), 1, 1, [128, 128, 128, 255],
            "PF_B8G8R8A8", parameterName.EndsWith("_Diff", StringComparison.OrdinalIgnoreCase)
                ? TextureRole.Diffuse
                : TextureRole.Normal,
            TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, false, path);

    internal sealed class ImmediateTextureLoader(string? failInstancedPathSubstring = null) : ITextureReferenceLoader
    {
        public Task<DecodedTextureAsset> LoadTextureAsync(
            string packagePath,
            string texturePath,
            MaterialParameterDefinition definition,
            CancellationToken cancellationToken = default)
        {
            if (failInstancedPathSubstring is not null && texturePath.Contains(
                    failInstancedPathSubstring, StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException($"Test texture '{texturePath}' is unavailable.");
            return Task.FromResult(CreateTestTexture(packagePath, texturePath, definition.Name));
        }
    }
}
