using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

/// <summary>Protects the detached catalogue ordering rules from package and WPF concerns.</summary>
public static class TextureCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture catalogue: active texture ranks before profile matches", ActiveTextureRanksFirst),
        new("texture catalogue: profile matches precede shared and general textures", ProfileMatchesRankBeforeSharedAndGeneral),
        new("texture catalogue: detached mesh profile covers supported racial scopes", DetachedMeshProfileCoversSupportedRacialScopes),
        new("texture catalogue: search matches path package and origin", SearchMatchesUserFacingProvenance),
        new("texture catalogue: malformed RON parent does not hide its valid repair candidate", MalformedRonParentKeepsRepairCandidate),
        new("player workspace: pickers admit only seek-free qualified BIOG references", PlayerPickerRequiresSeekFreePaths),
        new("texture catalogue: duplicate paths keep the highest mounted occurrence", DuplicatePathsKeepEffectiveOccurrence),
        new("texture catalogue: BIOG and cooked aliases collapse with mod override preserved", PickerCollapsesAliasesAndPreservesOverride),
        new("texture catalogue: current local texture remains local when its path is indexed", CurrentLocalTextureRemainsLocal),
        new("texture catalogue: picker accepts registry candidates after the editor is already open", PickerAcceptsRegistryCandidatesAfterOpen),
        new("texture catalogue: stale failed selection cannot discard a newer choice", StaleFailureCannotDiscardNewSelection),
        new("texture catalogue: selected display text does not become a search filter", SelectedDisplayTextDoesNotFilterPicker),
        new("texture catalogue: missing registry preserves every package texture", MissingRegistryPreservesEveryPackageTexture),
        new("texture catalogue: local path suppresses installed duplicate", LocalPathSuppressesInstalledDuplicate),
        new("texture catalogue: local hat textures stay out of the texture picker", LocalHatTextureIsNotPickable),
        new("texture catalogue: merged picker preserves candidate order and source", MergedPickerPreservesCandidateOrderAndSource),
        new("texture picker: hover preview stays outside committed selection", HoverPreviewStaysOutsideCommittedSelection),
        new("texture picker: stale hover preview cannot win", StaleHoverPreviewCannotWin),
        new("texture catalogue: texture editor resolves installed paths without guessing", TextureEditorResolvesInstalledPathsWithoutGuessing),
        new("randomisation texture: required decode failure reports family signature", RequiredDecodeFailureReportsFamilySignature),
        new("texture registry: discovery admits morph HIR and shared-eye paths", DiscoveryAdmitsSupportedPaths),
        new("texture registry: audit exclusions override broad head and HIR matching", DiscoveryAppliesAuditExclusions),
        new("texture registry: installed scans include GBL_Norm_Alpha across games", InstalledScansIncludeGlobalNormalAlpha),
        new("texture registry: occurrence retains mip storage metadata", OccurrenceRetainsMipStorageMetadata),
        new("texture registry: availability resolves installed and local paths", AvailabilityResolvesMergedPaths),
        new("texture registry: ambiguous object names are not resolved", AmbiguousObjectNamesAreRejected)
    ];

    private static void PlayerPickerRequiresSeekFreePaths()
    {
        const string qualified = "BIOG_HMF_HED_PROMorph_R.PROSheppard.HMF_HED_PROSheppard_Face_Diff_Stack";
        const string relative = "PROSheppard.HMF_HED_PROSheppard_Face_Diff_Stack";
        TestAssert.True(PlayerWorkspaceReferencePolicy.IsSeekFreeQualified(qualified),
            "A package-qualified BIOG reference was rejected.");
        TestAssert.True(!PlayerWorkspaceReferencePolicy.IsSeekFreeQualified(relative),
            "A package-relative reference was admitted to the Player picker.");

        var qualifiedTexture = Candidate(qualified, "EntryMenu.pcc");
        var relativeTexture = Candidate(relative, "BIOG_HMF_HED_PROMorph_R.pcc");
        var textures = PlayerWorkspaceReferencePolicy.SelectRegistryTextures([qualifiedTexture, relativeTexture]);
        TestAssert.Equal(1, textures.Count);
        TestAssert.Equal(qualified, textures[0].InstancedPath);

        var qualifiedMesh = new PackageAssetListItem(new AssetIdentity(
            "EntryMenu.pcc", "BIOG_HMF_HIR_PRO.Hair_PROShepard.HMF_HIR_PROShepard_MDL", 1, "SkeletalMesh"));
        var relativeMesh = new PackageAssetListItem(new AssetIdentity(
            "BIOG_HMF_HIR_PRO.pcc", "Hair_PROShepard.HMF_HIR_PROShepard_MDL", 2, "SkeletalMesh"));
        var meshes = PlayerWorkspaceReferencePolicy.SelectPackageAssets([qualifiedMesh, relativeMesh]);
        TestAssert.Equal(1, meshes.Count);
        TestAssert.Equal(qualifiedMesh.Identity, meshes[0].Identity);
    }

    private static void MalformedRonParentKeepsRepairCandidate()
    {
        const string malformed = "BIOG_HMM_HIR_PRO_R.PROCustomFade02.HMM_HIR_PROCustomFade01_Mask";
        const string valid = "BIOG_HMM_HIR_PRO_R.PROCustomFade01.HMM_HIR_PROCustomFade01_Mask";
        var candidate = Candidate(valid, "BIOG_HMM_HIR_PRO_R.pcc");
        var results = TextureCatalogSearch.FilterAndRank(
            [candidate],
            TextureCatalogProfile.Empty,
            malformed,
            "HMM_HIR_PROCustomFade01_Mask");

        TestAssert.Equal(1, results.Count);
        TestAssert.Equal(valid, results[0].InstancedPath);
    }

    private static void RequiredDecodeFailureReportsFamilySignature()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var diffuse = Candidate("BIOG_HMM_HED_PROMorph.Add.Missing_Diff", "missing.pcc");
        var normal = Candidate("BIOG_HMM_HED_PROMorph.Add.Missing_Norm", "missing.pcc");
        using var reader = new MorphFacePackageReader();
        var editor = new MaterialEditorViewModel(
            session,
            new StubColorDialogService(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            current.Source.PackagePath,
            [new PackageAssetListItem(current.Source)],
            _ => { },
            new HumanMaleFeatureMetadataCatalog(),
            [diffuse, normal],
            new TextureCatalogProfile("le3-human-male", ["HMM_HED"], []),
            true);
        var family = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Diff"] = diffuse.InstancedPath,
            ["HED_Norm"] = normal.InstancedPath
        };

        var prepared = editor.PrepareRandomisationAsync(
                new Dictionary<string, float>(),
                new Dictionary<string, System.Numerics.Vector4>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>> { ["human-face"] = family })
            .GetAwaiter().GetResult();

        TestAssert.True(prepared.FailedTextureFamilySignatures.Contains(
                MaterialRandomiser.TextureFamilySignature(family)),
            "A required decode failure did not identify the family that must be excluded on retry.");
    }

    private sealed class StubColorDialogService : IHdrColorDialogService
    {
        public bool ExtendedSliders { get; set; }
        public System.Numerics.Vector4? Edit(
            string title, System.Numerics.Vector4 value, Action<System.Numerics.Vector4> livePreview) => null;
        public System.Numerics.Vector4? EditStandard(
            string title, System.Numerics.Vector4 value, Action<System.Numerics.Vector4> livePreview) => null;
    }

    private static void TextureEditorResolvesInstalledPathsWithoutGuessing()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var unique = Candidate("BIOG_SAL_HED_PROMorph.Add.Unique_Diff", "sal.pcc");
        var ambiguousA = Candidate("BIOG_A_PROMorph.Add.Shared_Diff", "a.pcc");
        var ambiguousB = Candidate("BIOG_B_PROMorph.Add.Shared_Diff", "b.pcc");
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader,
            [new PackageAssetListItem(current.Source)], [unique, ambiguousA, ambiguousB],
            TextureCatalogProfile.Empty, true);

        TestAssert.True(editor.CanResolveInstancedPath(unique.InstancedPath),
            "An exact installed texture path was unavailable to randomisation.");
        TestAssert.True(editor.CanResolveInstancedPath("Unique_Diff"),
            "A unique installed object name was unavailable to randomisation.");
        TestAssert.True(!editor.CanResolveInstancedPath("Shared_Diff"),
            "An ambiguous object name was treated as a resolvable randomisation texture.");
    }

    private static void MissingRegistryPreservesEveryPackageTexture()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var unrelated = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Unrelated", 99, "Texture2D"));
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader,
            [new PackageAssetListItem(current.Source), unrelated]);

        TestAssert.True(editor.Candidates.Any(option => option.Asset == unrelated),
            "A package-local texture disappeared because the installed registry was unavailable.");
    }

    private static void LocalPathSuppressesInstalledDuplicate()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var local = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Custom_Diff", 99, "Texture2D"));
        var installed = Candidate(local.Identity.InstancedPath, "DLC_MOD_Custom\\external.pcc", TextureCatalogOrigin.Mod);
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader,
            [new PackageAssetListItem(current.Source), local], [installed],
            new TextureCatalogProfile("le3-human-male", ["HMM_HED"], ["HED_EYE"]), true);

        var matches = editor.Candidates.Where(option =>
            option.InstancedPath.Equals(local.Identity.InstancedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        TestAssert.Equal(1, matches.Length);
        TestAssert.True(matches[0].Asset == local && matches[0].RegistryCandidate is null,
            "An installed duplicate displaced or accompanied its authoritative package-local texture.");
    }

    private static void LocalHatTextureIsNotPickable()
    {
        var session = MaterialTestFixtures.CreateSession();
        var hat = new PackageAssetListItem(new AssetIdentity(
            "BIOG_HMM_HIR_PRO_R.pcc", "Cap.HMM_HAT_Cap_Diff_Stack", 45, "Texture2D"));
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader, [hat], [],
            TextureCatalogProfile.Empty, false);
        TestAssert.True(editor.Candidates.All(option => option.Asset != hat),
            "A package-local hat texture was offered as a new texture choice.");
    }

    private static void MergedPickerPreservesCandidateOrderAndSource()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var localOther = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Z_Local", 99, "Texture2D"));
        var preferred = Candidate("BIOG_HMM_HED_PROMorph.Add.HMM_HED_A", "preferred.pcc");
        var shared = Candidate("BIOG_HED_EYE.Textures.HED_EYE_A", "eyes.pcc");
        var general = Candidate("BIOG_SAL_HED_PROMorph.Add.SAL_HED_A", "general.pcc");
        var loader = new ControlledTextureLoader();
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader,
            [localOther, new PackageAssetListItem(current.Source)], [general, shared, preferred],
            new TextureCatalogProfile("le3-human-male", ["HMM_HED"], ["HED_EYE"]), true, loader);

        var ordered = editor.Candidates.Where(option => !option.IsNone).Select(option => option.InstancedPath).ToArray();
        TestAssert.True(ordered.SequenceEqual(
                [localOther.Identity.InstancedPath, current.Source.InstancedPath,
                 general.InstancedPath, shared.InstancedPath, preferred.InstancedPath]),
            "The merged picker reordered candidates instead of preserving local and installed source order.");

        var beforeSelection = ordered;
        editor.SelectedTexture = editor.Candidates.Single(option => option.Asset == localOther);
        loader.Complete(localOther.Identity.InstancedPath, CreateDecoded(localOther.Identity));
        TestAssert.True(SpinWait.SpinUntil(() => !editor.IsBusy, TimeSpan.FromSeconds(2)),
            "The test texture selection did not finish.");
        var afterSelection = editor.Candidates.Where(option => !option.IsNone)
            .Select(option => option.InstancedPath)
            .ToArray();
        TestAssert.True(afterSelection.SequenceEqual(beforeSelection),
            "Selecting a texture reordered the related candidates in the open picker.");

        var installedOption = editor.Candidates.Single(option => option.RegistryCandidate == preferred);
        TestAssert.True(installedOption.Asset is not null && installedOption.Asset.Thumbnail is null,
            "An installed texture was not given a lazy thumbnail source.");
    }

    private static void HoverPreviewStaysOutsideCommittedSelection()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var candidate = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Hover_Diff", 103, "Texture2D"));
        var loader = new ControlledTextureLoader();
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader,
            [new PackageAssetListItem(current.Source), candidate],
            available: false,
            references: loader);
        var option = editor.Candidates.Single(value => value.Asset == candidate);

        var preview = editor.PreviewTextureAsync(option);
        loader.Complete(candidate.Identity.InstancedPath, CreateDecoded(candidate.Identity));
        preview.GetAwaiter().GetResult();

        TestAssert.Equal(current.Source.InstancedPath, session.GetSelectedTexture("HED_Diff")?.Source.InstancedPath);
        TestAssert.Equal(candidate.Identity.InstancedPath, session.GetPreviewTexture("HED_Diff")?.Source.InstancedPath);
        TestAssert.True(!session.CanUndo, "Hover preview created an undo entry before the candidate was clicked.");

        editor.CancelTexturePreview();
        TestAssert.Equal(current.Source.InstancedPath, session.GetPreviewTexture("HED_Diff")?.Source.InstancedPath);

        editor.PreviewTextureAsync(editor.Candidates[0]).GetAwaiter().GetResult();
        TestAssert.Equal(current.Source.InstancedPath, session.GetPreviewTexture("HED_Diff")?.Source.InstancedPath);
        editor.CancelTexturePreview();

        editor.PreviewTextureAsync(option).GetAwaiter().GetResult();
        editor.CommitTexture(option);
        loader.Complete(candidate.Identity.InstancedPath, CreateDecoded(candidate.Identity));
        TestAssert.True(SpinWait.SpinUntil(() => !editor.IsBusy, TimeSpan.FromSeconds(2)),
            "The committed texture selection did not finish.");
        TestAssert.Equal(candidate.Identity.InstancedPath, session.GetSelectedTexture("HED_Diff")?.Source.InstancedPath);
        TestAssert.True(session.CanUndo, "Clicking a candidate did not create a normal undo entry.");
        session.Undo();
        TestAssert.Equal(current.Source.InstancedPath, session.GetSelectedTexture("HED_Diff")?.Source.InstancedPath);
    }

    private static void StaleHoverPreviewCannotWin()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var first = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Hover_First", 104, "Texture2D"));
        var second = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Hover_Second", 105, "Texture2D"));
        var loader = new ControlledTextureLoader();
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader,
            [new PackageAssetListItem(current.Source), first, second],
            available: false,
            references: loader);

        var firstPreview = editor.PreviewTextureAsync(editor.Candidates.Single(value => value.Asset == first));
        var secondPreview = editor.PreviewTextureAsync(editor.Candidates.Single(value => value.Asset == second));
        loader.Complete(first.Identity.InstancedPath, CreateDecoded(first.Identity));
        loader.Complete(second.Identity.InstancedPath, CreateDecoded(second.Identity));
        Task.WaitAll(firstPreview, secondPreview);

        TestAssert.Equal(second.Identity.InstancedPath, session.GetPreviewTexture("HED_Diff")?.Source.InstancedPath);
        TestAssert.Equal(current.Source.InstancedPath, session.GetSelectedTexture("HED_Diff")?.Source.InstancedPath);
        TestAssert.True(!session.CanUndo, "A stale hover preview created an undo entry.");
    }

    private static MaterialTextureEditorViewModel CreateTextureEditor(
        MaterialEditingSession session,
        MorphFacePackageReader reader,
        IReadOnlyList<PackageAssetListItem> local,
        IReadOnlyList<TextureCatalogCandidate>? installed = null,
        TextureCatalogProfile? profile = null,
        bool available = false,
        ITextureReferenceLoader? references = null) => new(
        session,
        new MaterialParameterDefinition("HED_Diff", "Diffuse", "skin", MaterialParameterKind.Texture,
            HeadMaterialFamily.Skin, TextureRole: TextureRole.Diffuse,
            ColorSpace: TextureColorSpace.Srgb, AlphaPolicy: TextureAlphaPolicy.Ignore),
        references ?? new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
        session.GetSelectedTexture("HED_Diff")!.Source.PackagePath,
        local,
        message => throw new Exception(message),
        installed,
        profile,
        available);

    private static void OccurrenceRetainsMipStorageMetadata()
    {
        var occurrence = Occurrence("BioG_Sal.pcc", 0, TextureCatalogOrigin.BaseGame) with
        {
            Mips = [new TextureMipStorageRecord(0, 1024, 512, 0x11, 524288, 131072, 4096, "Textures_DLC_MOD")]
        };

        var mip = occurrence.Mips.Single();
        TestAssert.Equal(0x11, mip.StorageType);
        TestAssert.Equal(4096, mip.ExternalOffset);
        TestAssert.Equal("Textures_DLC_MOD", mip.TextureCacheName);
    }

    private static void DiscoveryAdmitsSupportedPaths()
    {
        string[] admitted =
        [
            "BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add1",
            "BIOG_HMM_HIR_PRO.Hair.HMM_HIR_Diff",
            "biog_hmf_hir_pro.hair.hmf_hir_norm",
            "BIOG_HMF_HED_Alignment.Scar.HMF_Face_NormScars_03",
            "BIOG_Humanoid_MASTER_MTR_R.Skin_HumanHED_SpecMulitplier_Mask",
            "BIOG_HMM_EYE.Eye.EYE_Iris_Norm",
            "BIOG_ASA_EYE.Materials.ASA_EYE_Diff",
            "BIOG_KRO_EYE.Materials.KRO_EYE_Norm",
            "BIOG_Humanoid_MASTER_MTR_R.GBL_Norm_Alpha",
            "BIOG_Humanoid_MASTER_MTR_R.Skin_HumanScalp_SpecMulitplier_Mask",
            "BIOG_Humanoid_MASTER_MTR_R.Human.Teeth.HED_PRO_Teeth"
        ];

        TestAssert.True(admitted.All(TextureRegistryDiscovery.IsRelevantPath),
            "A supported morph, HIR, or shared-eye path was excluded from registry discovery.");
        TestAssert.True(!TextureRegistryDiscovery.IsRelevantPath("EngineResources.WhiteSquareTexture"),
            "An unrelated engine texture was admitted to the installed registry.");
    }

    private static void DiscoveryAppliesAuditExclusions()
    {
        string[] excluded =
        [
            "BIOG_GTH_HED_PROMorph_R.Head.GTH_HED_Diff",
            "BIOG_HMF_HED_PROJack_ALT_R.Visor.CM_ChromeJackVisor",
            "BIOG_TUF_HED_PROMorph_R.NYR_HED.NYR_HED_Diff",
            "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_Diff_Stack",
            "BIOG_HMF_HIR_PRO_R.Cap.HMF_HGR_Cap_Diff_Stack",
            "BIOG_HMF_HED_PROMorph_R.GUI_HMF_HED_Diff",
            "BIOG_HMM_HIR_PRO_R.Hair.HMM_HIR_Long_Diff_CC",
            "BIOG_TUR_HED_PROGarrus.Visor.TUR_HED_Visor_Diff",
            "BIOG_HMM_HED_PROKaiLang.HMM_HGR_Hair_Diff",
            "biog_hmm_hed_gnr_r.Textures.Cube_EyesBlue",
            "BIOG_HMM_HED_PROHackett.Textures.HMM_HGR_Hair_Diff",
            "BIOG_HMF_HED_PROJack_ALT_R.Visor.HMF_HED_Visor_Diff"
        ];
        TestAssert.True(excluded.All(TextureRegistryDiscovery.IsExcludedPath),
            "An audited exclusion still reaches the texture picker.");
        TestAssert.True(excluded.All(path => !TextureRegistryDiscovery.IsRelevantPath(path)),
            "An audited exclusion was re-admitted by a broad discovery fragment.");
        TestAssert.True(!TextureRegistryDiscovery.IsExcludedPath(
                "BIOG_HMF_HIR_PRO_R.Hair.HMF_HIR_Diff"),
            "An ordinary HIR hair texture was excluded.");
        TestAssert.True(!TextureRegistryDiscovery.IsExcludedPath(
                "BIOG_HMM_HIR_PRO_R.Hair.HMM_HIR_Long_CC_Diff"),
            "The literal _CC ending rule excluded a different HIR texture.");
        TestAssert.True(TextureRegistryDiscovery.IsExcludedPath(
                TextureCatalogPicker.CanonicalPath("Cap.HMM_HAT_Cap_Diff_Stack",
                    "BIOG_HMM_HIR_PRO_R.pcc")),
            "A short export path inside a BIOG HIR package bypassed the hat exclusion.");
        TestAssert.True(!TextureRegistryDiscovery.IsExcludedPath(
                "BIOG_HMF_HAT_Standalone.HMF_HAT_Cap_Diff"),
            "The HAT exclusion escaped its BIOG human HIR scope.");
    }

    private static void AvailabilityResolvesMergedPaths()
    {
        const string localPath = "WorkingPackage.Textures.Custom_Diff";
        const string installedPath = "BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add1";
        var availability = new TextureCatalogAvailability(
            [localPath],
            [Candidate(installedPath, "BioG_Sal.pcc")]);

        TestAssert.True(availability.TryResolve(localPath.ToLowerInvariant(), out var resolvedLocal),
            "The merged catalogue did not resolve a local path case-insensitively.");
        TestAssert.Equal(localPath, resolvedLocal);
        TestAssert.True(availability.TryResolve("SAL_HED_PRO_Add1", out var resolvedInstalled),
            "A unique installed object name did not resolve through the merged catalogue.");
        TestAssert.Equal(installedPath, resolvedInstalled);
    }

    private static void AmbiguousObjectNamesAreRejected()
    {
        var availability = new TextureCatalogAvailability(
            ["WorkingPackage.Textures.Shared_Diff"],
            [Candidate("BIOG_SAL_HED_PROMorph_R.Add.Shared_Diff", "BioG_Sal.pcc")]);

        TestAssert.True(!availability.TryResolve("Shared_Diff", out _),
            "An ambiguous object-name-only texture request resolved to an arbitrary package.");
    }

    private static void ActiveTextureRanksFirst()
    {
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], ["ASA_EYE"]);
        var active = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add2", "base.pcc");
        var preferred = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1", "base.pcc");

        var ranked = TextureCatalogSearch.FilterAndRank([preferred, active], profile, active.InstancedPath, string.Empty);

        TestAssert.Equal(active.InstancedPath, ranked[0].InstancedPath);
    }

    private static void InstalledScansIncludeGlobalNormalAlpha()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cases = new[]
        {
            (MorphFaceGame.LE1, LegendaryExplorerCoreRuntime.DefaultLe1CookedPath, "BIOC_Materials.pcc"),
            (MorphFaceGame.LE2, LegendaryExplorerCoreRuntime.DefaultLe2CookedPath, "SFXGame.pcc"),
            (MorphFaceGame.LE3, LegendaryExplorerCoreRuntime.DefaultLe3CookedPath, "BioP_Char.pcc")
        };
        if (cases.Any(value => string.IsNullOrWhiteSpace(value.Item2)))
        {
            return;
        }

        const string path = "BIOG_Humanoid_MASTER_MTR_R.GBL_Norm_Alpha";
        var scanner = new LecTextureRegistryPackageScanner();
        foreach (var (game, cookedPath, fileName) in cases)
        {
            var packagePath = Path.Combine(cookedPath!, fileName);
            if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException($"The installed {game} registry donor is missing.", packagePath);
            }
            var scan = scanner.Scan(game, packagePath, CancellationToken.None);
            TestAssert.True(scan.Textures.Any(value =>
                    value.InstancedPath.Equals(path, StringComparison.OrdinalIgnoreCase)),
                $"The installed {game} registry scan omitted '{path}' from {fileName}.");
        }
    }

    private static void DetachedMeshProfileCoversSupportedRacialScopes()
    {
        var detachedProfile = TextureCatalogProfiles.For(new MorphFaceProfile(
            "le3-detached-mesh",
            "Detached Custom Mesh",
            MorphFaceGame.LE3,
            string.Empty,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HumanMaleFeatureMetadataCatalog(),
            "[MESH]",
            "#66717D",
            _ => false,
            (_, _) => false));

        var requiredHeadScopes = new[]
        {
            "HMM_HED", "HMN_HED", "HMF_HED", "ASA_HED", "SAL_HED", "TUR_HED",
            "KRO_HED", "BAT_HED", "ALN_HED", "HMF_HIR", "HMM_HIR"
        };
        var requiredEyeScopes = new[]
        {
            "HMM_EYE", "HMF_EYE", "HED_EYE", "ASA_EYE", "SAL_EYE", "TUR_EYE",
            "KRO_EYE", "ALN_EYE", "Eye_Norm", "HAIR_"
        };

        TestAssert.True(requiredHeadScopes.All(detachedProfile.PreferredPathFragments.Contains),
            "Detached texture discovery did not include every supported racial head/hair scope.");
        TestAssert.True(requiredEyeScopes.All(detachedProfile.SharedPathFragments.Contains),
            "Detached texture discovery did not include every supported eye/hair scope.");
        TestAssert.True(!detachedProfile.SharedPathFragments.Contains("BAT_EYE"),
            "Detached texture discovery introduced an eye scope for Batarians, which have no eye material.");
    }

    private static void ProfileMatchesRankBeforeSharedAndGeneral()
    {
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], ["ASA_EYE"]);
        var general = Candidate("BIOG_TUR_HED_PROMorph_R.Adds.TUR_HED_PRO_Add1", "turian.pcc");
        var shared = Candidate("BIOG_ASA_EYE.ASA_EYE_Diff", "eyes.pcc");
        var preferred = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1", "asari.pcc");

        var ranked = TextureCatalogSearch.FilterAndRank([general, shared, preferred], profile, null, string.Empty);

        TestAssert.True(ranked.Select(candidate => candidate.InstancedPath).SequenceEqual(
            [preferred.InstancedPath, shared.InstancedPath, general.InstancedPath]),
            "Profile ranking did not keep preferred, shared, and general texture candidates in that order.");
    }

    private static void SearchMatchesUserFacingProvenance()
    {
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], []);
        var modded = Candidate(
            "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Custom",
            "DLC_MOD_Custom\\CookedPCConsole\\ModdedTextures.pcc",
            TextureCatalogOrigin.Mod);
        var vanilla = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1", "BioG_Asa.pcc");

        var byObjectName = TextureCatalogSearch.FilterAndRank([vanilla, modded], profile, null, "custom");
        var byPackage = TextureCatalogSearch.FilterAndRank([vanilla, modded], profile, null, "moddedtextures");
        var byOrigin = TextureCatalogSearch.FilterAndRank([vanilla, modded], profile, null, "mod");

        TestAssert.Equal(modded.InstancedPath, byObjectName.Single().InstancedPath);
        TestAssert.Equal(modded.InstancedPath, byPackage.Single().InstancedPath);
        TestAssert.Equal(modded.InstancedPath, byOrigin.Single().InstancedPath);
    }

    private static void DuplicatePathsKeepEffectiveOccurrence()
    {
        var path = "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1";
        var baseOccurrence = Occurrence("base.pcc", mountPriority: 0, TextureCatalogOrigin.BaseGame);
        var modOccurrence = Occurrence("DLC_MOD_Custom\\CookedPCConsole\\mod.pcc", mountPriority: 9021, TextureCatalogOrigin.Mod);
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE3,
            path,
            modOccurrence,
            [baseOccurrence, modOccurrence]);

        TestAssert.Equal(modOccurrence, candidate.EffectiveOccurrence);
        TestAssert.Equal(2, candidate.Occurrences.Count);
    }

    private static void PickerCollapsesAliasesAndPreservesOverride()
    {
        const string biogPath = "Hair.HMF_HIR_Test_Diff";
        const string qualifiedPath = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Test_Diff";
        var biogOccurrence = Occurrence("BIOG_HMF_HIR_PRO.pcc", 0, TextureCatalogOrigin.BaseGame);
        var levelOccurrence = Occurrence("BIOA_TEST.pcc", 0, TextureCatalogOrigin.BaseGame);
        var modOccurrence = Occurrence("DLC_MOD_Test\\CookedPCConsole\\ModHair.pcc", 9000, TextureCatalogOrigin.Mod);
        var biog = new TextureCatalogCandidate(TextureCatalogGame.LE3, biogPath,
            biogOccurrence, [biogOccurrence]);
        var level = new TextureCatalogCandidate(TextureCatalogGame.LE3, qualifiedPath,
            modOccurrence, [levelOccurrence, modOccurrence]);

        var choices = TextureCatalogPicker.Select([level, biog]);
        TestAssert.Equal(2, choices.Count);
        TestAssert.Equal(biogOccurrence, choices[0].EffectiveOccurrence);
        TestAssert.Equal(modOccurrence, choices[1].EffectiveOccurrence);
        TestAssert.Equal(qualifiedPath, TextureCatalogPicker.CanonicalPath(
            choices[0].InstancedPath, choices[0].EffectiveOccurrence.PackagePath));
    }

    private static void CurrentLocalTextureRemainsLocal()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var registryCandidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE3,
            current.Source.InstancedPath,
            Occurrence("DLC_MOD_Texture\\CookedPCConsole\\external.pcc", 9000, TextureCatalogOrigin.Mod),
            [Occurrence("DLC_MOD_Texture\\CookedPCConsole\\external.pcc", 9000, TextureCatalogOrigin.Mod)]);
        using var reader = new MorphFacePackageReader();
        var editor = new MaterialTextureEditorViewModel(
            session,
            new MaterialParameterDefinition("HED_Diff", "Diffuse", "skin", MaterialParameterKind.Texture,
                HeadMaterialFamily.Skin, TextureRole: TextureRole.Diffuse,
                ColorSpace: TextureColorSpace.Srgb, AlphaPolicy: TextureAlphaPolicy.Ignore),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            current.Source.PackagePath,
            [new PackageAssetListItem(current.Source)],
            message => throw new Exception(message),
            [registryCandidate],
            new TextureCatalogProfile("le3-human-male", ["HMM_HED"], []),
            isRegistryAvailable: true);

        TestAssert.True(editor.SelectedTexture?.Asset?.Identity == current.Source,
            "The current local texture was replaced by a registry option solely because its instanced path matched.");
        TestAssert.True(!editor.HasExternalRegistrySelection,
            "A current local texture sharing a registry path was incorrectly treated as an external selection.");
    }

    private static void PickerAcceptsRegistryCandidatesAfterOpen()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var registryCandidate = Candidate("BIOG_HMM_HED_PROMorph.Adds.HMM_HED_PRO_Custom", "mod.pcc", TextureCatalogOrigin.Mod);
        using var reader = new MorphFacePackageReader();
        var editor = new MaterialTextureEditorViewModel(
            session,
            new MaterialParameterDefinition("HED_Diff", "Diffuse", "skin", MaterialParameterKind.Texture,
                HeadMaterialFamily.Skin, TextureRole: TextureRole.Diffuse,
                ColorSpace: TextureColorSpace.Srgb, AlphaPolicy: TextureAlphaPolicy.Ignore),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            current.Source.PackagePath,
            [new PackageAssetListItem(current.Source)],
            message => throw new Exception(message));

        editor.UpdateRegistryCandidates(
            [registryCandidate],
            new TextureCatalogProfile("le3-human-male", ["HMM_HED"], []),
            isRegistryAvailable: true);

        TestAssert.True(editor.IsRegistryAvailable, "The already-open picker did not adopt the ready registry state.");
        TestAssert.True(editor.Candidates.Any(option => option.RegistryCandidate == registryCandidate),
            "The background registry result did not populate the already-open picker.");
        TestAssert.True(editor.SelectedTexture?.Asset?.Identity == current.Source,
            "Populating registry candidates replaced the current local texture selection.");
    }

    private static void StaleFailureCannotDiscardNewSelection()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var first = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.First_Diff", 101, "Texture2D"));
        var second = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Second_Diff", 102, "Texture2D"));
        var loader = new ControlledTextureLoader();
        var errors = new List<string>();
        var editor = new MaterialTextureEditorViewModel(
            session,
            new MaterialParameterDefinition("HED_Diff", "Diffuse", "skin", MaterialParameterKind.Texture,
                HeadMaterialFamily.Skin, TextureRole: TextureRole.Diffuse,
                ColorSpace: TextureColorSpace.Srgb, AlphaPolicy: TextureAlphaPolicy.Ignore),
            loader,
            current.Source.PackagePath,
            [new PackageAssetListItem(current.Source), first, second],
            errors.Add);
        var firstOption = editor.Candidates.Single(value => value.Asset == first);
        var secondOption = editor.Candidates.Single(value => value.Asset == second);

        editor.SelectedTexture = firstOption;
        editor.SelectedTexture = secondOption;
        loader.Fail(first.Identity.InstancedPath, new InvalidDataException("first failed late"));
        loader.Complete(second.Identity.InstancedPath, CreateDecoded(second.Identity));

        TestAssert.True(SpinWait.SpinUntil(() =>
                session.GetSelectedTexture("HED_Diff")?.Source == second.Identity && !editor.IsBusy,
                TimeSpan.FromSeconds(2)),
            "The newer texture selection was discarded after an older request failed.");
        TestAssert.Equal(0, errors.Count);
        TestAssert.True(ReferenceEquals(secondOption, editor.SelectedTexture),
            "The picker reverted to stale session state while the newer selection was loading.");
    }

    private static void SelectedDisplayTextDoesNotFilterPicker()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var registryCandidate = Candidate(
            "BIOG_HMM_HED_PROMorph.Adds.HMM_HED_PRO_Custom",
            "mod.pcc",
            TextureCatalogOrigin.Mod);
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(
            session,
            reader,
            [new PackageAssetListItem(current.Source)],
            [registryCandidate],
            new TextureCatalogProfile("le3-human-male", ["HMM_HED"], []),
            true);

        editor.SearchText = "FaceD";
        TestAssert.True(!editor.Candidates.Any(option => option.RegistryCandidate == registryCandidate),
            "The test search did not narrow the picker before selection synchronisation.");

        // WPF writes the selected item's display value back through an editable ComboBox's Text binding.
        editor.SearchText = editor.SelectedTexture!.DisplayName;

        TestAssert.Equal(string.Empty, editor.SearchText);
        TestAssert.True(editor.Candidates.Any(option => option.RegistryCandidate == registryCandidate),
            "Selecting a searched texture left the picker trapped behind its previous query.");

        var candidateCount = editor.Candidates.Count;
        editor.SearchText = registryCandidate.InstancedPath;
        TestAssert.Equal(candidateCount, editor.Candidates.Count);
        TestAssert.True(editor.Candidates.Any(option => option.RegistryCandidate == registryCandidate),
            "Selection synchronisation arriving before SelectedItem rebuilt the candidate list.");
    }

    private static TextureCatalogCandidate Candidate(
        string path,
        string packagePath,
        TextureCatalogOrigin origin = TextureCatalogOrigin.BaseGame) => new(
        TextureCatalogGame.LE3,
        path,
        Occurrence(packagePath, 0, origin),
        [Occurrence(packagePath, 0, origin)]);

    private static TextureCatalogOccurrence Occurrence(
        string packagePath,
        int mountPriority,
        TextureCatalogOrigin origin) => new(
        packagePath,
        ExportUIndex: 42,
        MountPriority: mountPriority,
        Origin: origin,
        Width: 1024,
        Height: 1024,
        PixelFormat: "PF_DXT1",
        TextureGroup: "TEXTUREGROUP_Character",
        HasExternalMips: false,
        TextureFileCacheName: null);

    private static DecodedTextureAsset CreateDecoded(AssetIdentity identity) => new(
        identity, 1, 1, [128, 128, 128, 255], "PF_B8G8R8A8",
        TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore,
        false, identity.InstancedPath);

    private sealed class ControlledTextureLoader : ITextureReferenceLoader
    {
        private readonly Dictionary<string, TaskCompletionSource<DecodedTextureAsset>> _requests =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<DecodedTextureAsset> LoadTextureAsync(
            string packagePath,
            string texturePath,
            MaterialParameterDefinition definition,
            CancellationToken cancellationToken = default)
        {
            var request = new TaskCompletionSource<DecodedTextureAsset>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _requests[texturePath] = request;
            cancellationToken.Register(() => request.TrySetCanceled(cancellationToken));
            return request.Task;
        }

        public void Complete(string texturePath, DecodedTextureAsset texture) =>
            _requests[texturePath].TrySetResult(texture);

        public void Fail(string texturePath, Exception exception) =>
            _requests[texturePath].TrySetException(exception);
    }

}
