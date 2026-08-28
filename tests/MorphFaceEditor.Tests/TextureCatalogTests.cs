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
        new("texture catalogue: search matches path package and origin", SearchMatchesUserFacingProvenance),
        new("texture catalogue: duplicate paths keep the highest mounted occurrence", DuplicatePathsKeepEffectiveOccurrence),
        new("texture catalogue: current local texture remains local when its path is indexed", CurrentLocalTextureRemainsLocal),
        new("texture catalogue: picker accepts registry candidates after the editor is already open", PickerAcceptsRegistryCandidatesAfterOpen),
        new("texture catalogue: missing registry preserves every package texture", MissingRegistryPreservesEveryPackageTexture),
        new("texture catalogue: local path suppresses installed duplicate", LocalPathSuppressesInstalledDuplicate),
        new("texture catalogue: merged picker ranks by relevance and source", MergedPickerRanksByRelevanceAndSource),
        new("texture catalogue: texture editor resolves installed paths without guessing", TextureEditorResolvesInstalledPathsWithoutGuessing),
        new("randomisation texture: required decode failure reports family signature", RequiredDecodeFailureReportsFamilySignature),
        new("texture registry: discovery admits morph HIR and shared-eye paths", DiscoveryAdmitsSupportedPaths),
        new("texture registry: occurrence retains mip storage metadata", OccurrenceRetainsMipStorageMetadata),
        new("texture registry: availability resolves installed and local paths", AvailabilityResolvesMergedPaths),
        new("texture registry: ambiguous object names are not resolved", AmbiguousObjectNamesAreRejected)
    ];

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
            new PackageReferenceService(reader),
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

    private static void MergedPickerRanksByRelevanceAndSource()
    {
        var session = MaterialTestFixtures.CreateSession();
        var current = session.GetSelectedTexture("HED_Diff")!;
        var localOther = new PackageAssetListItem(new AssetIdentity(
            current.Source.PackagePath, "WorkingPackage.Textures.Z_Local", 99, "Texture2D"));
        var preferred = Candidate("BIOG_HMM_HED_PROMorph.Add.HMM_HED_A", "preferred.pcc");
        var shared = Candidate("BIOG_HED_EYE.Textures.HED_EYE_A", "eyes.pcc");
        var general = Candidate("BIOG_SAL_HED_PROMorph.Add.SAL_HED_A", "general.pcc");
        using var reader = new MorphFacePackageReader();
        var editor = CreateTextureEditor(session, reader,
            [new PackageAssetListItem(current.Source), localOther], [general, shared, preferred],
            new TextureCatalogProfile("le3-human-male", ["HMM_HED"], ["HED_EYE"]), true);

        var ordered = editor.Candidates.Where(option => !option.IsNone).Select(option => option.InstancedPath).ToArray();
        TestAssert.True(ordered.SequenceEqual(
                [current.Source.InstancedPath, preferred.InstancedPath, shared.InstancedPath,
                 localOther.Identity.InstancedPath, general.InstancedPath]),
            "The merged picker did not rank active, preferred, shared, other local, and other installed textures in order.");
    }

    private static MaterialTextureEditorViewModel CreateTextureEditor(
        MaterialEditingSession session,
        MorphFacePackageReader reader,
        IReadOnlyList<PackageAssetListItem> local,
        IReadOnlyList<TextureCatalogCandidate>? installed = null,
        TextureCatalogProfile? profile = null,
        bool available = false) => new(
        session,
        new MaterialParameterDefinition("HED_Diff", "Diffuse", "skin", MaterialParameterKind.Texture,
            HeadMaterialFamily.Skin, TextureRole: TextureRole.Diffuse,
            ColorSpace: TextureColorSpace.Srgb, AlphaPolicy: TextureAlphaPolicy.Ignore),
        new PackageReferenceService(reader),
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
            "BIOG_ASA_EYE.Materials.ASA_EYE_Diff",
            "BIOG_KRO_EYE.Materials.KRO_EYE_Norm"
        ];

        TestAssert.True(admitted.All(TextureRegistryDiscovery.IsRelevantPath),
            "A supported morph, HIR, or shared-eye path was excluded from registry discovery.");
        TestAssert.True(!TextureRegistryDiscovery.IsRelevantPath("EngineResources.WhiteSquareTexture"),
            "An unrelated engine texture was admitted to the installed registry.");
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
            new PackageReferenceService(reader),
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
            new PackageReferenceService(reader),
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

}
