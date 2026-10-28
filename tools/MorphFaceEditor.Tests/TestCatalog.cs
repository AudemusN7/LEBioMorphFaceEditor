namespace MorphFaceEditor.Tests;

// Every case has one suite owner. QA owns broad workflow matrices; these suites
// retain focused contracts, negative paths and deterministic native regressions.
internal static class TestCatalog
{
    internal static IReadOnlyDictionary<string, IReadOnlyList<TestCase>> Suites { get; } =
        new Dictionary<string, IReadOnlyList<TestCase>>(StringComparer.OrdinalIgnoreCase)
        {
            ["core"] = [.. DeformationTests.All, .. EditingTests.All, .. FemaleTurianTests.All, .. DetachedMeshPreviewTests.All,
                .. CustomMaterialWorkspaceTests.All, .. RandomisationTests.Runtime,
                .. PlayerFacialDetailTexturePolicyTests.All, .. PlayerHairStyleCatalogTests.All,
                .. PlayerFaceScarTexturePolicyTests.All, .. Player2DaRandomisationPolicyTests.All],
            ["materials"] = [.. MaterialEditingTests.All,
                .. TextureHistoryTests.All,
                .. MaterialRendererTests.All, .. HumanSkinMaterialTests.All, .. HumanScalpRegressionTests.All,
                .. HairMaterialTests.All, .. EyeMaterialTests.All, .. AsariMaterialTests.All,
                .. SalarianMaterialTests.All, .. TurianMaterialTests.All, .. BatarianMaterialTests.All,
                .. KroganMaterialTests.All, .. VorchaMaterialTests.All],
            ["registry"] = [.. TextureRegistryStoreTests.All, .. TextureCatalogTests.All,
                .. InstalledAssetResolutionTests.All, .. TextureTransferDonorPolicyTests.All],
            ["rendering"] = [.. RenderingTests.All, .. AmbientOcclusionRenderingTests.All,
                .. EnvironmentLightingTests.All, .. EnvironmentRenderingTests.All,
                .. HumanHeadShadowTests.All, .. NativeHairPreviewTests.All],
            ["ui"] = [.. FemaleTurianTests.Ui, .. TufTattooRandomisationTests.All, .. UiPresentationTests.All, .. UiWorkspaceTests.All, .. UiInterchangeTests.All, .. UiRandomisationTests.All,
                .. UiPickerTests.All, .. UiEditorTests.All, .. UiProfileTests.All,
                .. AutomationCommandTests.All, .. VisorLensAnimationLifecycleTests.All, .. RandomisationPublicationTests.All,
                .. ApplicationSettingsTests.All, .. LoggingTests.All, .. LocalisationTests.All, .. GitHubUpdateServiceTests.All,
                .. TextureRegistrySettingsTests.All, .. MorphFaceCatalogTests.All],
            ["package"] = [.. PackagePersistenceTests.All, .. NativeMaterialReaderTests.All,
                .. PreviewPackageCacheTests.All,
                .. StandaloneImportTests.All, .. LegacyImportTests.All, .. PackageValueVerificationTests.All,
                .. PackageIntegrityTests.All, .. CompletedPccCloneTests.All,
                .. ActorAssignmentInventoryTests.All,
                .. ActorAssignmentTransactionTests.All, .. CustomMaterialEvidenceTests.All,
                .. ShaderMapOnlyReaderTests.All, .. SupportedMaterialGraphPolicyTests.All,
                .. MaterialInstancePreservationTests.All, .. Le2LocalizedEyeOverrideImportRoleTests.All,
                .. HeadHairTextureOwnershipTests.All, .. NativeHairMaterialSupportTests.All,
                .. PlayerRuntimeAssetTests.All],
            ["conversion"] = [.. ConversionTests.All],
            ["interchange"] = [.. RonCodecTests.All, .. PcsavHeadMorphTests.All,
                .. MorphFaceInterchangeExportTests.All, .. MaterialInterchangeTests.All,
                .. Le2PlayerMaterialRonIdentityTests.All],
            ["mesh"] = [.. CustomMeshTests.All,
                .. ImportedSkeletalMeshWriterTests.All, .. CustomMeshPccMaterializerTests.All,
                .. GltfInverseMorphTests.All, .. GltfTangentHandednessTests.All,
                .. PccTextureDependencyResolverTests.All],
            ["tooling"] = [.. LocaliserTests.All, .. RandomisationTests.Tooling, .. QaUtilityTests.All,
                .. QaNpcEyeGraphCheckTests.All]
        };
}
