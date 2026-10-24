using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Services;

// Sampling values and settings are copied before the first await; controls remain the texture-decode boundary.
internal sealed record EditorRandomisationRequest(
    string ProfileKey,
    bool Cursed,
    bool RandomiseMorphs,
    bool RandomiseMaterials,
    int MorphStrength,
    int MaterialStrength,
    RandomisationMorphInput Morphs,
    RandomisationMaterialInput Material,
    (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey)? Player,
    (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey)? Human,
    RandomisationHairInput Hair,
    bool IncludeBones,
    bool ResetMaterials,
    MaterialRandomisationScope? MaterialCleanupScope)
{
    public bool HasMaterial => ResetMaterials || Material.ScalarScope.Count +
        Material.VectorScope.Count + Material.TextureScope.Count > 0;
}

internal sealed record RandomisationMorphInput(
    IReadOnlyDictionary<string, float> Current,
    IReadOnlyDictionary<string, float> Baseline,
    IReadOnlyList<MorphRandomisationFeatureBounds> Bounds,
    IReadOnlySet<string> Scope,
    IReadOnlySet<string> ExcludedCharacterMorphs);

internal sealed record RandomisationMaterialInput(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, float> BaselineScalars,
    IReadOnlyDictionary<string, Vector4> BaselineVectors,
    IReadOnlyDictionary<string, string> Textures,
    IReadOnlyList<MaterialRandomisationScalarBounds> ScalarBounds,
    IReadOnlySet<string> ScalarScope,
    IReadOnlySet<string> VectorScope,
    IReadOnlySet<string> TextureScope,
    IReadOnlySet<string> IncludedScalars,
    IReadOnlyList<MaterialRandomisationProfileContext> Profiles)
{
    public string? FindControlName(string? scopeKey, string parameterName, MaterialParameterKind kind) =>
        (kind switch
        {
            MaterialParameterKind.Scalar => Scalars.Keys,
            MaterialParameterKind.Vector => Vectors.Keys,
            _ => Textures.Keys
        }).FirstOrDefault(name =>
            MaterialParameterControlKey.ParameterName(name).Equals(parameterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MaterialParameterControlKey.ScopeKey(name), scopeKey, StringComparison.OrdinalIgnoreCase));
}

internal sealed record MaterialRandomisationProfileContext(
    string Key,
    MaterialRandomisationProfile Profile,
    string? ParameterScopeKey,
    ResolvedHeadMaterial? Template)
{
    public IReadOnlyList<MorphRandomisationDonor> InstalledDonors { get; init; } = [];

    public bool Supports(string controlName, MaterialParameterKind kind) =>
        string.Equals(MaterialParameterControlKey.ScopeKey(controlName), ParameterScopeKey,
            StringComparison.OrdinalIgnoreCase) &&
        (Template is null || Template.Supports(MaterialParameterControlKey.ParameterName(controlName), kind));
}

internal sealed record RandomisationHairInput(
    bool Locked,
    bool CanEditMeshes,
    bool CanEditMorphs,
    bool CurrentMeshIsPaired,
    bool TargetedTextureRoll,
    IReadOnlyList<string> IncludedMorphs,
    IReadOnlyList<TextureCatalogCandidate> Textures,
    IReadOnlyList<AttachmentMeshCandidate> Meshes);
