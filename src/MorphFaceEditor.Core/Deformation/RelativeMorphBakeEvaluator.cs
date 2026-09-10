using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

/// <summary>
/// Preserves an imported Player RON bake while applying canonical target changes
/// relative to its authored feature weights. It owns topology gating and never
/// feeds evaluated output back into the immutable imported baseline.
/// </summary>
internal sealed class RelativeMorphBakeEvaluator
{
    private readonly SkeletalMeshAsset _baseHead;
    private readonly IReadOnlyList<(int LodIndex, Vector3[] Positions)> _orderedBaseLods;
    private readonly IReadOnlyList<Vector3[]> _baselineLods;
    private readonly IReadOnlyList<WeightedMorphTarget> _authoredTargets;
    private readonly IReadOnlyList<MorphTargetAsset> _profileTargets;

    public RelativeMorphBakeEvaluator(
        SkeletalMeshAsset baseHead,
        IReadOnlyList<Vector3[]> authoredLods,
        IReadOnlyList<WeightedMorphTarget> authoredTargets,
        IReadOnlyList<MorphTargetAsset> profileTargets)
    {
        _baseHead = baseHead ?? throw new ArgumentNullException(nameof(baseHead));
        ArgumentNullException.ThrowIfNull(authoredLods);
        ArgumentNullException.ThrowIfNull(authoredTargets);
        ArgumentNullException.ThrowIfNull(profileTargets);
        _orderedBaseLods = baseHead.AvailableLods.Count > 0
            ? baseHead.AvailableLods
                .OrderBy(lod => lod.LodIndex)
                .Select(lod => (lod.LodIndex, lod.Positions))
                .ToArray()
            : baseHead.AvailableLodPositions
                .Select((positions, index) => (index, positions))
                .ToArray();
        _baselineLods = CloneLods(authoredLods);
        _authoredTargets = authoredTargets.ToArray();
        _profileTargets = profileTargets.Distinct().ToArray();
    }

    public bool HasCompatibleLod0
    {
        get
        {
            var ordinal = FindStoredLodOrdinal(0);
            return _baselineLods.Count > 0 && ordinal >= 0 && IsLodCompatible(ordinal, 0);
        }
    }

    public IReadOnlyDictionary<int, DeformationResult> Evaluate(
        IReadOnlyList<WeightedMorphTarget> currentTargets)
    {
        ArgumentNullException.ThrowIfNull(currentTargets);
        var geometryLods = new Dictionary<int, DeformationResult>();
        for (var ordinal = 0; ordinal < _baselineLods.Count; ordinal++)
        {
            var lodIndex = ordinal < _orderedBaseLods.Count
                ? _orderedBaseLods[ordinal].LodIndex
                : ordinal;
            var positions = _baselineLods[ordinal].ToArray();
            var notes = new List<string>
            {
                "Imported authored bake is preserved; canonical feature deltas are applied relative to authored values."
            };

            if (IsLodCompatible(ordinal, lodIndex))
            {
                var authored = SparseMorphEvaluator.Evaluate(_baseHead, _authoredTargets, lodIndex);
                var current = SparseMorphEvaluator.Evaluate(_baseHead, currentTargets, lodIndex);
                for (var index = 0; index < positions.Length; index++)
                {
                    positions[index] += current.Positions[index] - authored.Positions[index];
                }
                var stableNormals = SourceNormals(lodIndex, positions.Length);
                var relativeNormals = new Vector3[positions.Length];
                for (var index = 0; index < relativeNormals.Length; index++)
                {
                    var normal = stableNormals[index] + current.Normals[index] - authored.Normals[index];
                    relativeNormals[index] = IsFinite(normal) && normal.LengthSquared() >= 1e-12f
                        ? Vector3.Normalize(normal)
                        : Vector3.UnitZ;
                }
                notes.Add("Only LODs with verified base and target topology were updated.");
                geometryLods[lodIndex] = new DeformationResult(
                    positions,
                    relativeNormals,
                    current.AppliedDeltaCount,
                    notes);
                continue;
            }

            notes.Add("Stored LOD topology was not proven compatible and remains unchanged.");
            geometryLods[lodIndex] = new DeformationResult(
                positions,
                SourceNormals(lodIndex, positions.Length).ToArray(),
                0,
                notes);
        }
        return geometryLods;
    }

    public IReadOnlyList<Vector3[]> CreateDraftLods(
        IReadOnlyDictionary<int, DeformationResult> evaluatedLods)
    {
        ArgumentNullException.ThrowIfNull(evaluatedLods);
        var result = CloneLods(_baselineLods).ToArray();
        foreach (var (lodIndex, evaluated) in evaluatedLods)
        {
            var ordinal = FindStoredLodOrdinal(lodIndex);
            if (ordinal >= 0 && IsLodCompatible(ordinal, lodIndex))
            {
                result[ordinal] = evaluated.Positions.ToArray();
            }
        }
        return result;
    }

    private int FindStoredLodOrdinal(int lodIndex)
    {
        for (var ordinal = 0; ordinal < _baselineLods.Count; ordinal++)
        {
            if (ordinal < _orderedBaseLods.Count && _orderedBaseLods[ordinal].LodIndex == lodIndex)
            {
                return ordinal;
            }
        }
        return -1;
    }

    private bool IsLodCompatible(int ordinal, int lodIndex)
    {
        if ((uint)ordinal >= (uint)_baselineLods.Count ||
            (uint)ordinal >= (uint)_orderedBaseLods.Count)
        {
            return false;
        }
        var basePositions = _orderedBaseLods[ordinal].Positions;
        if ((lodIndex > 0 && _baseHead.FindLod(lodIndex) is null) ||
            basePositions.Length == 0 ||
            basePositions.Length != _baselineLods[ordinal].Length)
        {
            return false;
        }

        // Validate zero-weight targets too: a later slider move must not turn
        // accepted topology into an evaluator exception.
        foreach (var target in _profileTargets)
        {
            var lod = target.Lods.FirstOrDefault(value => value.LodIndex == lodIndex);
            if (lod is null)
            {
                continue;
            }
            if (lod.BaseMeshVertexCount != basePositions.Length ||
                lod.Vertices.Any(vertex =>
                    vertex.SourceIndex < 0 || vertex.SourceIndex >= basePositions.Length ||
                    !IsFinite(vertex.PositionDelta) || !IsFinite(vertex.NormalDelta)))
            {
                return false;
            }
        }
        return true;
    }

    private IReadOnlyList<Vector3> SourceNormals(int lodIndex, int count)
    {
        var source = lodIndex == 0 ? _baseHead.Normals : _baseHead.FindLod(lodIndex)?.Normals;
        return source is { Length: var sourceCount } && sourceCount == count
            ? source
            : Enumerable.Repeat(Vector3.UnitZ, count).ToArray();
    }

    private static IReadOnlyList<Vector3[]> CloneLods(IReadOnlyList<Vector3[]> lods) =>
        lods.Select(lod => lod.ToArray()).ToArray();

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
