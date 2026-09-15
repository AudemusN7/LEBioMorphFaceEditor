using System.Numerics;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace MorphFaceEditor.LegendaryExplorer.Interchange;

/// <summary>Decodes detached PSK and glTF render data without opening a PCC.</summary>
public static class ImportedMeshDecoder
{
    private const float GltfToGameUnits = 100f;

    public static ImportedMeshAsset Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Imported mesh was not found.", path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".psk" or ".pskx" => ReadPsk(path),
            ".gltf" or ".glb" => ReadGltf(path),
            _ => throw new NotSupportedException("Detached mesh import supports PSK/PSKX and glTF/GLB files.")
        };
    }

    private static ImportedMeshAsset ReadPsk(string path)
    {
        var source = PSK.FromFile(path);
        var points = source.Points?.ToArray() ?? throw new InvalidDataException("PSK has no points.");
        var wedges = source.Wedges?.ToArray() ?? throw new InvalidDataException("PSK has no wedges.");
        var faces = source.Faces?.ToArray() ?? throw new InvalidDataException("PSK has no faces.");
        var materials = source.Materials?.ToArray() ?? [];
        if (wedges.Length == 0 || faces.Length == 0) throw new InvalidDataException("PSK has no renderable triangles.");

        foreach (var wedge in wedges)
            if ((uint)wedge.PointIndex >= (uint)points.Length)
                throw new InvalidDataException("PSK wedge references an invalid point.");
        foreach (var face in faces)
        {
            if ((uint)face.WedgeIdx0 >= (uint)wedges.Length ||
                (uint)face.WedgeIdx1 >= (uint)wedges.Length ||
                (uint)face.WedgeIdx2 >= (uint)wedges.Length)
                throw new InvalidDataException("PSK face references an invalid wedge.");
            if (materials.Length == 0 || (uint)face.MatIndex >= (uint)materials.Length)
                throw new InvalidDataException("PSK face references an invalid material.");
        }
        foreach (var weight in source.Weights ?? [])
        {
            if ((uint)weight.Point >= (uint)points.Length || (uint)weight.Bone >= (uint)(source.Bones?.Count ?? 0) ||
                !float.IsFinite(weight.Weight) || weight.Weight < 0)
                throw new InvalidDataException("PSK weight references an invalid point/bone or has an invalid value.");
        }

        var weightsByPoint = (source.Weights ?? [])
            .Where(weight => weight.Weight > 0)
            .GroupBy(weight => weight.Point)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(weight => weight.Weight).ToArray());
        if (weightsByPoint.Values.Any(influences => influences.Length > 4))
            throw new InvalidDataException("PSK contains more than four positive bone influences for one point.");

        var positions = wedges.Select(w => RestorePskHandedness(points[w.PointIndex])).ToArray();
        var uvs = wedges.Select(w => new Vector2(w.U, w.V)).ToArray();
        var sourceIndices = wedges.Select(w => (int)w.PointIndex).ToArray();
        var indices = new List<int>(faces.Length * 3);
        var sections = new List<ImportedMeshSection>();
        foreach (var group in faces.Select((face, i) => (face, i)).GroupBy(v => v.face.MatIndex))
        {
            var start = indices.Count;
            foreach (var value in group)
            {
                // Restoring Y is a reflection, so reverse each triangle exactly once.
                indices.Add(value.face.WedgeIdx0);
                indices.Add(value.face.WedgeIdx2);
                indices.Add(value.face.WedgeIdx1);
            }
            sections.Add(new ImportedMeshSection(group.Key, materials[group.Key].Name ?? string.Empty,
                start, indices.Count - start));
        }

        Vector3[]? normals = null;
        if (source.VertexNormals is { Count: > 0 } supplied)
        {
            if (supplied.Count == points.Length)
                normals = wedges.Select(w => NormalizeOrZero(RestorePskHandedness(supplied[w.PointIndex]))).ToArray();
            else if (supplied.Count == wedges.Length)
                normals = supplied.Select(v => NormalizeOrZero(RestorePskHandedness(v))).ToArray();
        }
        var generatedNormals = normals is null;
        normals ??= GenerateNormals(positions, indices);
        var tangents = GenerateTangents(positions, normals, uvs, indices);
        var bones = (source.Bones ?? []).Select((bone, index) => new ImportedMeshBone(
            bone.Name ?? $"Bone{index}", bone.ParentIndex,
            RestorePskHandedness(bone.Position),
            RestorePskQuaternion(bone.Rotation))).ToArray();
        BoneIndex4[]? boneIndices = null;
        Vector4[]? boneWeights = null;
        if (bones.Length > 0 && (source.Weights?.Count ?? 0) > 0)
        {
            boneIndices = new BoneIndex4[wedges.Length];
            boneWeights = new Vector4[wedges.Length];
            for (var i = 0; i < wedges.Length; i++)
            {
                var values = weightsByPoint.GetValueOrDefault(wedges[i].PointIndex) ?? [];
                var sum = values.Sum(v => v.Weight);
                if (!float.IsFinite(sum)) throw new InvalidDataException("PSK point weights do not have a finite sum.");
                if (sum <= 0) continue;
                var ids = values.Select(v => v.Bone).Concat([0, 0, 0, 0]).Take(4).ToArray();
                var ws = values.Select(v => v.Weight / sum).Concat([0f, 0f, 0f, 0f]).Take(4).ToArray();
                boneIndices[i] = new BoneIndex4(ids[0], ids[1], ids[2], ids[3]);
                boneWeights[i] = new Vector4(ws[0], ws[1], ws[2], ws[3]);
            }
        }
        return new ImportedMeshAsset(path, positions, normals, tangents, uvs, [.. indices], sections,
            bones, boneIndices, boneWeights, sourceIndices, generatedNormals, false);
    }

    private static ImportedMeshAsset ReadGltf(string path)
    {
        var model = ModelRoot.Load(path, new ReadSettings { Validation = ValidationMode.Skip });
        var nodes = model.DefaultScene?.VisualChildren.SelectMany(Flatten).Where(n => n.Mesh is not null)
            .Distinct().Select(n => (Node?)n).ToList() ?? [];
        if (nodes.Count == 0)
            nodes.Add(null); // A mesh without a scene is still a valid detached asset.

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var normalPresent = new List<bool>();
        var tangentPresent = new List<bool>();
        var uvPresent = new List<bool>();
        var indices = new List<int>();
        var sourceIndices = new List<int>();
        var sections = new List<ImportedMeshSection>();
        var materialSlots = new Dictionary<int, int>();
        var bones = Array.Empty<ImportedMeshBone>();
        var boneIndices = new List<BoneIndex4>();
        var boneWeights = new List<Vector4>();
        var haveRig = false;
        Skin? decodedSkin = null;
        var logicalSource = 0;
        var streamOffsets = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var world = node?.WorldMatrix ?? Matrix4x4.Identity;
            if (!Matrix4x4.Invert(world, out var inverse)) throw new InvalidDataException("glTF node has a non-invertible transform.");
            var normalMatrix = Matrix4x4.Transpose(inverse);
            // Legendary Explorer exports game vectors as glTF (X, Z, Y).
            // Swapping Y/Z is itself a reflection, so an otherwise ordinary
            // node changes handedness while an explicitly mirrored node does not.
            var changesHandedness = world.GetDeterminant() >= 0;
            var skin = node?.Skin;
            if (skin is not null)
            {
                if (decodedSkin is not null && decodedSkin.LogicalIndex != skin.LogicalIndex)
                    throw new InvalidDataException("glTF uses more than one skin; a single detached rig cannot represent it safely.");
                decodedSkin = skin;
                if (bones.Length == 0) bones = ReadGltfBones(skin);
            }
            var primitives = node?.Mesh?.Primitives ?? model.LogicalMeshes.SelectMany(m => m.Primitives);
            foreach (var primitive in primitives)
            {
                var positionAccessor = primitive.GetVertexAccessor("POSITION") ?? throw new InvalidDataException("glTF primitive has no POSITION accessor.");
                var localPositions = positionAccessor.AsVector3Array().ToArray();
                if (localPositions.Length == 0) continue;
                var localNormals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array().ToArray();
                var localTangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array().ToArray();
                var localUvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array().ToArray();
                if (localNormals is not null && localNormals.Length != localPositions.Length) throw new InvalidDataException("glTF NORMAL count does not match POSITION.");
                if (localTangents is not null && localTangents.Length != localPositions.Length) throw new InvalidDataException("glTF TANGENT count does not match POSITION.");
                if (localUvs is not null && localUvs.Length != localPositions.Length) throw new InvalidDataException("glTF TEXCOORD_0 count does not match POSITION.");
                // Material-split primitives commonly share all vertex accessors.
                // Reuse that stream for the second primitive; transforms are part
                // of the key through the visual node identity.
                var streamKey = string.Join(':', node?.LogicalIndex ?? -1,
                    positionAccessor.LogicalIndex,
                    primitive.GetVertexAccessor("NORMAL")?.LogicalIndex ?? -1,
                    primitive.GetVertexAccessor("TANGENT")?.LogicalIndex ?? -1,
                    primitive.GetVertexAccessor("TEXCOORD_0")?.LogicalIndex ?? -1,
                    primitive.GetVertexAccessor("JOINTS_0")?.LogicalIndex ?? -1,
                    primitive.GetVertexAccessor("WEIGHTS_0")?.LogicalIndex ?? -1);
                var isNewStream = !streamOffsets.TryGetValue(streamKey, out var offset);
                if (isNewStream)
                {
                    offset = positions.Count;
                    streamOffsets.Add(streamKey, offset);
                    for (var i = 0; i < localPositions.Length; i++)
                    {
                        positions.Add(GltfToCanonicalVector(Vector3.Transform(localPositions[i], world)) * GltfToGameUnits);
                        normals.Add(localNormals is null
                            ? Vector3.Zero
                            : NormalizeOrZero(GltfToCanonicalVector(
                                Vector3.TransformNormal(localNormals[i], normalMatrix))));
                        tangents.Add(localTangents is null
                            ? Vector4.Zero
                            : new Vector4(
                                NormalizeOrZero(GltfToCanonicalVector(Vector3.TransformNormal(
                                    new Vector3(localTangents[i].X, localTangents[i].Y, localTangents[i].Z),
                                    normalMatrix))),
                                changesHandedness ? -localTangents[i].W : localTangents[i].W));
                        uvs.Add(localUvs is null ? Vector2.Zero : localUvs[i]);
                        normalPresent.Add(localNormals is not null);
                        tangentPresent.Add(localTangents is not null);
                        uvPresent.Add(localUvs is not null);
                        sourceIndices.Add(logicalSource++);
                    }
                }
                var primitiveIndices = primitive.GetTriangleIndices();
                var start = indices.Count;
                foreach (var (a, b, c) in primitiveIndices)
                {
                    if ((uint)a >= (uint)localPositions.Length || (uint)b >= (uint)localPositions.Length || (uint)c >= (uint)localPositions.Length)
                        throw new InvalidDataException("glTF triangle references an invalid vertex.");
                    indices.Add(offset + a);
                    indices.Add(offset + (changesHandedness ? c : b));
                    indices.Add(offset + (changesHandedness ? b : c));
                }
                var materialKey = primitive.Material?.LogicalIndex ?? -1;
                if (!materialSlots.TryGetValue(materialKey, out var materialIndex))
                {
                    materialIndex = materialSlots.Count;
                    materialSlots.Add(materialKey, materialIndex);
                }
                var materialName = primitive.Material?.Name ?? $"material-{materialKey}";
                sections.Add(new ImportedMeshSection(materialIndex, materialName, start, indices.Count - start));

                var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array().ToArray();
                var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array().ToArray();
                if (isNewStream && (joints is not null || weights is not null))
                {
                    if (joints is null || weights is null || joints.Length != localPositions.Length || weights.Length != localPositions.Length || bones.Length == 0)
                        throw new InvalidDataException("glTF skin streams are incomplete or have no skeleton.");
                    haveRig = true;
                    for (var i = 0; i < joints.Length; i++)
                    {
                        var j = new[] { (int)joints[i].X, (int)joints[i].Y, (int)joints[i].Z, (int)joints[i].W };
                        if (j.Any(value => (uint)value >= (uint)bones.Length)) throw new InvalidDataException("glTF JOINTS_0 references an invalid joint.");
                        var value = weights[i];
                        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
                            !float.IsFinite(value.Z) || !float.IsFinite(value.W) ||
                            value.X < 0 || value.Y < 0 || value.Z < 0 || value.W < 0)
                            throw new InvalidDataException("glTF WEIGHTS_0 contains a negative or non-finite value.");
                        var sum = value.X + value.Y + value.Z + value.W;
                        if (sum > 0) value /= sum;
                        boneIndices.Add(new BoneIndex4(j[0], j[1], j[2], j[3]));
                        boneWeights.Add(value);
                    }
                }
                else if (isNewStream)
                {
                    for (var i = 0; i < localPositions.Length; i++)
                    {
                        boneIndices.Add(default);
                        boneWeights.Add(Vector4.Zero);
                    }
                }
            }
        }
        if (positions.Count == 0 || indices.Count == 0) throw new InvalidDataException("glTF has no renderable triangles.");
        var positionArray = positions.ToArray();
        var generatedNormalValues = GenerateNormals(positionArray, indices);
        var finalNormals = normals.Select((value, index) => normalPresent[index]
            ? value
            : generatedNormalValues[index]).ToArray();
        var generatedNormals = normalPresent.Any(value => !value);
        var anyUvs = uvPresent.Any(value => value);
        var finalUvs = anyUvs ? uvs.ToArray() : null;
        var generatedTangentValues = GenerateTangents(
            positionArray,
            finalNormals,
            finalUvs,
            indices,
            anyUvs ? uvPresent : null);
        Vector4[]? finalTangents = tangentPresent.Any(value => value) || generatedTangentValues is not null
            ? tangents.Select((value, index) => tangentPresent[index]
                ? value
                : generatedTangentValues?[index] ?? Vector4.Zero).ToArray()
            : null;
        var generatedTangents = generatedTangentValues is not null && tangentPresent.Any(value => !value);
        return new ImportedMeshAsset(path, positions.ToArray(), finalNormals, finalTangents,
            finalUvs, indices.ToArray(), sections, bones,
            haveRig ? boneIndices.ToArray() : null, haveRig ? boneWeights.ToArray() : null,
            sourceIndices.ToArray(), generatedNormals, generatedTangents,
            TextureCoordinatesAreComplete: anyUvs && uvPresent.All(value => value));
    }

    private static IEnumerable<Node> Flatten(Node node)
    {
        yield return node;
        foreach (var child in node.VisualChildren.SelectMany(Flatten)) yield return child;
    }

    private static ImportedMeshBone[] ReadGltfBones(Skin skin)
    {
        var joints = skin.Joints;
        var map = joints.Select((joint, index) => (joint, index)).ToDictionary(v => v.joint, v => v.index);
        return joints.Select((joint, index) => new ImportedMeshBone(
            joint.Name ?? $"Bone{index}", joint.VisualParent is not null && map.TryGetValue(joint.VisualParent, out var parent) ? parent : -1,
            GltfToCanonicalVector(joint.LocalTransform.Translation) * GltfToGameUnits,
            GltfToCanonicalQuaternion(joint.LocalTransform.Rotation))).ToArray();
    }

    internal static Vector3 GltfToCanonicalVector(Vector3 value) => new(value.X, value.Z, value.Y);

    internal static Quaternion GltfToCanonicalQuaternion(Quaternion value)
    {
        // A reflected basis transforms the quaternion's vector (axial) part
        // by det(S) * S while retaining W. This keeps identity as identity.
        var converted = new Quaternion(-value.X, -value.Z, -value.Y, value.W);
        return converted.LengthSquared() > 1e-12f
            ? Quaternion.Normalize(converted)
            : Quaternion.Identity;
    }

    private static Vector3 RestorePskHandedness(Vector3 value) => new(value.X, -value.Y, value.Z);
    private static Quaternion RestorePskQuaternion(Quaternion value)
    {
        var restored = new Quaternion(value.X, -value.Y, value.Z, value.W);
        return restored.LengthSquared() > 1e-12f ? Quaternion.Normalize(restored) : Quaternion.Identity;
    }
    private static Vector3 NormalizeOrZero(Vector3 value) => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

    private static Vector3[] GenerateNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var result = new Vector3[positions.Count];
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = positions[indices[i]]; var b = positions[indices[i + 1]]; var c = positions[indices[i + 2]];
            var normal = Vector3.Cross(b - a, c - a);
            result[indices[i]] += normal; result[indices[i + 1]] += normal; result[indices[i + 2]] += normal;
        }
        for (var i = 0; i < result.Length; i++) result[i] = NormalizeOrZero(result[i]);
        return result;
    }

    private static Vector4[]? GenerateTangents(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3>? normals,
        IReadOnlyList<Vector2>? uvs, IReadOnlyList<int> indices, IReadOnlyList<bool>? uvPresent = null)
    {
        if (normals is null || uvs is null || uvs.Count != positions.Count) return null;
        var tangent = new Vector3[positions.Count]; var bitangent = new Vector3[positions.Count];
        var hasTangent = false;
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var ia = indices[i]; var ib = indices[i + 1]; var ic = indices[i + 2];
            if (uvPresent is not null && (!uvPresent[ia] || !uvPresent[ib] || !uvPresent[ic])) continue;
            var e1 = positions[ib] - positions[ia]; var e2 = positions[ic] - positions[ia];
            var duv1 = uvs[ib] - uvs[ia]; var duv2 = uvs[ic] - uvs[ia];
            var determinant = duv1.X * duv2.Y - duv1.Y * duv2.X;
            if (MathF.Abs(determinant) < 1e-8f) continue;
            hasTangent = true;
            var reciprocal = 1f / determinant;
            var t = (e1 * duv2.Y - e2 * duv1.Y) * reciprocal;
            var bt = (e2 * duv1.X - e1 * duv2.X) * reciprocal;
            tangent[ia] += t; tangent[ib] += t; tangent[ic] += t;
            bitangent[ia] += bt; bitangent[ib] += bt; bitangent[ic] += bt;
        }
        if (!hasTangent) return null;
        return Enumerable.Range(0, positions.Count).Select(i =>
        {
            var n = NormalizeOrZero(normals[i]);
            var t = NormalizeOrZero(tangent[i] - n * Vector3.Dot(n, tangent[i]));
            var handedness = Vector3.Dot(Vector3.Cross(n, t), bitangent[i]) < 0 ? -1f : 1f;
            return new Vector4(t, handedness);
        }).ToArray();
    }
}
