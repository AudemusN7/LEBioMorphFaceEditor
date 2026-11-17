using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

/// <summary>Transfers an observed posed surface between compatible canonical rigs.</summary>
public static class PlayerBakedShapeTransfer
{
    public static Vector3[] Transfer(SkeletalMeshAsset source, MorphFaceDocument sourceDocument,
        SkeletalMeshAsset target, MorphFaceDocument targetDocument)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceDocument);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetDocument);
        var sourceLod = ValidateMesh(source, sourceDocument, false);
        var targetLod = ValidateMesh(target, targetDocument, true);
        ValidateCompatibleRigs(sourceLod.Topology.ReferenceSkeleton, targetLod.Topology.ReferenceSkeleton);

        var sourcePose = SkeletalPoseComposer.Compose(sourceLod.Topology.ReferenceSkeleton, sourceDocument.FinalSkeleton);
        var targetPose = SkeletalPoseComposer.Compose(targetLod.Topology.ReferenceSkeleton, targetDocument.FinalSkeleton);
        var sourcePosed = Skin(sourceDocument.BakedLods[0], sourceLod, sourcePose);
        var offsets = sourcePosed.Select((position, index) => position - sourceLod.Positions[index]).ToArray();
        if (offsets.Any(value => !Finite(value))) throw new InvalidDataException("Source posed displacement is invalid.");
        var mapped = PlayerSurfaceCorrespondence.Map(source, target, offsets);
        if (mapped.Length != targetLod.Positions.Length || mapped.Any(value => !Finite(value)))
            throw new InvalidDataException("Mapped surface displacement is invalid.");

        // Both poses change local translations only. The palette's linear part is
        // identity, so its per-vertex displacement can be removed exactly once.
        var destinationDraft = targetDocument.BakedLods[0];
        var posedDraft = Skin(destinationDraft, targetLod, targetPose);
        var desired = targetLod.Positions.Select((position, index) => position + mapped[index]).ToArray();
        var result = desired.Select((position, index) => position - (posedDraft[index] - destinationDraft[index])).ToArray();
        if (result.Any(value => !Finite(value))) throw new InvalidDataException("Transferred geometry is invalid.");
        var verified = Skin(result, targetLod, targetPose);
        for (var i = 0; i < result.Length; i++)
            if (Vector3.Distance(verified[i], desired[i]) > 0.0001f)
                throw new InvalidDataException("Transferred geometry could not reproduce the requested pose.");
        return result;
    }

    private static SkeletalMeshLod ValidateMesh(SkeletalMeshAsset mesh, MorphFaceDocument document, bool requireCompleteBones)
    {
        var lod = mesh.FindLod(0) ?? throw new InvalidDataException("Canonical mesh has no LOD0.");
        var render = lod.RenderData ?? throw new InvalidDataException("Canonical mesh has no skinning data.");
        var count = lod.Positions.Length;
        if (count == 0 || lod.Topology.LodIndex != 0 || lod.Topology.VertexCount != count ||
            lod.Normals.Length != count || render.BoneIndices.Count != count || render.BoneWeights.Count != count ||
            document.BakedLods.Count == 0 || document.BakedLods[0] is null || document.BakedLods[0].Length != count ||
            lod.Positions.Any(v => !Finite(v)) || lod.Normals.Any(v => !Finite(v)) || document.BakedLods[0].Any(v => !Finite(v)) ||
            render.Indices.Count == 0 || render.Indices.Count % 3 != 0 || render.Indices.Any(i => (uint)i >= (uint)count))
            throw new InvalidDataException("Canonical mesh or authored LOD0 is invalid.");
        var bones = lod.Topology.ReferenceSkeleton;
        if (bones.Count == 0 || bones.Any(b => string.IsNullOrWhiteSpace(b.Name) || !Finite(b.Position) || !Finite(b.Orientation) || b.Orientation.LengthSquared() < 1e-12f) ||
            bones.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bones.Count)
            throw new InvalidDataException("Canonical rig is invalid.");
        for (var i = 1; i < bones.Count; i++)
            if (bones[i].ParentIndex < 0 || bones[i].ParentIndex >= i) throw new InvalidDataException("Canonical rig hierarchy is invalid.");
        var names = bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.FinalSkeleton.Any(b => string.IsNullOrWhiteSpace(b.BoneName) || !names.Contains(b.BoneName) || !Finite(b.Translation)) ||
            document.FinalSkeleton.Select(b => b.BoneName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.FinalSkeleton.Count ||
            (requireCompleteBones && document.FinalSkeleton.Count != bones.Count))
            throw new InvalidDataException("Authored final skeleton is invalid.");
        for (var i = 0; i < count; i++)
        {
            var weights = render.BoneWeights[i];
            var sum = weights.X + weights.Y + weights.Z + weights.W;
            if (!(weights.X >= 0 && weights.Y >= 0 && weights.Z >= 0 && weights.W >= 0) ||
                !(sum > 0) || !float.IsFinite(sum))
                throw new InvalidDataException("Canonical skin weights are invalid.");
            var indices = render.BoneIndices[i];
            if ((weights.X > 0 && (uint)indices.X >= (uint)bones.Count) ||
                (weights.Y > 0 && (uint)indices.Y >= (uint)bones.Count) ||
                (weights.Z > 0 && (uint)indices.Z >= (uint)bones.Count) ||
                (weights.W > 0 && (uint)indices.W >= (uint)bones.Count))
                throw new InvalidDataException("Canonical skin influence is invalid.");
        }
        return lod;
    }

    private static void ValidateCompatibleRigs(IReadOnlyList<ReferenceBone> source, IReadOnlyList<ReferenceBone> target)
    {
        if (source.Count != target.Count) throw new InvalidDataException("Canonical rigs have different bones.");
        var targetIndices = target.Select((bone, index) => (bone.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
        {
            var bone = source[i];
            if (!targetIndices.TryGetValue(bone.Name, out var targetIndex))
                throw new InvalidDataException("Canonical rigs have different bones.");
            var other = target[targetIndex];
            var sourceParent = i == 0 ? null : source[bone.ParentIndex].Name;
            var targetParent = targetIndex == 0 ? null : target[other.ParentIndex].Name;
            if (!string.Equals(sourceParent, targetParent, StringComparison.OrdinalIgnoreCase) ||
                !(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(bone.Orientation), Quaternion.Normalize(other.Orientation))) >= 0.99999f))
                throw new InvalidDataException("Canonical rig coordinate frames differ.");
        }
    }

    private static Vector3[] Skin(Vector3[] positions, SkeletalMeshLod lod, SkeletalPose pose) =>
        CpuSkinningEvaluator.Skin(new DeformationResult(positions, lod.Normals, 0, []), lod.RenderData!, pose.SkinningMatrices).Positions;

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}
