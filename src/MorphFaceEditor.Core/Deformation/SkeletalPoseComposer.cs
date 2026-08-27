using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public sealed record SkeletalPose(
    IReadOnlyList<Matrix4x4> SkinningMatrices,
    IReadOnlyList<Vector3> LocalTranslations);

public static class SkeletalPoseComposer
{
    public static SkeletalPose Compose(
        IReadOnlyList<ReferenceBone> referenceSkeleton,
        IReadOnlyList<BoneTranslation> finalSkeleton)
    {
        ArgumentNullException.ThrowIfNull(referenceSkeleton);
        ArgumentNullException.ThrowIfNull(finalSkeleton);
        var translations = finalSkeleton
            .GroupBy(bone => bone.BoneName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single().Translation, StringComparer.OrdinalIgnoreCase);
        var bindGlobal = new Matrix4x4[referenceSkeleton.Count];
        var finalGlobal = new Matrix4x4[referenceSkeleton.Count];
        var localTranslations = new Vector3[referenceSkeleton.Count];
        for (var index = 0; index < referenceSkeleton.Count; index++)
        {
            var bone = referenceSkeleton[index];
            var orientation = bone.Orientation.LengthSquared() < 1e-12f
                ? Quaternion.Identity
                : Quaternion.Normalize(bone.Orientation);
            var finalTranslation = translations.GetValueOrDefault(bone.Name, bone.Position);
            localTranslations[index] = finalTranslation;
            var bindLocal = Matrix4x4.CreateFromQuaternion(orientation) * Matrix4x4.CreateTranslation(bone.Position);
            var finalLocal = Matrix4x4.CreateFromQuaternion(orientation) * Matrix4x4.CreateTranslation(finalTranslation);
            if (index == 0 || bone.ParentIndex is < 0 || bone.ParentIndex >= index)
            {
                bindGlobal[index] = bindLocal;
                finalGlobal[index] = finalLocal;
            }
            else
            {
                bindGlobal[index] = bindLocal * bindGlobal[bone.ParentIndex];
                finalGlobal[index] = finalLocal * finalGlobal[bone.ParentIndex];
            }
        }

        var palette = new Matrix4x4[referenceSkeleton.Count];
        for (var index = 0; index < palette.Length; index++)
        {
            if (!Matrix4x4.Invert(bindGlobal[index], out var inverseBind))
            {
                throw new InvalidDataException($"Reference transform for bone {index} '{referenceSkeleton[index].Name}' is not invertible.");
            }
            palette[index] = inverseBind * finalGlobal[index];
        }
        return new SkeletalPose(palette, localTranslations);
    }
}
