using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer.Interchange;

/// <summary>Rebinds a disposable export mesh to the face's complete authored shape.</summary>
internal static class MorphMeshExportPose
{
    internal static Vector3[] Apply(SkeletalMesh mesh, IReadOnlyList<Vector3[]> bakedLods,
        IReadOnlyList<BoneTranslation> finalSkeleton)
    {
        var reference = mesh.RefSkeleton.Select(bone => new ReferenceBone(
            bone.Name.Instanced, bone.ParentIndex, bone.Position, bone.Orientation)).ToArray();
        var pose = SkeletalPoseComposer.Compose(reference, finalSkeleton);
        var lod0 = Array.Empty<Vector3>();
        for (var lodIndex = 0; lodIndex < mesh.LODModels.Length; lodIndex++)
        {
            var lod = mesh.LODModels[lodIndex];
            var gpu = lod.VertexBufferGPUSkin?.VertexData;
            var count = gpu?.Length ?? lod.ME1VertexBufferGPUSkin.Length;
            var baked = lodIndex < bakedLods.Count && bakedLods[lodIndex].Length == count
                ? bakedLods[lodIndex]
                : null;
            var positions = new Vector3[count];
            for (var index = 0; index < count; index++)
            {
                var original = gpu is not null ? gpu[index].Position : lod.ME1VertexBufferGPUSkin[index].Position;
                var bones = gpu is not null ? gpu[index].InfluenceBones : lod.ME1VertexBufferGPUSkin[index].InfluenceBones;
                var weights = gpu is not null ? gpu[index].InfluenceWeights : lod.ME1VertexBufferGPUSkin[index].InfluenceWeights;
                var indices = MorphFacePackageReader.ResolveBoneIndices(index, bones, lod.Chunks);
                var offset = Vector3.Zero;
                var total = 0;
                for (var influence = 0; influence < 4 && pose.SkinningMatrices.Count > 0; influence++)
                {
                    var weight = weights[influence];
                    if (weight == 0) continue;
                    offset += pose.SkinningMatrices[indices[influence]].Translation * weight;
                    total += weight;
                }
                // FinalSkeleton changes translations only: the linear part of each
                // skin matrix is identity, so authored normals/tangents stay intact.
                if (pose.SkinningMatrices.Count > 0)
                    offset = total > 0 ? offset / total : pose.SkinningMatrices[indices.X].Translation;
                positions[index] = (baked?[index] ?? original) + offset;
                if (gpu is not null)
                {
                    var vertex = gpu[index];
                    vertex.Position = positions[index];
                    gpu[index] = vertex;
                }
                else
                    lod.ME1VertexBufferGPUSkin[index].Position = positions[index];
            }
            if (lodIndex == 0) lod0 = positions;
        }

        // These positions and the final rig now describe one new bind pose.
        // Importers must not apply the original reference-to-final offset again.
        for (var index = 0; index < mesh.RefSkeleton.Length; index++)
            mesh.RefSkeleton[index].Position = pose.LocalTranslations[index];
        return lod0;
    }
}
