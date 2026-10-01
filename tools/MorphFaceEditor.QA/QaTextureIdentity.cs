using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

internal static class QaTextureIdentity
{
    // Seek-free donors omit their linker name. Compare the same full identity
    // the PCC writer materialises, while retaining package/group distinctions.
    internal static string CanonicalPath(AssetIdentity identity) =>
        PccAssetPathPolicy.FromDonorOccurrence(identity.InstancedPath, identity.PackagePath);

    internal static bool SameTexture(AssetIdentity expected, AssetIdentity actual) =>
        expected.ClassName.Equals(actual.ClassName, StringComparison.OrdinalIgnoreCase) &&
        CanonicalPath(expected).Equals(CanonicalPath(actual), StringComparison.OrdinalIgnoreCase);
}
