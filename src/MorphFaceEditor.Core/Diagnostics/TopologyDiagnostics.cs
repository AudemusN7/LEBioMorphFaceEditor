using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record TopologyIssue(DiagnosticSeverity Severity, string Code, string Message);

public sealed record TopologyDiagnosticReport(
    SkeletalMeshTopology Topology,
    IReadOnlyList<TopologyIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != DiagnosticSeverity.Error);
}

/// <summary>Validates mesh, baked-face and sparse-target structural compatibility before live editing.</summary>
public static class TopologyDiagnostics
{
    public static TopologyDiagnosticReport Analyze(
        SkeletalMeshAsset mesh,
        MorphFaceDocument? face = null,
        IEnumerable<MorphTargetAsset>? targets = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var issues = new List<TopologyIssue>();
        var topology = mesh.Topology;

        if (topology.VertexCount == 0)
        {
            issues.Add(new TopologyIssue(DiagnosticSeverity.Error, "MESH_EMPTY", "LOD 0 contains no vertices."));
        }

        if (mesh.Positions.Length != topology.VertexCount)
        {
            issues.Add(new TopologyIssue(
                DiagnosticSeverity.Error,
                "POSITION_COUNT",
                $"Position count {mesh.Positions.Length} differs from topology vertex count {topology.VertexCount}."));
        }

        if (mesh.Normals.Length != topology.VertexCount)
        {
            issues.Add(new TopologyIssue(
                DiagnosticSeverity.Error,
                "NORMAL_COUNT",
                $"Normal count {mesh.Normals.Length} differs from topology vertex count {topology.VertexCount}."));
        }

        if (mesh.RenderData is { } renderData)
        {
            ValidateVertexAttributeCount(issues, "TANGENT_COUNT", "Tangent", renderData.Tangents.Count, topology.VertexCount);
            ValidateVertexAttributeCount(issues, "UV_COUNT", "UV", renderData.TextureCoordinates.Count, topology.VertexCount);
            ValidateVertexAttributeCount(issues, "BONE_INDEX_COUNT", "Bone-index", renderData.BoneIndices.Count, topology.VertexCount);
            ValidateVertexAttributeCount(issues, "BONE_WEIGHT_COUNT", "Bone-weight", renderData.BoneWeights.Count, topology.VertexCount);

            if (renderData.Indices.Count != topology.IndexCount)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "INDEX_COUNT",
                    $"Render index count {renderData.Indices.Count} differs from topology index count {topology.IndexCount}."));
            }

            foreach (var invalidIndex in renderData.Indices
                         .Where(index => index < 0 || index >= topology.VertexCount)
                         .Distinct()
                         .Take(10))
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "INDEX_VERTEX_RANGE",
                    $"Index buffer references vertex {invalidIndex}, outside the {topology.VertexCount}-vertex buffer."));
            }

            if (renderData.MaterialSlots.Count != topology.MaterialCount)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "MATERIAL_SLOT_COUNT",
                    $"Render material count {renderData.MaterialSlots.Count} differs from topology material count {topology.MaterialCount}."));
            }

            ValidateBoneInfluences(issues, renderData, topology);
        }

        foreach (var section in topology.Sections)
        {
            var lastIndexExclusive = (long)section.BaseIndex + (long)section.TriangleCount * 3;
            if (section.BaseIndex < 0 || section.TriangleCount < 0 || lastIndexExclusive > topology.IndexCount)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "SECTION_INDEX_RANGE",
                    $"Section at base index {section.BaseIndex} spans beyond the {topology.IndexCount}-index buffer."));
            }

            if (section.MaterialIndex < 0 || section.MaterialIndex >= topology.MaterialCount)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "SECTION_MATERIAL_RANGE",
                    $"Section material {section.MaterialIndex} is outside the {topology.MaterialCount} material slots."));
            }

            if (section.ChunkIndex < 0 || section.ChunkIndex >= topology.Chunks.Count)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "SECTION_CHUNK_RANGE",
                    $"Section chunk {section.ChunkIndex} is outside the {topology.Chunks.Count} chunks."));
            }
        }

        foreach (var (chunk, chunkIndex) in topology.Chunks.Select((value, index) => (value, index)))
        {
            foreach (var boneIndex in chunk.BoneMap)
            {
                if (boneIndex < 0 || boneIndex >= topology.ReferenceSkeleton.Count)
                {
                    issues.Add(new TopologyIssue(
                        DiagnosticSeverity.Error,
                        "CHUNK_BONE_RANGE",
                        $"Chunk {chunkIndex} maps bone {boneIndex}, outside the {topology.ReferenceSkeleton.Count}-bone skeleton."));
                }
            }
        }

        foreach (var (bone, boneIndex) in topology.ReferenceSkeleton.Select((value, index) => (value, index)))
        {
            if (boneIndex == 0 && bone.ParentIndex is not (0 or -1))
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Warning,
                    "ROOT_PARENT",
                    $"Root bone '{bone.Name}' has parent index {bone.ParentIndex}."));
            }
            else if (boneIndex > 0 && (bone.ParentIndex < 0 || bone.ParentIndex >= boneIndex))
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "BONE_PARENT_RANGE",
                    $"Bone {boneIndex} '{bone.Name}' has invalid parent index {bone.ParentIndex}."));
            }
        }

        if (face is not null)
        {
            if (face.BakedLods.Count == 0)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Warning,
                    "FACE_NO_LODS",
                    "BioMorphFace contains no baked LODs; preview will use the base mesh geometry."));
            }
            else if (face.BakedLods[0].Length != topology.VertexCount)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "FACE_VERTEX_COUNT",
                    $"Baked face LOD 0 has {face.BakedLods[0].Length} vertices; base mesh LOD 0 has {topology.VertexCount}."));
            }

            var duplicateFeatures = face.MorphFeatures
                .GroupBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key);
            foreach (var featureName in duplicateFeatures)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "DUPLICATE_FEATURE",
                    $"Morph feature '{featureName}' occurs more than once."));
            }

            var duplicateBones = face.FinalSkeleton
                .GroupBy(bone => bone.BoneName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key);
            foreach (var boneName in duplicateBones)
            {
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "DUPLICATE_FINAL_BONE",
                    $"Final-skeleton bone '{boneName}' occurs more than once."));
            }
        }

        if (targets is not null)
        {
            foreach (var target in targets)
            {
                if (target.Lods.Count == 0)
                {
                    issues.Add(new TopologyIssue(
                        DiagnosticSeverity.Error,
                        "TARGET_NO_LODS",
                        $"Morph target '{target.Source.InstancedPath}' contains no LODs."));
                    continue;
                }

                var lod = target.Lods[0];
                if (lod.BaseMeshVertexCount != topology.VertexCount)
                {
                    issues.Add(new TopologyIssue(
                        DiagnosticSeverity.Error,
                        "TARGET_BASE_COUNT",
                        $"Morph target '{target.Source.InstancedPath}' expects {lod.BaseMeshVertexCount} vertices; base mesh has {topology.VertexCount}."));
                }

                foreach (var invalidIndex in lod.Vertices
                             .Where(vertex => vertex.SourceIndex < 0 || vertex.SourceIndex >= topology.VertexCount)
                             .Select(vertex => vertex.SourceIndex)
                             .Distinct()
                             .Take(10))
                {
                    issues.Add(new TopologyIssue(
                        DiagnosticSeverity.Error,
                        "TARGET_SOURCE_RANGE",
                        $"Morph target '{target.Source.InstancedPath}' references out-of-range vertex {invalidIndex}."));
                }
            }
        }

        if (issues.Count == 0)
        {
            issues.Add(new TopologyIssue(
                DiagnosticSeverity.Information,
                "TOPOLOGY_OK",
                $"LOD 0 topology is internally consistent ({topology.VertexCount} vertices, signature {topology.SignatureSha256})."));
        }

        return new TopologyDiagnosticReport(topology, issues);
    }

    private static void ValidateBoneInfluences(
        ICollection<TopologyIssue> issues,
        SkeletalMeshRenderData renderData,
        SkeletalMeshTopology topology)
    {
        for (var vertexIndex = 0; vertexIndex < Math.Min(renderData.BoneIndices.Count, renderData.BoneWeights.Count); vertexIndex++)
        {
            var indices = renderData.BoneIndices[vertexIndex];
            var weights = renderData.BoneWeights[vertexIndex];
            var values = new[] { weights.X, weights.Y, weights.Z, weights.W };
            for (var influence = 0; influence < values.Length; influence++)
            {
                if (values[influence] <= 0)
                {
                    continue;
                }
                var boneIndex = indices[influence];
                if (boneIndex >= 0 && boneIndex < topology.ReferenceSkeleton.Count)
                {
                    continue;
                }
                issues.Add(new TopologyIssue(
                    DiagnosticSeverity.Error,
                    "VERTEX_BONE_RANGE",
                    $"Vertex {vertexIndex} influence {influence} references bone {boneIndex}, outside the {topology.ReferenceSkeleton.Count}-bone skeleton."));
                return;
            }
        }
    }

    private static void ValidateVertexAttributeCount(
        ICollection<TopologyIssue> issues,
        string code,
        string label,
        int actual,
        int expected)
    {
        if (actual == expected)
        {
            return;
        }

        issues.Add(new TopologyIssue(
            DiagnosticSeverity.Error,
            code,
            $"{label} count {actual} differs from topology vertex count {expected}."));
    }
}
