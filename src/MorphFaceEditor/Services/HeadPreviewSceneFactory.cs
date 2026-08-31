using System.IO;
using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Diagnostics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Services;

/// <summary>Converts detached Unreal-space face data into renderer-owned preview scenes and updates.</summary>
public sealed class HeadPreviewSceneFactory
{
    // UE axes are remapped once at the service boundary; the renderer stays coordinate-system agnostic.
    private static readonly Matrix4x4 UnrealToPreview = new(
        0, 0, 1, 0,
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);
    private static readonly Matrix4x4 PreviewToUnreal = Matrix4x4.Transpose(UnrealToPreview);

    public HeadPreviewScene Create(LoadedMorphFace loaded, int lodIndex = 0)
    {
        Validate(loaded);
        var baseLod = RequireLod(loaded.BaseHead, lodIndex);
        var bakedLodOrdinal = FindStoredLodOrdinal(loaded.BaseHead, lodIndex);
        var usesBaseHeadGeometry = loaded.UsesCustomBaseMesh || loaded.IgnoresAuthoredGeometry;
        var positions = !usesBaseHeadGeometry &&
                        bakedLodOrdinal >= 0 &&
                        loaded.Document.BakedLods.Count > bakedLodOrdinal &&
                        loaded.Document.BakedLods[bakedLodOrdinal].Length == baseLod.Topology.VertexCount
            ? loaded.Document.BakedLods[bakedLodOrdinal]
            : baseLod.Positions;
        var finalSkeleton = usesBaseHeadGeometry
            ? []
            : loaded.Document.FinalSkeleton;
        var pose = SkeletalPoseComposer.Compose(
            loaded.BaseHead.Topology.ReferenceSkeleton,
            finalSkeleton);
        return CreateScene(
            loaded,
            positions,
            baseLod.Normals,
            finalSkeleton,
            ConvertPalette(pose.SkinningMatrices),
            applySkinning: !usesBaseHeadGeometry,
            lodIndex);
    }

    private static int FindStoredLodOrdinal(SkeletalMeshAsset mesh, int lodIndex)
    {
        if (mesh.AvailableLods.Count == 0)
        {
            return lodIndex < mesh.AvailableLodPositions.Count ? lodIndex : -1;
        }

        var ordinal = 0;
        foreach (var lod in mesh.AvailableLods.OrderBy(lod => lod.LodIndex))
        {
            if (lod.LodIndex == lodIndex)
            {
                return ordinal;
            }
            ordinal++;
        }
        return -1;
    }

    public HeadPreviewScene CreateEditable(LoadedMorphFace loaded, MorphFaceEvaluation evaluation, int lodIndex = 0)
    {
        Validate(loaded);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!evaluation.LodGeometry.TryGetValue(lodIndex, out var geometry))
        {
            throw new InvalidDataException($"The editable face has no evaluated LOD {lodIndex}.");
        }
        return CreateScene(
            loaded,
            geometry.Positions,
            geometry.Normals,
            evaluation.FinalSkeleton,
            ConvertPalette(evaluation.Pose.SkinningMatrices),
            applySkinning: true,
            lodIndex);
    }

    public HeadPreviewDeformationUpdate CreateUpdate(LoadedMorphFace loaded, MorphFaceEvaluation evaluation, int lodIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!evaluation.LodGeometry.TryGetValue(lodIndex, out var geometry))
        {
            throw new InvalidDataException($"The editable face has no evaluated LOD {lodIndex}.");
        }
        var mesh = CreateMesh(
            loaded.BaseHead,
            RequireLod(loaded.BaseHead, lodIndex),
            geometry.Positions,
            geometry.Normals,
            loaded.Materials,
            attachment: false,
            applySkinning: true,
            game: loaded.Game);
        return new HeadPreviewDeformationUpdate(
            mesh.Name,
            mesh.Vertices,
            ConvertPalette(evaluation.Pose.SkinningMatrices))
        {
            AttachmentUpdates = EnumerateAttachments(loaded)
                .Select(attachment =>
                {
                    var attachmentLod = SelectLod(attachment, lodIndex);
                    var deformed = SkinAttachment(attachment, attachmentLod, evaluation.FinalSkeleton);
                    var attachmentMesh = CreateMesh(
                        attachment,
                        attachmentLod,
                        deformed.Positions,
                        deformed.Normals,
                        loaded.Materials,
                        attachment: true,
                        applySkinning: false,
                        game: loaded.Game);
                    return new HeadPreviewMeshVertexUpdate(attachmentMesh.Name, attachmentMesh.Vertices);
                })
                .ToArray()
        };
    }

    public HeadPreviewMaterialUpdate CreateMaterialUpdate(LoadedMorphFace loaded, ResolvedHeadMaterialSet materials)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(materials);
        var attachmentMeshes = EnumerateAttachments(loaded).ToArray();
        var hairMaterialKeys = attachmentMeshes
            .Where(mesh => mesh.RenderData is not null)
            .SelectMany(mesh => mesh.RenderData!.MaterialSlots)
            .Where(identity => identity is not null)
            .Cast<AssetIdentity>()
            .Select(MaterialIdentityKey.Create)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var update = new[] { loaded.BaseHead }.Concat(attachmentMeshes)
            .Where(mesh => mesh.RenderData is not null)
            .SelectMany(mesh => mesh.RenderData!.MaterialSlots)
            .Where(identity => identity is not null)
            .Cast<AssetIdentity>()
            .DistinctBy(MaterialIdentityKey.Create)
            .Select(identity => CreateMaterial(
                identity,
                materials,
                hairMaterialKeys.Contains(MaterialIdentityKey.Create(identity)),
                loaded.Game))
            .ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase);
        return new HeadPreviewMaterialUpdate(update);
    }

    private static HeadPreviewScene CreateScene(
        LoadedMorphFace loaded,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<BoneTranslation> finalSkeleton,
        IReadOnlyList<Matrix4x4> palette,
        bool applySkinning,
        int lodIndex)
    {
        var meshes = new List<HeadPreviewMesh>
        {
            CreateMesh(loaded.BaseHead, RequireLod(loaded.BaseHead, lodIndex), positions, normals, loaded.Materials, attachment: false, applySkinning: applySkinning,
                game: loaded.Game)
        };
        if (loaded.HairMesh is not null)
        {
            var attachmentLod = SelectLod(loaded.HairMesh, lodIndex);
            var deformed = SkinAttachment(loaded.HairMesh, attachmentLod, finalSkeleton);
            meshes.Add(CreateMesh(
                loaded.HairMesh,
                attachmentLod,
                deformed.Positions,
                deformed.Normals,
                loaded.Materials,
                attachment: true,
                applySkinning: false,
                game: loaded.Game));
        }
        foreach (var otherMesh in loaded.OtherMeshes)
        {
            var attachmentLod = SelectLod(otherMesh, lodIndex);
            var deformed = SkinAttachment(otherMesh, attachmentLod, finalSkeleton);
            meshes.Add(CreateMesh(
                otherMesh,
                attachmentLod,
                deformed.Positions,
                deformed.Normals,
                loaded.Materials,
                attachment: true,
                applySkinning: false,
                game: loaded.Game));
        }

        var allPositions = meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.Position).ToArray();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var position in allPositions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        return new HeadPreviewScene(
            loaded.Document.Source.InstancedPath,
            meshes,
            new HeadPreviewBounds(minimum, maximum),
            palette);
    }

    private static DeformationResult SkinAttachment(
        SkeletalMeshAsset attachment,
        SkeletalMeshLod lod,
        IReadOnlyList<BoneTranslation> finalSkeleton)
    {
        var pose = SkeletalPoseComposer.Compose(attachment.Topology.ReferenceSkeleton, finalSkeleton);
        return CpuSkinningEvaluator.Skin(
            new DeformationResult(
                lod.Positions.ToArray(),
                lod.Normals.ToArray(),
                0,
                ["Attachment bind geometry uses the face's named final-skeleton translations."]),
            lod.RenderData,
            pose.SkinningMatrices);
    }

    private static void Validate(LoadedMorphFace loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (!loaded.IgnoresAuthoredGeometry && !loaded.TopologyDiagnostics.IsValid)
        {
            throw new InvalidDataException("The face cannot be previewed because topology validation failed.");
        }
        if (loaded.IgnoresAuthoredGeometry && !TopologyDiagnostics.Analyze(loaded.BaseHead).IsValid)
        {
            throw new InvalidDataException("The material-only base head cannot be previewed because topology validation failed.");
        }
        if (loaded.HairMesh is not null && !TopologyDiagnostics.Analyze(loaded.HairMesh).IsValid)
        {
            throw new InvalidDataException("The hair/attachment mesh cannot be previewed because topology validation failed.");
        }
        if (loaded.OtherMeshes.Any(mesh => !TopologyDiagnostics.Analyze(mesh).IsValid))
        {
            throw new InvalidDataException("An m_oOtherMeshes attachment cannot be previewed because topology validation failed.");
        }
    }

    private static IEnumerable<SkeletalMeshAsset> EnumerateAttachments(LoadedMorphFace loaded)
    {
        if (loaded.HairMesh is not null)
        {
            yield return loaded.HairMesh;
        }
        foreach (var mesh in loaded.OtherMeshes)
        {
            yield return mesh;
        }
    }

    private static HeadPreviewMesh CreateMesh(
        SkeletalMeshAsset source,
        SkeletalMeshLod lod,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        ResolvedHeadMaterialSet materials,
        bool attachment,
        bool applySkinning,
        MorphFaceGame game)
    {
        var renderData = lod.RenderData;
        if (positions.Count != lod.Topology.VertexCount || normals.Count != lod.Topology.VertexCount)
        {
            throw new InvalidDataException(
                $"Mesh '{source.Source.InstancedPath}' LOD {lod.LodIndex} has {lod.Topology.VertexCount} vertices but received {positions.Count} positions and {normals.Count} normals.");
        }

        var vertices = new HeadPreviewVertex[lod.Topology.VertexCount];
        for (var index = 0; index < vertices.Length; index++)
        {
            var boneIndices = renderData.BoneIndices[index];
            var previewNormal = ConvertDirection(normals[index]);
            var previewTangent = SkeletalMeshTangentBasis.Orthogonalize(
                ConvertTangent(renderData.Tangents[index]),
                previewNormal);
            vertices[index] = new HeadPreviewVertex(
                ConvertPosition(positions[index]),
                previewNormal,
                previewTangent,
                renderData.TextureCoordinates[index],
                checked((uint)Math.Max(boneIndices.X, 0)),
                checked((uint)Math.Max(boneIndices.Y, 0)),
                checked((uint)Math.Max(boneIndices.Z, 0)),
                checked((uint)Math.Max(boneIndices.W, 0)),
                renderData.BoneWeights[index]);
        }

        var sections = lod.Topology.Sections.Select(section =>
        {
            var identity = section.MaterialIndex >= 0 && section.MaterialIndex < renderData.MaterialSlots.Count
                ? renderData.MaterialSlots[section.MaterialIndex]
                : null;
            var material = identity is null
                ? new HeadPreviewMaterial($"Material {section.MaterialIndex}", HeadMaterialFamily.Unknown) with
                {
                    IsLe2 = game == MorphFaceGame.LE2,
                    IsLe3 = game == MorphFaceGame.LE3
                }
                : CreateMaterial(identity, materials, attachment, game);
            return new HeadPreviewSection(
                section.BaseIndex,
                checked(section.TriangleCount * 3),
                section.MaterialIndex,
                material);
        }).ToArray();

        return new HeadPreviewMesh(
            source.Source.InstancedPath,
            vertices,
            renderData.Indices.Select(index => checked((uint)index)).ToArray(),
            sections,
            attachment,
            applySkinning);
    }

    private static SkeletalMeshLod RequireLod(SkeletalMeshAsset mesh, int lodIndex) =>
        mesh.FindLod(lodIndex) ?? throw new InvalidDataException(
            $"Mesh '{mesh.Source.InstancedPath}' has no renderable LOD {lodIndex}.");

    private static SkeletalMeshLod SelectLod(SkeletalMeshAsset mesh, int requestedLod) =>
        mesh.FindLod(requestedLod) ??
        mesh.AvailableLods.Where(lod => lod.LodIndex <= requestedLod).MaxBy(lod => lod.LodIndex) ??
        RequireLod(mesh, 0);

    private static HeadPreviewMaterial CreateMaterial(
        AssetIdentity identity,
        ResolvedHeadMaterialSet materials,
        bool attachment,
        MorphFaceGame game)
    {
        var resolved = materials.Find(identity);
        if (resolved is null)
        {
            var fallbackFamily = HeadMaterialClassifier.Classify(identity.InstancedPath, attachment);
            return new HeadPreviewMaterial(identity.InstancedPath, fallbackFamily) with
            {
                Key = MaterialIdentityKey.Create(identity),
                IsLe2 = game == MorphFaceGame.LE2,
                IsLe3 = game == MorphFaceGame.LE3
            };
        }
        var textures = resolved.Textures.Values.ToDictionary(
            value => value.ParameterName,
            value => new HeadPreviewTexture(
                value.Texture.CacheKey,
                value.ParameterName,
                value.Texture.Width,
                value.Texture.Height,
                value.Texture.Rgba8,
                value.Texture.Role,
                value.Texture.ColorSpace,
                value.Texture.AlphaPolicy,
                value.Texture.HasMeaningfulAlpha,
                value.Texture.Mips),
            StringComparer.OrdinalIgnoreCase);
        var family = attachment && resolved.Family is not HeadMaterialFamily.Hair and not HeadMaterialFamily.Lashes
            ? HeadMaterialClassifier.Classify(identity.InstancedPath, attachment: true)
            : resolved.Family;
        return new HeadPreviewMaterial(
            resolved.Key,
            resolved.Source.InstancedPath,
            family,
            family == resolved.Family ? resolved.BlendMode : HumanMaterialProfiles.BlendMode(family),
            family == resolved.Family ? resolved.TwoSided : HumanMaterialProfiles.IsTwoSided(family),
            resolved.Scalars,
            resolved.Vectors,
            textures)
        {
            SupportedScalars = resolved.SupportedScalars,
            SupportedVectors = resolved.SupportedVectors,
            SupportedTextures = resolved.SupportedTextures,
            IsLe2 = game == MorphFaceGame.LE2,
            IsLe3 = game == MorphFaceGame.LE3,
            FixedCubeTexture = resolved.FixedCubeTexture is null
                ? null
                : new HeadPreviewCubeTexture(
                    resolved.FixedCubeTexture.CacheKey,
                    resolved.FixedCubeTexture.Size,
                    resolved.FixedCubeTexture.Rgba8Faces,
                    resolved.FixedCubeTexture.ColorSpace,
                    resolved.FixedCubeTexture.Mips),
            SecondaryFixedCubeTexture = resolved.SecondaryFixedCubeTexture is null
                ? null
                : new HeadPreviewCubeTexture(
                    resolved.SecondaryFixedCubeTexture.CacheKey,
                    resolved.SecondaryFixedCubeTexture.Size,
                    resolved.SecondaryFixedCubeTexture.Rgba8Faces,
                    resolved.SecondaryFixedCubeTexture.ColorSpace,
                    resolved.SecondaryFixedCubeTexture.Mips)
        };
    }

    private static IReadOnlyList<Matrix4x4> ConvertPalette(IReadOnlyList<Matrix4x4> palette) =>
        palette.Select(matrix => PreviewToUnreal * matrix * UnrealToPreview).ToArray();

    private static Vector3 ConvertPosition(Vector3 value) => Vector3.Transform(value, UnrealToPreview);

    private static Vector3 ConvertDirection(Vector3 value)
    {
        var converted = Vector3.TransformNormal(value, UnrealToPreview);
        return converted.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(converted);
    }

    private static Vector4 ConvertTangent(Vector4 value)
    {
        var direction = ConvertDirection(new Vector3(value.X, value.Y, value.Z));
        return new Vector4(direction, value.W);
    }
}
