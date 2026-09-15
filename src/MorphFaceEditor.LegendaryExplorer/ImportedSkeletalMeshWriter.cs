using System.Numerics;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using LecColor = LegendaryExplorerCore.SharpDX.Color;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Builds the binary and LOD property payload for a detached mesh export.
/// ImportedMeshAsset is already in the canonical basis shared by the detached
/// preview and the UE3 package reader. The writer therefore preserves the
/// supplied geometry and transforms; no origin/bone-offset repair is applied.
/// </summary>
public static class ImportedSkeletalMeshWriter
{
    private const int MaxInfluences = 4;
    private const byte FullInfluence = 255;

    /// <summary>
    /// Creates one LOD skeletal mesh and its companion LODInfo property. The
    /// supplied material indexes are written verbatim to the mesh material
    /// array; they are not looked up by name here.
    /// </summary>
    public static ImportedSkeletalMeshBuild Build(
        ImportedMeshAsset asset,
        MEGame game,
        IReadOnlyList<int> materialUIndexes)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(materialUIndexes);
        Validate(asset, game, materialUIndexes);

        var skeleton = BuildSkeleton(asset, out var skeletalDepth);
        var hasRig = HasUsableRig(asset);
        if (!hasRig)
        {
            skeleton = BuildMinimumSkeleton();
            skeletalDepth = 1;
        }

        var positions = asset.Positions;
        var vertices = BuildVertices(asset, game, hasRig, skeleton.Length);
        var bounds = CalculateBounds(positions);

        var lod = new StaticLODModel
        {
            IndexBuffer = asset.Indices.Select(value => checked((ushort)value)).ToArray(),
            RequiredBones = Enumerable.Range(0, skeleton.Length).Select(value => checked((byte)value)).ToArray(),
            ActiveBoneIndices = Enumerable.Range(0, skeleton.Length).Select(value => checked((ushort)value)).ToArray(),
            RawPointIndices = asset.SourceVertexIndices.Select(value => checked((ushort)value)).ToArray(),
            NumVertices = checked((uint)vertices.Length),
            NumTexCoords = 1,
            Sections = asset.Sections
                .OrderBy(section => section.IndexStart)
                .Select(section => new SkelMeshSection
                {
                    BaseIndex = checked((uint)section.IndexStart),
                    ChunkIndex = 0,
                    MaterialIndex = checked((ushort)section.MaterialIndex),
                    NumTriangles = section.IndexCount / 3
                }).ToArray(),
            Chunks =
            [
                new SkelMeshChunk
                {
                    BaseVertexIndex = 0,
                    BoneMap = Enumerable.Range(0, skeleton.Length)
                        .Select(value => checked((ushort)value)).ToArray(),
                    NumRigidVertices = hasRig ? 0 : vertices.Length,
                    NumSoftVertices = hasRig ? vertices.Length : 0,
                    MaxBoneInfluences = MaxInfluences
                }
            ]
        };

        if (game == MEGame.LE1)
        {
            lod.ME1VertexBufferGPUSkin = vertices.Select(ToMe1Vertex).ToArray();
        }
        else
        {
            lod.VertexBufferGPUSkin = new SkeletalMeshVertexBuffer
            {
                NumTexCoords = 1,
                MeshExtension = Vector3.One,
                MeshOrigin = Vector3.Zero,
                VertexData = vertices
            };
        }

        var mesh = SkeletalMesh.Create();
        mesh.Materials = materialUIndexes.ToArray();
        mesh.Bounds = bounds;
        mesh.Origin = Vector3.Zero;
        mesh.RotOrigin = new Rotator(0, 0, 0);
        mesh.RefSkeleton = skeleton;
        mesh.SkeletalDepth = skeletalDepth;
        mesh.LODModels = [lod];
        for (var index = 0; index < skeleton.Length; index++)
            mesh.NameIndexMap.Add(skeleton[index].Name, index);

        return new ImportedSkeletalMeshBuild(mesh, MeshHelper.GetLodInfoForSkeletalMesh(mesh, game));

        GPUSkinVertex[] BuildVertices(
            ImportedMeshAsset source,
            MEGame _,
            bool rigged,
            int boneCount)
        {
            var output = new GPUSkinVertex[source.Positions.Length];
            for (var index = 0; index < output.Length; index++)
            {
                var normal = NormalizeOrFallback(source.Normals?[index] ?? Vector3.Zero, Vector3.UnitZ);
                var tangentValue = source.Tangents?[index] ?? new Vector4(Vector3.UnitX, 1);
                var tangent = new Vector3(tangentValue.X, tangentValue.Y, tangentValue.Z);
                tangent = NormalizeOrFallback(tangent, Vector3.UnitX);
                tangent -= normal * Vector3.Dot(normal, tangent);
                tangent = NormalizeOrFallback(tangent, OrthogonalTangent(normal));
                var sign = tangentValue.W < 0 ? -1f : 1f;
                (Influences Bones, Influences Weights) influences = rigged
                    ? QuantizeInfluences(source.BoneIndices![index], source.BoneWeights![index], boneCount)
                    : (new Influences(0, 0, 0, 0), new Influences(FullInfluence, 0, 0, 0));
                output[index] = new GPUSkinVertex
                {
                    Position = source.Positions[index],
                    UV = new Vector2DHalf(
                        (source.TextureCoordinates?[index] ?? Vector2.Zero).X,
                        (source.TextureCoordinates?[index] ?? Vector2.Zero).Y),
                    TangentX = (PackedNormal)new Vector4(tangent, 1),
                    TangentZ = (PackedNormal)new Vector4(normal, sign),
                    InfluenceBones = influences.Bones,
                    InfluenceWeights = influences.Weights
                };
            }
            return output;
        }

        SoftSkinVertex ToMe1Vertex(GPUSkinVertex vertex)
        {
            var normal = NormalizeOrFallback((Vector3)vertex.TangentZ, Vector3.UnitZ);
            var tangent = NormalizeOrFallback((Vector3)vertex.TangentX, OrthogonalTangent(normal));
            var sign = ((Vector4)vertex.TangentZ).W < 0 ? -1f : 1f;
            var bitangent = NormalizeOrFallback(Vector3.Cross(normal, tangent) * sign, Vector3.UnitY);
            return new SoftSkinVertex
            {
                Position = vertex.Position,
                TangentX = vertex.TangentX,
                TangentY = (PackedNormal)new Vector4(bitangent, 1),
                TangentZ = vertex.TangentZ,
                UV = new Vector2(vertex.UV.X, vertex.UV.Y),
                UV2 = Vector2.Zero,
                UV3 = Vector2.Zero,
                UV4 = Vector2.Zero,
                BoneColor = new LecColor(1f),
                InfluenceBones = vertex.InfluenceBones,
                InfluenceWeights = vertex.InfluenceWeights
            };
        }
    }

    internal static (Influences Bones, Influences Weights) QuantizeInfluences(
        BoneIndex4 indices,
        Vector4 weights,
        int boneCount)
    {
        var source = new[]
        {
            (Bone: indices.X, Weight: weights.X),
            (Bone: indices.Y, Weight: weights.Y),
            (Bone: indices.Z, Weight: weights.Z),
            (Bone: indices.W, Weight: weights.W)
        };
        var positive = source
            .Where(value => value.Weight > 0)
            .OrderByDescending(value => value.Weight)
            .ToArray();
        if (positive.Length == 0)
            return (new Influences(0, 0, 0, 0), new Influences(FullInfluence, 0, 0, 0));

        var sum = positive.Sum(value => value.Weight);
        if (!float.IsFinite(sum) || sum <= 0)
            throw new InvalidDataException("Imported mesh bone weights do not have a finite positive sum.");
        var floors = positive.Select(value =>
        {
            var exact = value.Weight * FullInfluence / sum;
            return (value.Bone, Bytes: (byte)MathF.Floor(exact), Remainder: exact - MathF.Floor(exact));
        }).ToArray();
        var bytes = new byte[MaxInfluences];
        var bones = new byte[MaxInfluences];
        var assigned = floors.Sum(value => (int)value.Bytes);
        for (var index = 0; index < floors.Length; index++)
        {
            bones[index] = checked((byte)floors[index].Bone);
            bytes[index] = floors[index].Bytes;
        }
        foreach (var remainderIndex in Enumerable.Range(0, floors.Length)
                     .OrderByDescending(index => floors[index].Remainder)
                     .Take(FullInfluence - assigned))
            bytes[remainderIndex]++;
        for (var index = positive.Length; index < MaxInfluences; index++)
            bones[index] = 0;
        return (
            new Influences(bones[0], bones[1], bones[2], bones[3]),
            new Influences(bytes[0], bytes[1], bytes[2], bytes[3]));
    }

    private static bool HasUsableRig(ImportedMeshAsset asset) =>
        asset.Bones.Count > 0 &&
        asset.BoneIndices?.Length == asset.Positions.Length &&
        asset.BoneWeights?.Length == asset.Positions.Length &&
        asset.BoneWeights.Any(value => value != Vector4.Zero);

    private static MeshBone[] BuildSkeleton(ImportedMeshAsset asset, out int depth)
    {
        var children = new int[asset.Bones.Count];
        for (var index = 0; index < asset.Bones.Count; index++)
        {
            var parent = asset.Bones[index].ParentIndex == index ? -1 : asset.Bones[index].ParentIndex;
            if (parent >= 0) children[parent]++;
        }
        var result = asset.Bones.Select((bone, index) => new MeshBone
        {
            Name = new NameReference(bone.Name, 0),
            ParentIndex = bone.ParentIndex == index ? -1 : bone.ParentIndex,
            NumChildren = children[index],
            Position = bone.Position,
            Orientation = bone.Orientation.LengthSquared() > 1e-12f
                ? Quaternion.Normalize(bone.Orientation)
                : Quaternion.Identity,
            BoneColor = new LecColor(1f),
            Flags = 0
        }).ToArray();
        depth = result.Length == 0 ? 0 : Enumerable.Range(0, result.Length).Max(index => BoneDepth(result, index));
        return result;
    }

    private static MeshBone[] BuildMinimumSkeleton() =>
    [
        new MeshBone
        {
            Name = new NameReference("root", 0),
            ParentIndex = -1,
            NumChildren = 0,
            Position = Vector3.Zero,
            Orientation = Quaternion.Identity,
            BoneColor = new LecColor(1f),
            Flags = 0
        }
    ];

    private static int BoneDepth(IReadOnlyList<MeshBone> bones, int index)
    {
        var depth = 1;
        var seen = new HashSet<int>();
        for (var current = index; bones[current].ParentIndex >= 0; current = bones[current].ParentIndex)
        {
            if (!seen.Add(current)) throw new InvalidDataException("Imported mesh skeleton contains a parent cycle.");
            depth++;
        }
        return depth;
    }

    private static BoxSphereBounds CalculateBounds(IReadOnlyList<Vector3> positions)
    {
        var min = positions[0];
        var max = positions[0];
        foreach (var position in positions.Skip(1))
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        var origin = (min + max) / 2;
        var extent = (max - min) / 2;
        return new BoxSphereBounds
        {
            Origin = origin,
            BoxExtent = extent,
            SphereRadius = extent.Length()
        };
    }

    private static void Validate(ImportedMeshAsset asset, MEGame game, IReadOnlyList<int> materials)
    {
        if (game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
            throw new ArgumentOutOfRangeException(nameof(game), "Only Legendary Edition games are supported.");
        if (asset.Positions.Length == 0 || asset.Positions.Length > ushort.MaxValue)
            throw new InvalidDataException("Imported mesh must contain between one and 65535 vertices.");
        if (asset.Positions.Any(value => !IsFinite(value)))
            throw new InvalidDataException("Imported mesh contains a non-finite position.");
        if (!asset.HasCompleteTextureCoordinates)
            throw new InvalidDataException("PCC export requires complete texture coordinates for every mesh vertex.");
        if (asset.SourceVertexIndices.Length != asset.Positions.Length ||
            asset.SourceVertexIndices.Any(value => value < 0 || value > ushort.MaxValue))
            throw new InvalidDataException("Imported mesh source vertex mapping is invalid.");
        if (asset.Indices.Length == 0 || asset.Indices.Length % 3 != 0)
            throw new InvalidDataException("Imported mesh indices must contain complete triangles.");
        if (asset.Indices.Any(value => (uint)value >= (uint)asset.Positions.Length))
            throw new InvalidDataException("Imported mesh contains an out-of-range triangle index.");
        if (materials.Count == 0)
            throw new InvalidDataException("Imported mesh has no material slots.");
        if (materials.Any(value => value < 0))
            throw new InvalidDataException("Imported mesh material UIndexes must be non-negative.");
        if (asset.Sections.Count == 0)
            throw new InvalidDataException("Imported mesh has no material sections.");
        var cursor = 0;
        foreach (var section in asset.Sections.OrderBy(value => value.IndexStart))
        {
            if (section.MaterialIndex < 0 || section.MaterialIndex >= materials.Count ||
                section.IndexStart != cursor || section.IndexCount <= 0 || section.IndexCount % 3 != 0)
                throw new InvalidDataException("Imported mesh sections must cover the index buffer contiguously.");
            cursor = checked(section.IndexStart + section.IndexCount);
        }
        if (cursor != asset.Indices.Length)
            throw new InvalidDataException("Imported mesh sections do not cover the full index buffer.");
        ValidateStream(asset.Normals, asset.Positions.Length, nameof(asset.Normals));
        ValidateStream(asset.Tangents, asset.Positions.Length, nameof(asset.Tangents));
        ValidateStream(asset.TextureCoordinates, asset.Positions.Length, nameof(asset.TextureCoordinates));
        if (asset.Normals?.Any(value => !IsFinite(value)) == true ||
            asset.Tangents?.Any(value => !IsFinite(value)) == true ||
            asset.TextureCoordinates?.Any(value => !IsFinite(value)) == true)
            throw new InvalidDataException("Imported mesh contains a non-finite render stream value.");
        if (asset.Bones.Count > byte.MaxValue)
            throw new InvalidDataException("Imported mesh skeleton exceeds the 255-bone influence limit.");
        if (asset.BoneIndices is not null && asset.BoneIndices.Length != asset.Positions.Length ||
            asset.BoneWeights is not null && asset.BoneWeights.Length != asset.Positions.Length)
            throw new InvalidDataException("Imported mesh bone streams must match the position count.");
        if (asset.BoneIndices is null != (asset.BoneWeights is null))
            throw new InvalidDataException("Imported mesh must provide both bone index and weight streams.");
        if (asset.BoneIndices is not null)
        {
            foreach (var index in asset.BoneIndices)
                if (new[] { index.X, index.Y, index.Z, index.W }.Any(value => value < 0 || value >= asset.Bones.Count))
                    throw new InvalidDataException("Imported mesh contains an out-of-range bone index.");
            if (asset.BoneWeights!.Any(value =>
                    !float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W) ||
                    value.X < 0 || value.Y < 0 || value.Z < 0 || value.W < 0))
                throw new InvalidDataException("Imported mesh contains invalid bone weights.");
        }
        foreach (var bone in asset.Bones)
        {
            if (string.IsNullOrWhiteSpace(bone.Name) || bone.ParentIndex < -1 || bone.ParentIndex >= asset.Bones.Count)
                throw new InvalidDataException("Imported mesh contains an invalid skeleton bone.");
            if (!float.IsFinite(bone.Position.X) || !float.IsFinite(bone.Position.Y) || !float.IsFinite(bone.Position.Z) ||
                !float.IsFinite(bone.Orientation.X) || !float.IsFinite(bone.Orientation.Y) ||
                !float.IsFinite(bone.Orientation.Z) || !float.IsFinite(bone.Orientation.W))
                throw new InvalidDataException("Imported mesh contains a non-finite skeleton transform.");
        }
        if (asset.Bones.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != asset.Bones.Count)
            throw new InvalidDataException("Imported mesh skeleton contains duplicate bone names.");
    }

    private static void ValidateStream<T>(IReadOnlyCollection<T>? stream, int count, string name)
    {
        if (stream is not null && stream.Count != count)
            throw new InvalidDataException($"Imported mesh {name} stream does not match the position count.");
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback) =>
        IsFinite(value) && value.LengthSquared() > 1e-12f
            ? Vector3.Normalize(value)
            : fallback;

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static Vector3 OrthogonalTangent(Vector3 normal)
    {
        var axis = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return NormalizeOrFallback(Vector3.Cross(axis, normal), Vector3.UnitX);
    }
}

public sealed record ImportedSkeletalMeshBuild(
    SkeletalMesh Binary,
    ArrayProperty<StructProperty> LodInfo);

/// <summary>
/// Adapts the package-independent mesh binary builder to the C4 PCC
/// materializer. Material references are intentionally sparse (only used
/// source slots are supplied), while a SkeletalMesh material array must be
/// dense and retain the source slot indexes.
/// </summary>
public sealed class ImportedSkeletalMeshPackageWriter : ICustomMeshBinaryWriter
{
    public ExportEntry Write(
        IMEPackage package,
        ExportEntry packageRoot,
        ImportedMeshAsset source,
        string meshName,
        IReadOnlyList<CustomMeshMaterialReference> materials)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(packageRoot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(meshName);
        ArgumentNullException.ThrowIfNull(materials);
        if (!ReferenceEquals(packageRoot.FileRef, package))
            throw new InvalidOperationException("The custom mesh package root belongs to another package.");
        if (materials.Count == 0)
            throw new InvalidDataException("The custom mesh has no material references.");

        var highestMaterialIndex = materials.Max(value => value.MaterialIndex);
        if (highestMaterialIndex < 0 || highestMaterialIndex > ushort.MaxValue)
            throw new InvalidDataException("The custom mesh has an invalid material slot index.");
        var materialUIndexes = new int[checked(highestMaterialIndex + 1)];
        var assignedSlots = new HashSet<int>();
        foreach (var reference in materials)
        {
            ArgumentNullException.ThrowIfNull(reference.Material);
            if (reference.MaterialIndex < 0)
                throw new InvalidDataException("The custom mesh has an invalid material slot index.");
            if (!assignedSlots.Add(reference.MaterialIndex))
                throw new InvalidDataException(
                    $"The custom mesh has duplicate material references for slot {reference.MaterialIndex}.");
            if (!ReferenceEquals(reference.Material.FileRef, package))
                throw new InvalidOperationException("A custom mesh material belongs to another package.");
            if (reference.Material.UIndex <= 0)
                throw new InvalidDataException("The custom mesh has a material without a valid export UIndex.");
            materialUIndexes[reference.MaterialIndex] = reference.Material.UIndex;
        }

        var build = ImportedSkeletalMeshWriter.Build(source, package.Game, materialUIndexes);
        var mesh = ExportCreator.CreateExport(
            package,
            meshName,
            "SkeletalMesh",
            packageRoot,
            indexed: false);
        mesh.WriteBinary(build.Binary);
        mesh.WriteProperty(build.LodInfo);
        return mesh;
    }
}
