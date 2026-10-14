using System.Text.Json.Serialization;

namespace MorphFaceEditor.Core.Materials;

/// <summary>Detached storage metadata for one non-empty texture mip.</summary>
public sealed record TextureMipStorageRecord(
    int Index,
    int Width,
    int Height,
    int StorageType,
    int UncompressedSize,
    int CompressedSize,
    int ExternalOffset,
    string? TextureCacheName);

/// <summary>The complete compact registry payload for one installed Legendary Edition game.</summary>
public sealed record TextureRegistrySnapshot(
    int SchemaVersion,
    TextureCatalogGame Game,
    DateTimeOffset BuiltAtUtc,
    int InstalledPackageCount,
    IReadOnlyList<TextureCatalogCandidate> Candidates)
{
    public const int CurrentSchemaVersion = 1;

    // Complete identity index, including dependencies outside the head pickers.
    public IReadOnlyList<InstalledAssetOccurrence> Assets { get; init; } = [];
    /// <summary>Highest official package for each physical filename, independent of mod overrides.</summary>
    public IReadOnlyList<string> OfficialPackagePaths { get; init; } = [];
    public IReadOnlyList<string> MountedPackagePaths { get; init; } = [];
    public IReadOnlyList<MountedPackageFingerprint> MountedPackages { get; init; } = [];

    public IReadOnlyList<MorphFaceTemplateCandidate> MorphFaceTemplates { get; init; } = [];
    public IReadOnlyList<AttachmentMeshCandidate> AttachmentMeshes { get; init; } = [];
    public IReadOnlyList<ManualRegistryAsset> ManualAssets { get; init; } = [];
}

public sealed record MountedPackageFingerprint(string PackagePath, long Length, DateTime LastWriteTimeUtc)
{
    public static MountedPackageFingerprint Read(string path)
    {
        var file = new FileInfo(path);
        return new(Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc);
    }

    public bool IsCurrent()
    {
        var file = new FileInfo(PackagePath);
        return file.Exists && file.Length == Length && file.LastWriteTimeUtc == LastWriteTimeUtc;
    }
}

public sealed record InstalledAssetOccurrence(
    string InstancedPath,
    string ClassName,
    string PackagePath,
    int ExportUIndex,
    int MountPriority,
    TextureCatalogOrigin Origin = TextureCatalogOrigin.BaseGame)
{
    public static bool IsSupportedClass(string className) => className is
        "Texture2D" or "SkeletalMesh" or "TextureCube" or "Material" or "MaterialInstanceConstant" or
        "MaterialInstanceTimeVarying" or "RvrEffectsMaterialUser" or "RvrEffectsMaterial";
}

public sealed record ManualRegistryAsset(
    string PackagePath,
    int ExportUIndex,
    string InstancedPath,
    string ClassName,
    bool IsMissing = false);

public sealed record AttachmentMeshOccurrence(
    string PackagePath,
    string InstancedPath,
    int ExportUIndex,
    int MountPriority,
    TextureCatalogOrigin Origin,
    int BoneCount = 0);

public sealed record AttachmentMeshCandidate(
    string CanonicalPath,
    AttachmentMeshOccurrence EffectiveOccurrence,
    IReadOnlyList<AttachmentMeshOccurrence> Occurrences)
{
    /// <summary>True only when the effective official donor is from its filename-winning package.</summary>
    [JsonIgnore]
    public bool IsEligibleOfficialDefault { get; init; }
}

/// <summary>A lightweight pointer to a target-game face discovered during the registry scan.</summary>
public sealed record MorphFaceTemplateCandidate(
    string PackagePath,
    int ExportUIndex,
    string FacePath,
    string? BaseHeadPath,
    int MountPriority,
    TextureCatalogOrigin Origin);

/// <summary>Shared, non-exclusive discovery rules used by the one-pass installed-package scanner.</summary>
public static class TextureRegistryDiscovery
{
    private static readonly string[] ExcludedPathFragments =
    [
        "BIOG_AMB_MON_NKD_R", "BIOG_CBT_VAR_NKD_R", "BioApl_Cor_Corpse",
        "BioApl_Veh_HoverCar01", "BIOA_GalaxyMap_T", "BIOA_GLO_00_A_Opening_FlyBy_T",
        "BIOA_GXM10_T", "BIOA_UNC50_T", "BIOG_GTH_HED_PROMorph",
        "BIOG_HMF_HED_PROJack_ALT_R.Visor.CM_ChromeJackVisor",
        "biog_hmm_arm_lwn_r", "BIOG_YAH_HED_PROMorph_R",
        "BIOG_HMF_HED_ANN_R.Textures.Cube_EyesANN",
        "BIOG_HMF_HED_FTL_R.Textures.Cube_EyesFTLCube_Eyes",
        "BIOG_HMF_HED_FTL_R.Textures.HMF_HED_FTL_Iris_NormCube",
        "BIOG_HMF_HED_FTL_R.Textures.HMF_HED_FTL_Lens_NormCube",
        "PROEdi.EDI", "BIOG_Humanoid_MASTER_MTR_R.CubeMap",
        "BIOG_PRN_HED_PRO", "BIOG_TUF_HED_PROMorph_R.NYR_HED",
        "HMM_HED_PROCenturion", "GUI_", "PROGarrus.Visor",
        "KaiLang.HMM_HGR", "biog_hmm_hed_gnr_r.Textures.Cube_Eyes",
        "PROHackett.Textures.HMM_HGR", "PROJack_ALT_R.Visor"
    ];

    private static readonly string[] HatTextureFragments =
        ["HMM_HAT", "HMF_HAT", "HMM_HGR", "HMF_HGR", "HMM_HLT", "HMF_HLT"];

    private static readonly string[] PlayerMaterialPaths =
    [
        "BIOG_Humanoid_MASTER_MTR_R.Skin_HumanScalp_SpecMulitplier_Mask",
        "BIOG_Humanoid_MASTER_MTR_R.Human.Teeth.HED_PRO_Teeth"
    ];

    private static readonly string[] RelevantPathFragments =
    [
        "PROMorph",
        "HMM_HED",
        "HMF_HED",
        "HMN_HED",
        "HumanHED",
        "HMM_HIR",
        "HMF_HIR",
        "HMM_EYE",
        "HMF_EYE",
        "HED_EYE",
        "EYE_",
        "ASA_EYE",
        "SAL_EYE",
        "TUR_EYE",
        "KRO_EYE",
        "BAT_EYE",
        "GBL_Norm_Alpha"
    ];

    public static bool IsRelevantPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return !IsExcludedPath(path) &&
               (IsExplicitPlayerMaterialPath(path) ||
                RelevantPathFragments.Any(fragment =>
                    path.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsExplicitPlayerMaterialPath(string path) =>
        PlayerMaterialPaths.Any(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));

    public static bool IsExcludedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (ExcludedPathFragments.Any(fragment =>
                path.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (path.Contains("HMM_HIR", StringComparison.OrdinalIgnoreCase) &&
            path.Split('.').Last().EndsWith("_CC", StringComparison.OrdinalIgnoreCase))
            return true;

        return (path.Contains("BIOG_HMM_HIR", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("BIOG_HMF_HIR", StringComparison.OrdinalIgnoreCase)) &&
               HatTextureFragments.Any(fragment =>
                   path.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
