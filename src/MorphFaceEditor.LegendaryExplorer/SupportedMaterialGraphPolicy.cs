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
                if (IsCanonicalHeadMaterial(game, current.ClassName, path))
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
                // Effects-material wrappers are supported only when the wrapper
                // itself is canonical; arbitrary user-authored shader graphs
                // cannot become supported by pointing at a stock base.
                if (!IsCanonicalHeadMaterial(game, current.ClassName, path))
                {
                    unsupported = $"unreviewed effects material '{path}'";
                    return false;
                }
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

    private static bool IsCanonicalHeadMaterial(MEGame game, string className, string path)
    {
        // LE1's officially backported Shepard hair graph is absent from the
        // GlobalMorphs projection. Its shader parents are supported independently
        // of whichever stock or custom MIC points to them.
        if (game == MEGame.LE1 &&
            ($"{className}|{path}".Equals(
                 "RvrEffectsMaterialUser|EffectsMaterials.Users.HMN_HED_PRO_MASTER_HAIR_ME3_MAT_USER",
                 StringComparison.OrdinalIgnoreCase) ||
             $"{className}|{path}".Equals(
                 "Material|BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_ME3_MASTER_Hair_MAT",
                 StringComparison.OrdinalIgnoreCase)))
            return true;
        if (MaterialDependencyOracle.Instance.GetKind(game, className, path) ==
            MaterialOracleEntryKind.Unknown)
        {
            return false;
        }

        var leaf = path.Split('.').Last();
        if (leaf.StartsWith("CombinedEffect_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The oracle is a dependency projection and includes body and VFX
        // materials. Ordinary roots must classify as an assignable head family
        // or recognized headwear; the Anderson exception covers the shared body-
        // derived graph used by stock LE3 Beret and Officer materials.
        return HeadMaterialClassifier.IsAssignableHeadFamily(HeadMaterialClassifier.Classify(path)) ||
               IsRecognizedHeadwearPath(path) ||
               IsReviewedSharedHatRoot(game, className, path);
    }

    private static bool IsRecognizedHeadwearPath(string path) =>
        path.Contains("hat", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("helmet", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("visor", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("beanie", StringComparison.OrdinalIgnoreCase);

    private static bool IsReviewedSharedHatRoot(MEGame game, string className, string path)
    {
        var identity = $"{className}|{path}";
        return game == MEGame.LE3 && IsOneOf(identity,
            "RvrEffectsMaterialUser|BIOG_HMM_ARM_AND_R.Master.Andersonb_MASTER_MAT_USER",
            "Material|BIOG_HMM_ARM_AND_R.Master.Andersonb_MASTER_MAT");
    }

    private static bool IsOneOf(string identity, params string[] reviewedIdentities) =>
        reviewedIdentities.Contains(identity, StringComparer.OrdinalIgnoreCase);
}
