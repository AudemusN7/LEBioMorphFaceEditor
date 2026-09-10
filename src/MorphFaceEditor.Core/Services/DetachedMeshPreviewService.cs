using System.Numerics;
using System.Security.Cryptography;
using MorphFaceEditor.Core.Diagnostics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Services;

public sealed record DetachedMeshRigValidation(bool IsValid, IReadOnlyList<string> Issues)
{
    public string? BlockReason => Issues.Count == 0 ? null : string.Join(" ", Issues);
}

public sealed record DetachedMeshEditingCapabilities(
    MorphFaceGeometryMode GeometryMode,
    bool CanEditMorphFeatures,
    bool CanEditBones,
    DetachedMeshRigValidation Rig);

public enum DetachedMeshUpAxis
{
    Auto,
    ZUp,
    YUp
}

/// <summary>
/// Package-independent state for an imported mesh. The decoded source is kept
/// intact for future interchange export while Mesh and Document expose its
/// authored LOD0 through the editor's existing renderer and fixed-bake session.
/// </summary>
public sealed record DetachedMeshPreview(
    ImportedMeshAsset Source,
    DetachedMeshUpAxis UpAxis,
    DetachedMeshUpAxis EffectiveUpAxis,
    SkeletalMeshAsset Mesh,
    MorphFaceDocument Document,
    TopologyDiagnosticReport TopologyDiagnostics,
    DetachedMeshEditingCapabilities Editing,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Converts decoded PSK/glTF data into the editor's detached domain models.
/// It does not infer a player profile, resolve package materials, or alter the
/// source positions/indices. Bone editing is exposed only after a structural
/// proof of the complete imported rig.
/// </summary>
public sealed class DetachedMeshPreviewService
{
    private const float MinimumWeight = 1e-6f;
    private const float MinimumQuaternionLengthSquared = 1e-12f;

    public DetachedMeshPreview Create(ImportedMeshAsset imported, DetachedMeshUpAxis upAxis = DetachedMeshUpAxis.Auto)
    {
        ArgumentNullException.ThrowIfNull(imported);
        ValidateGeometry(imported);

        var source = CloneAsset(imported);
        var effectiveUpAxis = ResolveUpAxis(source.SourcePath, upAxis);
        var previewSource = effectiveUpAxis == DetachedMeshUpAxis.YUp ? RotateYUpToZUp(source) : CloneAsset(source);
        var rig = VerifyRig(previewSource);
        var normals = previewSource.Normals?.ToArray() ?? GenerateNormals(previewSource.Positions, previewSource.Indices);
        var tangents = previewSource.Tangents?.ToArray() ?? normals.Select(CreateTangent).ToArray();
        var uvs = previewSource.TextureCoordinates?.ToArray() ?? new Vector2[previewSource.Positions.Length];
        var boneIndices = rig.IsValid ? previewSource.BoneIndices!.ToArray() : new BoneIndex4[previewSource.Positions.Length];
        var boneWeights = rig.IsValid ? previewSource.BoneWeights!.ToArray() : new Vector4[previewSource.Positions.Length];
        ReferenceBone[] skeleton = rig.IsValid
            ? previewSource.Bones.Select(bone => new ReferenceBone(
                bone.Name, bone.ParentIndex, bone.Position, Quaternion.Normalize(bone.Orientation))).ToArray()
            : [];
        ImportedMeshSection[] sections = previewSource.Sections.Count == 0
            ? [new ImportedMeshSection(0, "Material 0", 0, previewSource.Indices.Length)]
            : previewSource.Sections.ToArray();
        var materialCount = Math.Max(1, sections.Max(section => section.MaterialIndex) + 1);
        var topologySections = sections.Select(section => new MeshSectionTopology(
            section.MaterialIndex, 0, section.IndexStart, section.IndexCount / 3)).ToArray();
        var chunks = new[]
        {
            new MeshChunkTopology(
                0,
                rig.IsValid ? 0 : previewSource.Positions.Length,
                rig.IsValid ? previewSource.Positions.Length : 0,
                rig.IsValid ? 4 : 0,
                rig.IsValid ? Enumerable.Range(0, skeleton.Length).ToArray() : [])
        };
        var topology = new SkeletalMeshTopology(
            0,
            previewSource.Positions.Length,
            previewSource.Indices.Length,
            materialCount,
            topologySections,
            chunks,
            skeleton,
            rig.IsValid ? Enumerable.Range(0, skeleton.Length).ToArray() : [],
            rig.IsValid ? Enumerable.Range(0, skeleton.Length).ToArray() : [],
            CalculateSignature(previewSource.Positions, previewSource.Indices));
        var renderData = new SkeletalMeshRenderData(
            tangents,
            uvs,
            boneIndices,
            boneWeights,
            previewSource.Indices.ToArray(),
            Enumerable.Repeat<AssetIdentity?>(null, materialCount).ToArray());
        var name = Path.GetFileNameWithoutExtension(source.SourcePath);
        var identity = new AssetIdentity(source.SourcePath, $"DetachedMesh.{name}", 0, "SkeletalMesh");
        var mesh = new SkeletalMeshAsset(
            identity,
            previewSource.Positions.ToArray(),
            normals,
            topology,
            renderData,
            [previewSource.Positions.ToArray()],
            [new SkeletalMeshLod(0, previewSource.Positions.ToArray(), normals, topology, renderData)]);
        var signature = topology.SignatureSha256;
        var document = new MorphFaceDocument(
            new AssetIdentity(source.SourcePath, $"DetachedMesh.{name}", 0, "ImportedMesh"),
            new PackageFingerprint(previewSource.Positions.Length, DateTime.UnixEpoch, signature),
            identity,
            null,
            [],
            skeleton.Select(bone => new BoneTranslation(bone.Name, bone.Position)).ToArray(),
            MorphFaceMaterialOverrides.Empty,
            [previewSource.Positions.ToArray()],
            []);
        var diagnostics = TopologyDiagnostics.Analyze(mesh, document);
        if (!diagnostics.IsValid)
        {
            throw new InvalidDataException(
                "Detached mesh failed topology validation: " +
                string.Join("; ", diagnostics.Issues.Where(issue => issue.Severity == DiagnosticSeverity.Error)
                    .Select(issue => issue.Message)));
        }

        var warnings = new List<string>();
        if (source.Normals is null) warnings.Add("The source had no complete normals; preview normals were generated.");
        if (source.Tangents is null) warnings.Add("The source had no complete tangents; preview tangents were generated.");
        if (source.TextureCoordinates is null) warnings.Add("The source had no complete UV0; zero UVs are used for preview.");
        if (source.Bones.Count > 0 && !rig.IsValid)
            warnings.Add("The imported rig was rejected; the mesh remains previewable but bone controls are disabled. " + rig.BlockReason);

        return new DetachedMeshPreview(
            source,
            upAxis,
            effectiveUpAxis,
            mesh,
            document,
            diagnostics,
            new DetachedMeshEditingCapabilities(MorphFaceGeometryMode.FixedBake, false, rig.IsValid, rig),
            warnings);
    }

    public DetachedMeshPreview Build(ImportedMeshAsset imported) => Create(imported);

    private static DetachedMeshUpAxis ResolveUpAxis(string sourcePath, DetachedMeshUpAxis requested) =>
        requested != DetachedMeshUpAxis.Auto
            ? requested
            : Path.GetExtension(sourcePath).Equals(".gltf", StringComparison.OrdinalIgnoreCase) ||
              Path.GetExtension(sourcePath).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                ? DetachedMeshUpAxis.YUp
                : DetachedMeshUpAxis.ZUp;

    private static ImportedMeshAsset RotateYUpToZUp(ImportedMeshAsset source)
    {
        var basis = Matrix4x4.CreateRotationX(MathF.PI / 2f);
        Matrix4x4.Invert(basis, out var inverseBasis);
        return source with
        {
            Positions = source.Positions.Select(value => Vector3.Transform(value, basis)).ToArray(),
            Normals = source.Normals?.Select(value => Vector3.Normalize(Vector3.TransformNormal(value, basis))).ToArray(),
            Tangents = source.Tangents?.Select(value =>
            {
                var xyz = Vector3.TransformNormal(new Vector3(value.X, value.Y, value.Z), basis);
                return new Vector4(Vector3.Normalize(xyz), value.W);
            }).ToArray(),
            Bones = source.Bones.Select(bone =>
            {
                var orientation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(bone.Orientation));
                var rotated = inverseBasis * orientation * basis;
                return bone with
                {
                    Position = Vector3.Transform(bone.Position, basis),
                    Orientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotated))
                };
            }).ToArray()
        };
    }

    private static void ValidateGeometry(ImportedMeshAsset mesh)
    {
        if (mesh.Positions.Length == 0)
            throw new InvalidDataException("Detached mesh has no vertices.");
        if (mesh.Indices.Length == 0 || mesh.Indices.Length % 3 != 0)
            throw new InvalidDataException("Detached mesh has no complete triangle index buffer.");
        if (mesh.Positions.Any(position => !IsFinite(position)))
            throw new InvalidDataException("Detached mesh contains a non-finite position.");
        if (mesh.Indices.Any(index => index < 0 || index >= mesh.Positions.Length))
            throw new InvalidDataException("Detached mesh index buffer references a vertex outside the vertex buffer.");
        ValidateOptionalStream(mesh.Normals, mesh.Positions.Length, "normal", IsFinite);
        ValidateOptionalStream(mesh.Tangents, mesh.Positions.Length, "tangent", IsFinite);
        ValidateOptionalStream(mesh.TextureCoordinates, mesh.Positions.Length, "UV", IsFinite);
        if (mesh.SourceVertexIndices.Length != mesh.Positions.Length)
            throw new InvalidDataException("Detached mesh source-vertex mapping does not match positions.");
        foreach (var section in mesh.Sections)
        {
            if (section.IndexStart < 0 || section.IndexCount < 0 || section.IndexCount % 3 != 0 ||
                (long)section.IndexStart + section.IndexCount > mesh.Indices.Length)
                throw new InvalidDataException($"Detached mesh section '{section.MaterialName}' has an invalid index range.");
            if (section.MaterialIndex < 0)
                throw new InvalidDataException("Detached mesh section has a negative material index.");
        }
    }

    private static DetachedMeshRigValidation VerifyRig(ImportedMeshAsset mesh)
    {
        var issues = new List<string>();
        var invalid = false;
        void AddIssue(string issue)
        {
            invalid = true;
            if (issues.Count < 20 && !issues.Contains(issue, StringComparer.Ordinal))
            {
                issues.Add(issue);
            }
        }
        if (mesh.Bones.Count == 0)
            return new DetachedMeshRigValidation(false, issues);
        if (mesh.BoneIndices is null || mesh.BoneWeights is null ||
            mesh.BoneIndices.Length != mesh.Positions.Length || mesh.BoneWeights.Length != mesh.Positions.Length)
            return new DetachedMeshRigValidation(false, ["Skeleton is present but per-vertex bone streams are missing or incomplete."]);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bone, index) in mesh.Bones.Select((value, index) => (value, index)))
        {
            if (string.IsNullOrWhiteSpace(bone.Name) || !names.Add(bone.Name))
                AddIssue($"Skeleton bone {index} has an empty or duplicate name.");
            if ((index == 0 && bone.ParentIndex is not (-1 or 0)) ||
                (index > 0 && (bone.ParentIndex < 0 || bone.ParentIndex >= index)))
                AddIssue($"Skeleton bone {index} has an invalid parent index {bone.ParentIndex}.");
            if (!IsFinite(bone.Position) || !IsFinite(bone.Orientation) ||
                bone.Orientation.LengthSquared() < MinimumQuaternionLengthSquared)
                AddIssue($"Skeleton bone {index} has a non-finite or zero transform.");
        }

        for (var vertex = 0; vertex < mesh.Positions.Length; vertex++)
        {
            var indices = mesh.BoneIndices[vertex];
            var weights = mesh.BoneWeights[vertex];
            var values = new[] { weights.X, weights.Y, weights.Z, weights.W };
            var sum = 0f;
            for (var influence = 0; influence < values.Length; influence++)
            {
                var weight = values[influence];
                if (!float.IsFinite(weight) || weight < 0)
                {
                    AddIssue($"Vertex {vertex} has a negative or non-finite bone weight.");
                    continue;
                }
                if (weight > MinimumWeight && (uint)indices[influence] >= (uint)mesh.Bones.Count)
                    AddIssue($"Vertex {vertex} references bone {indices[influence]} outside the skeleton.");
                sum += weight;
            }
            if (!float.IsFinite(sum) || sum <= MinimumWeight)
                AddIssue($"Vertex {vertex} has no positive bone influence.");
            else if (Math.Abs(sum - 1f) > 0.001f)
                AddIssue($"Vertex {vertex} bone weights sum to {sum:G6}, not 1.");
        }
        return new DetachedMeshRigValidation(!invalid, issues);
    }

    private static Vector3[] GenerateNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var normals = new Vector3[positions.Count];
        for (var index = 0; index < indices.Count; index += 3)
        {
            var a = indices[index];
            var b = indices[index + 1];
            var c = indices[index + 2];
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (normal.LengthSquared() <= 1e-12f) continue;
            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }
        for (var index = 0; index < normals.Length; index++)
            normals[index] = normals[index].LengthSquared() <= 1e-12f ? Vector3.UnitZ : Vector3.Normalize(normals[index]);
        return normals;
    }

    private static Vector4 CreateTangent(Vector3 normal)
    {
        var axis = Math.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        return new Vector4(Vector3.Normalize(Vector3.Cross(axis, normal)), 1f);
    }

    private static string CalculateSignature(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var position in positions)
            {
                writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z);
            }
            foreach (var index in indices) writer.Write(index);
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static ImportedMeshAsset CloneAsset(ImportedMeshAsset source) => source with
    {
        Positions = source.Positions.ToArray(),
        Normals = source.Normals?.ToArray(),
        Tangents = source.Tangents?.ToArray(),
        TextureCoordinates = source.TextureCoordinates?.ToArray(),
        Indices = source.Indices.ToArray(),
        Sections = source.Sections.ToArray(),
        Bones = source.Bones.ToArray(),
        BoneIndices = source.BoneIndices?.ToArray(),
        BoneWeights = source.BoneWeights?.ToArray(),
        SourceVertexIndices = source.SourceVertexIndices.ToArray()
    };

    private static void ValidateOptionalStream<T>(IReadOnlyCollection<T>? values, int expectedCount, string name, Func<T, bool> finite)
    {
        if (values is null) return;
        if (values.Count != expectedCount)
            throw new InvalidDataException($"Detached mesh {name} stream does not match positions.");
        if (values.Any(value => !finite(value)))
            throw new InvalidDataException($"Detached mesh {name} stream contains a non-finite value.");
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool IsFinite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
