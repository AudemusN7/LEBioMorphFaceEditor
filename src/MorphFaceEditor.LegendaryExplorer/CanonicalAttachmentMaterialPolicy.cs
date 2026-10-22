using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Reviewed stock attachment slot choices shared by preview, eligibility and PCC import.</summary>
internal static class CanonicalAttachmentMaterialPolicy
{
    internal const string AshleyMeshPath = "BIOG_HMF_HIR_PRO.Ashley.HMF_HIR_PROAshley_MDL";
    internal const string AshleyMaterialPath = "BIOG_HMF_HIR_PRO.Ashley.HMF_HIR_PROAshley_Mat_2a";
    private const string LegacyAshleyMaterialPath = "BIOG_HMF_HIR_PRO.Ashley.HMF_HIR_PROAshley_Mat_1b";

    internal static ExportEntry? ResolveReplacement(
        ExportEntry mesh, IEntry material, PackageCache cache, InstalledGameAssetResolver? installedAssets = null)
    {
        if (mesh.Game != MEGame.LE2 ||
            !mesh.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ||
            !PccAssetPathPolicy.FromExport(mesh).Equals(AshleyMeshPath, StringComparison.OrdinalIgnoreCase) ||
            !material.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
            !PccPackageWorkflow.CanonicalPath(material).Equals(LegacyAshleyMaterialPath, StringComparison.OrdinalIgnoreCase))
            return null;
        var assets = installedAssets ?? new InstalledGameAssetResolver();
        // This is a stock slot choice, not a replacement for authored MIC edits.
        if (assets.Resolve(material, cache) is null ||
            material is ExportEntry authored && MaterialInstanceSourcePolicy.OverridesOfficialInstance(authored, assets, cache))
            return null;
        return assets.Resolve(MEGame.LE2, AshleyMaterialPath, "MaterialInstanceConstant", cache)
            ?? throw new InvalidDataException($"The canonical LE2 Ashley MIC '{AshleyMaterialPath}' has no exact official donor.");
    }

    internal static PreparedMesh? PrepareMesh(
        ExportEntry mesh, PackageCache cache, InstalledGameAssetResolver? installedAssets = null)
    {
        if (mesh.Game != MEGame.LE2 || !mesh.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ||
            !PccAssetPathPolicy.FromExport(mesh).Equals(AshleyMeshPath, StringComparison.OrdinalIgnoreCase)) return null;
        var replacements = mesh.GetBinaryData<SkeletalMesh>().Materials.Select((index, slot) =>
            (Slot: slot, Material: mesh.FileRef.GetEntry(index) is { } material
                ? ResolveReplacement(mesh, material, cache, installedAssets) : null))
            .Where(value => value.Material is not null).ToArray();
        if (replacements.Length == 0) return null;

        // Work on a disposable package snapshot. Installed assets and authored
        // source meshes must retain their original material array and bytes.
        using var stream = mesh.FileRef.SaveToStream(compress: false);
        stream.Position = 0;
        var package = MEPackageHandler.OpenMEPackageFromStream(stream, mesh.FileRef.FilePath);
        try
        {
            var prepared = package.GetUExport(mesh.UIndex);
            var binary = prepared.GetBinaryData<SkeletalMesh>();
            foreach (var replacement in replacements)
            {
                var material = package.Exports.FirstOrDefault(entry =>
                    entry.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) &&
                    PccAssetPathPolicy.FromExport(entry).Equals(AshleyMaterialPath, StringComparison.OrdinalIgnoreCase))
                    ?? PccPackageWorkflow.ImportDependencyGraph(package, replacement.Material!, null, null,
                        targetPathOverride: AshleyMaterialPath, installedAssets: installedAssets, sourceCache: cache);
                binary.Materials[replacement.Slot] = material.UIndex;
            }
            prepared.WriteBinary(binary);
            return new PreparedMesh(package, prepared);
        }
        catch { package.Dispose(); throw; }
    }

    internal sealed record PreparedMesh(IMEPackage Package, ExportEntry Mesh) : IDisposable
    {
        public void Dispose() => Package.Dispose();
    }
}
