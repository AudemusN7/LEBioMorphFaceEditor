using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public sealed record MorphMeshTopologyMatch(
    string CoordinateSystem,
    Vector3[] CanonicalOrderPositions,
    int[] ImportedToCanonical,
    float BaseRootMeanSquareDistance);

/// <summary>
/// Proves correspondence between detached interchange geometry and a canonical
/// player mesh. Counts are only rejection filters; UV identity and the complete
/// material-labelled triangle graph must also agree before a rig may be reused.
/// </summary>
public static class MorphMeshTopologyMatcher
{
    private const float TextureCoordinateTolerance = 1f / 32768f;

    public static MorphMeshTopologyMatch? Match(
        ImportedMeshAsset imported,
        SkeletalMeshAsset canonical)
    {
        ArgumentNullException.ThrowIfNull(imported);
        ArgumentNullException.ThrowIfNull(canonical);
        var canonicalLod = canonical.FindLod(0);
        var canonicalRender = canonicalLod?.RenderData ?? canonical.RenderData;
        var importedUvs = imported.TextureCoordinates;
        if (canonicalLod is null || canonicalRender is null ||
            importedUvs is null || !imported.HasCompleteTextureCoordinates ||
            imported.Positions.Length != canonicalLod.Positions.Length ||
            importedUvs.Length != canonicalLod.Positions.Length ||
            imported.Indices.Length != canonicalRender.Indices.Count)
        {
            return null;
        }

        foreach (var flipV in new[] { false, true })
        {
            var mapping = TryIdentityMapping(
                              importedUvs,
                              canonicalRender.TextureCoordinates,
                              flipV)
                          ?? TryUniqueUvMapping(
                              importedUvs,
                              canonicalRender.TextureCoordinates,
                              flipV);
            if (mapping is null || !TopologyMatches(imported, canonicalLod, mapping))
            {
                continue;
            }

            return SelectCoordinateMapping(imported, canonicalLod.Positions, mapping, flipV);
        }
        return null;
    }

    private static int[]? TryIdentityMapping(
        IReadOnlyList<Vector2> imported,
        IReadOnlyList<Vector2> canonical,
        bool flipV)
    {
        for (var index = 0; index < imported.Count; index++)
        {
            if (!SameUv(NormalizeUv(imported[index], flipV), canonical[index]))
            {
                return null;
            }
        }
        return Enumerable.Range(0, imported.Count).ToArray();
    }

    private static int[]? TryUniqueUvMapping(
        IReadOnlyList<Vector2> imported,
        IReadOnlyList<Vector2> canonical,
        bool flipV)
    {
        var canonicalByUv = canonical
            .Select((uv, index) => (Key: UvKey(uv), Index: index))
            .GroupBy(value => value.Key)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Index).ToArray());
        var mapping = new int[imported.Count];
        for (var importedIndex = 0; importedIndex < imported.Count; importedIndex++)
        {
            var normalized = NormalizeUv(imported[importedIndex], flipV);
            if (!canonicalByUv.TryGetValue(UvKey(normalized), out var candidates) ||
                candidates.Length != 1 ||
                !SameUv(normalized, canonical[candidates[0]]))
            {
                // Duplicate UVs need a later graph-isomorphism proof. Guessing
                // among them from sculpted positions would make recognition unsafe.
                return null;
            }
            mapping[importedIndex] = candidates[0];
        }
        return mapping.Distinct().Count() == mapping.Length ? mapping : null;
    }

    private static bool TopologyMatches(
        ImportedMeshAsset imported,
        SkeletalMeshLod canonical,
        IReadOnlyList<int> importedToCanonical)
    {
        var importedTriangles = ReadImportedTriangles(imported, importedToCanonical);
        var canonicalTriangles = ReadCanonicalTriangles(canonical);
        return importedTriangles is not null && canonicalTriangles is not null &&
               importedTriangles.SequenceEqual(canonicalTriangles, StringComparer.Ordinal);
    }

    private static string[]? ReadImportedTriangles(
        ImportedMeshAsset mesh,
        IReadOnlyList<int> importedToCanonical)
    {
        var materialByIndex = Enumerable.Repeat(-1, mesh.Indices.Length).ToArray();
        foreach (var section in mesh.Sections)
        {
            if (section.IndexStart < 0 || section.IndexCount < 0 || section.IndexCount % 3 != 0 ||
                section.IndexStart + section.IndexCount > mesh.Indices.Length)
            {
                return null;
            }
            for (var index = section.IndexStart; index < section.IndexStart + section.IndexCount; index++)
            {
                if (materialByIndex[index] >= 0)
                {
                    return null;
                }
                materialByIndex[index] = section.MaterialIndex;
            }
        }
        if (materialByIndex.Any(value => value < 0))
        {
            return null;
        }

        var triangles = new string[mesh.Indices.Length / 3];
        for (var index = 0; index < mesh.Indices.Length; index += 3)
        {
            var source = new[] { mesh.Indices[index], mesh.Indices[index + 1], mesh.Indices[index + 2] };
            if (source.Any(value => value < 0 || value >= importedToCanonical.Count) ||
                materialByIndex[index] != materialByIndex[index + 1] ||
                materialByIndex[index] != materialByIndex[index + 2])
            {
                return null;
            }
            var mapped = source.Select(value => importedToCanonical[value]).Order().ToArray();
            triangles[index / 3] = TriangleKey(materialByIndex[index], mapped);
        }
        Array.Sort(triangles, StringComparer.Ordinal);
        return triangles;
    }

    private static string[]? ReadCanonicalTriangles(SkeletalMeshLod mesh)
    {
        var indices = mesh.RenderData.Indices;
        var materialByIndex = Enumerable.Repeat(-1, indices.Count).ToArray();
        foreach (var section in mesh.Topology.Sections)
        {
            var indexCount = checked(section.TriangleCount * 3);
            if (section.BaseIndex < 0 || indexCount < 0 ||
                section.BaseIndex + indexCount > indices.Count)
            {
                return null;
            }
            for (var index = section.BaseIndex; index < section.BaseIndex + indexCount; index++)
            {
                if (materialByIndex[index] >= 0)
                {
                    return null;
                }
                materialByIndex[index] = section.MaterialIndex;
            }
        }
        if (materialByIndex.Any(value => value < 0))
        {
            return null;
        }

        var triangles = new string[indices.Count / 3];
        for (var index = 0; index < indices.Count; index += 3)
        {
            if (materialByIndex[index] != materialByIndex[index + 1] ||
                materialByIndex[index] != materialByIndex[index + 2])
            {
                return null;
            }
            var vertices = new[] { indices[index], indices[index + 1], indices[index + 2] };
            if (vertices.Any(value => value < 0 || value >= mesh.Positions.Length))
            {
                return null;
            }
            Array.Sort(vertices);
            triangles[index / 3] = TriangleKey(materialByIndex[index], vertices);
        }
        Array.Sort(triangles, StringComparer.Ordinal);
        return triangles;
    }

    private static MorphMeshTopologyMatch SelectCoordinateMapping(
        ImportedMeshAsset imported,
        IReadOnlyList<Vector3> canonicalPositions,
        IReadOnlyList<int> importedToCanonical,
        bool flipV)
    {
        var extension = Path.GetExtension(imported.SourcePath);
        var transforms = extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase) ||
                         extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
            ? new (string Name, Func<Vector3, Vector3> Apply)[]
            {
                ("glTF XYZ", value => value),
                ("glTF X-YZ", value => new Vector3(value.X, -value.Y, value.Z)),
                ("glTF XZ-Y", value => new Vector3(value.X, value.Z, -value.Y)),
                ("glTF X-ZY", value => new Vector3(value.X, -value.Z, value.Y))
            }
            : [("PSK Unreal", (Func<Vector3, Vector3>)(value => value))];

        var best = transforms
            .Select(transform =>
            {
                var reordered = new Vector3[imported.Positions.Length];
                double squared = 0;
                for (var importedIndex = 0; importedIndex < imported.Positions.Length; importedIndex++)
                {
                    var position = transform.Apply(imported.Positions[importedIndex]);
                    var canonicalIndex = importedToCanonical[importedIndex];
                    reordered[canonicalIndex] = position;
                    squared += Vector3.DistanceSquared(position, canonicalPositions[canonicalIndex]);
                }
                return (transform.Name, Positions: reordered,
                    Rms: (float)Math.Sqrt(squared / imported.Positions.Length));
            })
            .MinBy(value => value.Rms);
        var uvSuffix = flipV ? ", flipped V" : string.Empty;
        return new MorphMeshTopologyMatch(
            best.Name + uvSuffix,
            best.Positions,
            importedToCanonical.ToArray(),
            best.Rms);
    }

    private static Vector2 NormalizeUv(Vector2 value, bool flipV) =>
        flipV ? new Vector2(value.X, 1 - value.Y) : value;

    private static bool SameUv(Vector2 left, Vector2 right) =>
        Vector2.DistanceSquared(left, right) <= TextureCoordinateTolerance * TextureCoordinateTolerance;

    private static (int U, int V) UvKey(Vector2 value) =>
        ((int)MathF.Round(value.X / TextureCoordinateTolerance),
            (int)MathF.Round(value.Y / TextureCoordinateTolerance));

    private static string TriangleKey(int material, IReadOnlyList<int> vertices) =>
        $"{material:D6}:{vertices[0]:D8}:{vertices[1]:D8}:{vertices[2]:D8}";
}
