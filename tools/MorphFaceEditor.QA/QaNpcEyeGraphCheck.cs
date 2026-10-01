using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

/// <summary>Checks the saved LE2 NPC eye graph independently of material-parameter roundtrips.</summary>
internal static class QaNpcEyeGraphCheck
{
    internal sealed record MicEvidence(
        string Path,
        string MicRole,
        string? ParentPath,
        string? ParentClass,
        string? ParentRole);

    internal sealed record GraphEvidence(
        string Game,
        string Family,
        string BaseHeadPath,
        string? EffectsPackageRole,
        string? UsersPackageRole,
        string? ExpectedEffectsUserRole,
        IReadOnlyList<MicEvidence> EyeMics,
        string? InstalledEffectsDonor,
        string? InstalledBasePath,
        string? InstalledBaseDonor,
        IReadOnlyList<string> Failures)
    {
        public bool Passed => Failures.Count == 0;
    }

    internal static GraphEvidence Inspect(
        IMEPackage package,
        ExportEntry baseHead,
        string family,
        InstalledGameAssetResolver? installedAssets = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(baseHead);
        if (!family.Equals("HMM", StringComparison.OrdinalIgnoreCase) &&
            !family.Equals("HMF", StringComparison.OrdinalIgnoreCase) &&
            !family.Equals("ASA", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The LE2 NPC eye check supports ASA, HMM, and HMF.", nameof(family));

        const string effectsPath = "EffectsMaterials.Users.HMM_EYE_MASTER_MAT_USER";
        const string installedBasePath = "BIOG_Humanoid_MASTER_MTR_R.Eye.HMM_EYE_MASTER_MAT";
        var failures = new List<string>();
        var effectsPackage = package.FindExport("EffectsMaterials", "Package");
        var usersPackage = package.FindExport("EffectsMaterials.Users", "Package");
        var effectsEntry = package.FindEntry(effectsPath, "RvrEffectsMaterialUser");

        if (effectsPackage is null)
            failures.Add("EffectsMaterials is not a Package export.");
        if (usersPackage is null)
            failures.Add("EffectsMaterials.Users is not a Package export.");
        if (effectsEntry is not ImportEntry)
            failures.Add($"{effectsPath} is not a RvrEffectsMaterialUser import.");
        if (package.FindExport(effectsPath, "RvrEffectsMaterialUser") is not null)
            failures.Add($"{effectsPath} was materialized as an export instead of remaining an import.");

        var micEvidence = new List<MicEvidence>();
        SkeletalMesh mesh;
        try
        {
            mesh = baseHead.GetBinaryData<SkeletalMesh>();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failures.Add($"Base head could not be read as a SkeletalMesh: {exception.Message}");
            return new(package.Game.ToString(), family, baseHead.InstancedFullPath,
                Role(effectsPackage), Role(usersPackage), Role(effectsEntry), micEvidence,
                null, installedBasePath, null, failures);
        }

        foreach (var index in mesh.Materials ?? [])
        {
            var material = package.GetEntry(index);
            if (material is null || !material.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
                !IsEyeMaterial(material.InstancedFullPath))
                continue;

            var parentIndex = material is ExportEntry mic
                ? mic.GetProperty<ObjectProperty>("Parent")?.Value ?? 0
                : 0;
            var parent = parentIndex == 0 ? null : package.GetEntry(parentIndex);
            var parentRole = Role(parent);
            micEvidence.Add(new(material.InstancedFullPath, Role(material) ?? material.ClassName, parent?.InstancedFullPath,
                parent?.ClassName, parentRole));

            if (material is not ExportEntry)
                failures.Add($"Eye MIC {material.InstancedFullPath} is not a saved export.");
            if (parent is not ImportEntry ||
                !parent.InstancedFullPath.Equals(effectsPath, StringComparison.OrdinalIgnoreCase) ||
                !parent.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase))
                failures.Add($"Eye MIC {material.InstancedFullPath} does not inherit from imported {effectsPath}; found {parent?.InstancedFullPath ?? "unresolved Parent"}.");
        }

        if (micEvidence.Count == 0)
            failures.Add($"Base head {baseHead.InstancedFullPath} has no exported eye MaterialInstanceConstant in its material slots.");

        string? installedEffectsDonor = null;
        string? resolvedInstalledBasePath = null;
        string? installedBaseDonor = null;
        if (installedAssets is not null)
        {
            using var cache = new PackageCache();
            var installedBase = installedAssets.Resolve(package.Game, installedBasePath, "Material", cache);
            installedBaseDonor = installedBase?.FileRef.FilePath;
            var installedEffects = installedAssets.Resolve(package.Game, effectsPath, "RvrEffectsMaterialUser", cache);
            if (installedEffects is not null)
            {
                installedEffectsDonor = installedEffects.FileRef.FilePath;
                var actualBase = installedEffects.GetProperty<ObjectProperty>("m_pBaseMaterial")?
                    .ResolveToEntry(installedEffects.FileRef);
                resolvedInstalledBasePath = actualBase?.InstancedFullPath;
                if (actualBase is not null && !actualBase.InstancedFullPath.Equals(installedBasePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Installed {effectsPath} resolves to base {actualBase.InstancedFullPath}, expected {installedBasePath}.");
                }
            }
        }

        return new(package.Game.ToString(), family, baseHead.InstancedFullPath,
            Role(effectsPackage), Role(usersPackage), Role(effectsEntry), micEvidence,
            installedEffectsDonor, resolvedInstalledBasePath, installedBaseDonor, failures);
    }

    internal static void RequireValid(GraphEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.Passed)
            throw new InvalidDataException("LE2 NPC eye material graph is invalid: " + string.Join("; ", evidence.Failures));
    }

    private static bool IsEyeMaterial(string path) =>
        path.Contains("EYE", StringComparison.OrdinalIgnoreCase);

    private static string? Role(IEntry? entry) => entry switch
    {
        ExportEntry export when export.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase) => "Package export",
        ExportEntry => "Export",
        ImportEntry import when import.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase) => "Package import",
        ImportEntry => "Import",
        _ => null
    };
}
