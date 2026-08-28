using MorphFaceEditor.Core.Materials;
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
        new("texture catalogue: projector filters paths and verifies exact Texture2D exports", ProjectorFiltersAndVerifies),
        new("texture catalogue: current local texture remains local when its path is indexed", CurrentLocalTextureRemainsLocal),
        new("texture catalogue: projection honours cancellation before package resolution", ProjectionHonoursCancellation),
        new("texture catalogue: picker accepts registry candidates after the editor is already open", PickerAcceptsRegistryCandidatesAfterOpen),
        new("texture registry: discovery admits morph HIR and shared-eye paths", DiscoveryAdmitsSupportedPaths),
        new("texture registry: occurrence retains mip storage metadata", OccurrenceRetainsMipStorageMetadata),
        new("texture registry: availability resolves installed and local paths", AvailabilityResolvesMergedPaths),
        new("texture registry: ambiguous object names are not resolved", AmbiguousObjectNamesAreRejected)
    ];

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

    private static void ProjectorFiltersAndVerifies()
    {
        const string preferredPath = "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1";
        const string sharedEyePath = "BIOG_ASA_EYE.ASA_EYE_Diff";
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], ["ASA_EYE"]);
        var resolver = new FakeOccurrenceResolver(new Dictionary<(string Package, string Path), TextureCatalogOccurrence>
        {
            [("base.pcc", preferredPath)] = Occurrence("base.pcc", 0, TextureCatalogOrigin.BaseGame),
            [("mod.pcc", preferredPath)] = Occurrence("mod.pcc", 9000, TextureCatalogOrigin.Mod),
            [("eye.pcc", sharedEyePath)] = Occurrence("eye.pcc", 0, TextureCatalogOrigin.BaseGame)
        });
        var entries = new[]
        {
            new TextureCatalogIndexEntry(preferredPath, ["base.pcc", "mod.pcc"]),
            new TextureCatalogIndexEntry(sharedEyePath, ["eye.pcc"]),
            new TextureCatalogIndexEntry("BIOG_ASA_HED_PROMorph_R.Adds.NotATexture", ["invalid.pcc"]),
            new TextureCatalogIndexEntry("Materials.Anamorphic", ["irrelevant.pcc"])
        };

        var candidates = TextureCatalogProjector.Project(MorphFaceGame.LE3, profile, entries, resolver);

        TestAssert.Equal(2, candidates.Count);
        var preferred = candidates.Single(candidate => candidate.InstancedPath == preferredPath);
        TestAssert.Equal("mod.pcc", preferred.EffectiveOccurrence.PackagePath);
        TestAssert.Equal(2, preferred.Occurrences.Count);
        TestAssert.True(candidates.Any(candidate => candidate.InstancedPath == sharedEyePath),
            "A profile-declared shared eye texture was not admitted to the compact catalogue.");
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

    private static void ProjectionHonoursCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = TextureCatalogProjector.Project(
                MorphFaceGame.LE1,
                TextureCatalogProfile.Empty,
                [new TextureCatalogIndexEntry("BIOG_HMM_HED_PROMorph.Adds.Texture", ["fixture.pcc"])],
                new FakeOccurrenceResolver(new Dictionary<(string Package, string Path), TextureCatalogOccurrence>()),
                cancellation.Token);
            throw new Exception("Cancelled texture-catalogue projection completed.");
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation must be checked before any PCC is opened.
        }
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

    private sealed class FakeOccurrenceResolver(
        IReadOnlyDictionary<(string Package, string Path), TextureCatalogOccurrence> occurrences) : ITextureCatalogOccurrenceResolver
    {
        public bool TryResolve(
            MorphFaceGame game,
            string packagePath,
            string instancedPath,
            out TextureCatalogOccurrence occurrence) =>
            occurrences.TryGetValue((packagePath, instancedPath), out occurrence!);
    }
}
