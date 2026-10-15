using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Checks whether an attachment material instance inherits from a reviewed
/// canonical base material. Material-instance identity and values remain
/// authored data; only the terminal shader graph must be canonical.
/// </summary>
internal static class SupportedMaterialGraphPolicy
{
    internal static bool IsSupported(
        MEGame game,
        ExportEntry material,
        GamePackageReferenceResolver referenceResolver,
        out string unsupported)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(referenceResolver);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = material;
        while (true)
        {
            var path = PccAssetPathPolicy.FromExport(current);
            var identity = $"{current.Game}|{Path.GetFullPath(current.FileRef.FilePath)}|{current.UIndex}";
            if (!visited.Add(identity))
            {
                unsupported = $"material parent cycle at {current.ClassName} '{path}'";
                return false;
            }

            if (current.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase))
            {
                if (MorphFacePackageReader.DescribeMaterialMaster(game, current) is not null)
                {
                    unsupported = string.Empty;
                    return true;
                }

                unsupported = $"unreviewed base material '{path}'";
                return false;
            }

            string parentPropertyName;
            if (current.IsA("RvrEffectsMaterialUser"))
            {
                parentPropertyName = "m_pBaseMaterial";
            }
            else if (current.IsA("MaterialInstanceConstant") ||
                     current.IsA("MaterialInstanceTimeVarying"))
            {
                parentPropertyName = "Parent";
            }
            else
            {
                unsupported = $"unsupported material graph node {current.ClassName} '{path}'";
                return false;
            }

            var parentReference = current.GetProperty<ObjectProperty>(parentPropertyName)?
                .ResolveToEntry(current.FileRef);
            if (parentReference is null)
            {
                unsupported = $"unresolved {parentPropertyName} for {current.ClassName} '{path}'";
                return false;
            }

            current = referenceResolver.Resolve(parentReference);
            if (current is null)
            {
                unsupported = $"unresolved {parentPropertyName} for {material.ClassName} '{path}'";
                return false;
            }
        }
    }
}
