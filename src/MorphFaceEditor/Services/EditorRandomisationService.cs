using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Services;

internal delegate Task<PreparedMaterialRandomisation> ResolveRandomisationMaterials(
    IReadOnlyDictionary<string, float> scalars, IReadOnlyDictionary<string, Vector4> vectors,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> textureFamilies, string? scopeKey);

internal sealed record PreparedEditorRandomisation(
    IReadOnlyDictionary<string, float> Morphs, PreparedMaterialRandomisation? Material,
    bool RetainHairMesh, bool NoReadableFamily);

/// <summary>Samples detached command inputs; texture decoding is supplied by the editor's existing loader.</summary>
internal sealed class EditorRandomisationService(MorphRandomisationCatalog catalog)
{
    public async Task<PreparedEditorRandomisation> PrepareAsync(EditorRandomisationRequest request,
        int? morphSeed, int? materialSeed, ResolveRandomisationMaterials resolve)
    {
        IReadOnlyDictionary<string, float> morphs = new Dictionary<string, float>();
        MorphRandomisationDonor? donor = null;
        if (morphSeed is { } seed)
        {
            if (request.Cursed)
                morphs = CursedMorphRandomiser.CreateFeatureValues(
                    request.Morphs.Scope.ToDictionary(name => name, name => request.Morphs.Baseline[name],
                        StringComparer.OrdinalIgnoreCase), request.MorphStrength, seed);
            else
            {
                donor = catalog.SelectDonor(request.ProfileKey, seed, requireMaterial: false);
                morphs = catalog.CreateMorphProposal(donor, request.Morphs.Current,
                    request.Morphs.Bounds, request.Morphs.Scope, request.MorphStrength, seed).Values;
            }
        }
        // Cursed deliberately bypasses the NPC Character guard; explicit mesh locks remain.
        var retainHair = !request.Cursed && request.Player is null &&
            request.Morphs.ExcludedCharacterMorphs.Any(name =>
                morphs.GetValueOrDefault(name, request.Morphs.Current[name]) != 0f);
        var protectScalp = request.Hair.CurrentMeshIsPaired && (request.Hair.Locked || retainHair);
        var textureNames = request.Material.TextureScope
            .Where(name => request.Player is null ||
                !IsPlayerExclusiveTexture(MaterialParameterControlKey.ParameterName(name)) &&
                !Player2DaParameters.ContainsKey(MaterialParameterControlKey.ParameterName(name)))
            .Where(name => !protectScalp || !IsHumanScalpTexture(MaterialParameterControlKey.ParameterName(name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        PreparedMaterialRandomisation? material = null;
        var noReadableFamily = false;
        if (materialSeed is { } materialRandomSeed)
        {
            if (request.Cursed)
                material = await PrepareCursedMaterialsAsync(request, textureNames, materialRandomSeed, resolve);
            else if (request.Material.Profiles.Count > 0)
                (material, noReadableFamily) = await PrepareNormalMaterialsAsync(
                    request, textureNames, materialRandomSeed, donor?.Id, resolve);
        }
        return new(morphs, material, retainHair, noReadableFamily);
    }

    private async Task<PreparedMaterialRandomisation> PrepareCursedMaterialsAsync(
        EditorRandomisationRequest request, IReadOnlySet<string> textureNames, int seed,
        ResolveRandomisationMaterials resolve)
    {
        var input = request.Material;
        var values = CursedMorphRandomiser.CreateExtrasProposal([],
            input.ScalarScope.ToDictionary(name => name, name => input.BaselineScalars[name], StringComparer.OrdinalIgnoreCase),
            input.VectorScope.ToDictionary(name => name, name => input.BaselineVectors[name], StringComparer.OrdinalIgnoreCase),
            request.MaterialStrength, seed, input.ScalarBounds.Where(value => input.ScalarScope.Contains(value.Name)).ToArray());
        var families = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (textureNames.Count > 0)
        {
            for (var index = 0; index < input.Profiles.Count; index++)
            {
                var context = input.Profiles[index];
                var requested = ProfileTextureNames(context, textureNames);
                var available = ProfileTextureNames(context, input.Textures.Keys);
                var selected = catalog.SelectMaterialDonor(context.Key, unchecked(seed + index * 7919));
                var donor = context.InstalledDonors.First(value => value.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
                foreach (var family in donor.MaterialTextureFamilies.Where(family =>
                    MaterialTextureFamilyPolicy.IsInScope(family.Value, requested, available)))
                {
                    var members = family.Value.Select(member => new
                        { Name = input.FindControlName(context.ParameterScopeKey, member.Key, MaterialParameterKind.Texture), member.Value })
                        .Where(member => member.Name is not null)
                        .ToDictionary(member => member.Name!, member => member.Value, StringComparer.OrdinalIgnoreCase);
                    if (members.Count > 0) families.TryAdd($"{context.ParameterScopeKey}|{family.Key}", members);
                }
            }
        }
        var scalarScope = NpcNumericScope(input.ScalarScope, request.Player is not null);
        var vectorScope = NpcNumericScope(input.VectorScope, request.Player is not null);
        return await resolve(values.ScalarValues.Where(value => scalarScope.Contains(value.Key))
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase),
            values.VectorValues.Where(value => vectorScope.Contains(value.Key))
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase), families, null);
    }

    private static IReadOnlySet<string> NpcNumericScope(IReadOnlySet<string> names, bool player) => player
        ? names.Where(name => !Player2DaParameters.ContainsKey(MaterialParameterControlKey.ParameterName(name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
        : names;

    private static IReadOnlySet<string> ProfileTextureNames(MaterialRandomisationProfileContext context,
        IEnumerable<string> controls) => controls.Where(name => context.Supports(name, MaterialParameterKind.Texture))
        .Select(MaterialParameterControlKey.ParameterName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static bool WereAllTextureFamiliesRejected(int eligibleSignatureCount,
        int excludedSignatureCount, int appliedTextureFamilies) =>
        eligibleSignatureCount > 0 && excludedSignatureCount >= eligibleSignatureCount && appliedTextureFamilies == 0;

    private async Task<(PreparedMaterialRandomisation Material, bool NoReadableFamily)> PrepareNormalMaterialsAsync(
        EditorRandomisationRequest request, IReadOnlySet<string> requestedTextureNames,
        int seed, string? excludedDonorId, ResolveRandomisationMaterials resolve)
    {
        var input = request.Material;
        var profiles = input.Profiles;
        var scalarScope = NpcNumericScope(input.ScalarScope, request.Player is not null);
        var vectorScope = NpcNumericScope(input.VectorScope, request.Player is not null);
        var noReadableFamily = false;
        var claimedScalars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedVectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedTextureFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PreparedMaterialRandomisation>();

        for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var context = profiles[profileIndex];
            var profile = context.Profile;
            var profileScalarControls = scalarScope
                .Where(name => context.Supports(name, MaterialParameterKind.Scalar))
                .Where(name => profile.Scalars.ContainsKey(MaterialParameterControlKey.ParameterName(name)))
                .Where(claimedScalars.Add)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profileVectorControls = vectorScope
                .Where(name => context.Supports(name, MaterialParameterKind.Vector))
                .Where(name => profile.Vectors.ContainsKey(MaterialParameterControlKey.ParameterName(name)))
                .Where(claimedVectors.Add)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profileScalars = profileScalarControls.Select(MaterialParameterControlKey.ParameterName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profileVectors = profileVectorControls.Select(MaterialParameterControlKey.ParameterName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentScalars = input.Scalars
                .Where(value => context.Supports(value.Key, MaterialParameterKind.Scalar))
                .ToDictionary(value => MaterialParameterControlKey.ParameterName(value.Key),
                    value => value.Value,
                    StringComparer.OrdinalIgnoreCase);
            var currentVectors = input.Vectors
                .Where(value => context.Supports(value.Key, MaterialParameterKind.Vector))
                .ToDictionary(value => MaterialParameterControlKey.ParameterName(value.Key),
                    value => value.Value,
                    StringComparer.OrdinalIgnoreCase);
            var currentTextures = input.Textures
                .Where(value => context.Supports(value.Key, MaterialParameterKind.Texture))
                .ToDictionary(value => MaterialParameterControlKey.ParameterName(value.Key),
                    value => value.Value,
                    StringComparer.OrdinalIgnoreCase);
            var scalarBounds = input.ScalarBounds
                .Where(value => context.Supports(value.Name, MaterialParameterKind.Scalar))
                .Select(value => value with { Name = MaterialParameterControlKey.ParameterName(value.Name) }).ToArray();
            var requestedProfileTextures = ProfileTextureNames(context, requestedTextureNames);
            var availableTextureNames = ProfileTextureNames(context, input.Textures.Keys);
            var installedDonors = context.InstalledDonors;
            var compatibleDonors = excludedDonorId is null
                ? installedDonors
                : installedDonors.Where(value =>
                    !value.Id.Equals(excludedDonorId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (compatibleDonors.Count == 0) compatibleDonors = installedDonors;
            var textureFamilies = compatibleDonors
                .SelectMany(value => value.MaterialTextureFamilies)
                .Where(family => MaterialTextureFamilyPolicy.IsInScope(
                    family.Value, requestedProfileTextures, availableTextureNames))
                .Select(value => value.Key)
                .Where(family => claimedTextureFamilies.Add($"{context.ParameterScopeKey}|{family}"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (profileScalars.Count + profileVectors.Count + textureFamilies.Count == 0) continue;

            var profileSeed = unchecked(seed + profileIndex * 7919);
            var donor = catalog.SelectMaterialDonor(context.Key, profileSeed, excludedDonorId);
            var eligibleSignatureCount = compatibleDonors
                .SelectMany(value => value.MaterialTextureFamilies
                    .Where(family => textureFamilies.Contains(family.Key))
                    .Select(family => family.Value))
                .Select(MaterialRandomiser.TextureFamilySignature)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var excludedSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PreparedMaterialRandomisation? prepared = null;
            for (var attempt = 0; attempt <= eligibleSignatureCount; attempt++)
            {
                var proposal = MaterialRandomiser.CreateProposal(
                    donor, compatibleDonors, profile,
                    currentScalars, currentVectors, scalarBounds,
                    profileScalars, profileVectors, textureFamilies,
                    request.MaterialStrength, profileSeed, currentTextures, excludedSignatures);
                var dependentVectors = MaterialRandomiser.DependentVectorNames(
                    profile.ProfileKey, proposal.TextureFamilies, currentTextures);
                prepared = await resolve(
                    proposal.Scalars.Where(value => profileScalars.Contains(value.Key))
                        .Select(value => new
                        {
                            Name = input.FindControlName(
                                context.ParameterScopeKey, value.Key, MaterialParameterKind.Scalar),
                            value.Value
                        })
                        .Where(value => value.Name is not null)
                        .ToDictionary(value => value.Name!, value => value.Value, StringComparer.OrdinalIgnoreCase),
                    proposal.Vectors.Where(value => profileVectors.Contains(value.Key) ||
                                                     dependentVectors.Contains(value.Key))
                        .Select(value => new
                        {
                            Name = input.FindControlName(
                                context.ParameterScopeKey, value.Key, MaterialParameterKind.Vector),
                            value.Value
                        })
                        .Where(value => value.Name is not null)
                        .ToDictionary(value => value.Name!, value => value.Value, StringComparer.OrdinalIgnoreCase),
                    proposal.TextureFamilies,
                    context.ParameterScopeKey);
                if (prepared.FailedTextureFamilySignatures.Count == 0) break;
                var added = false;
                foreach (var failure in prepared.FailedTextureFamilySignatures)
                    added |= excludedSignatures.Add(failure);
                if (!added) break;
            }
            if (prepared is null) continue;
            results.Add(prepared);
            noReadableFamily |= WereAllTextureFamiliesRejected(
                eligibleSignatureCount, excludedSignatures.Count, prepared.AppliedTextureFamilies);
        }

        return (PreparedMaterialRandomisation.Merge(results.ToArray()) ?? PreparedMaterialRandomisation.Empty, noReadableFamily);
    }

    internal static bool IsPlayerExclusiveTexture(string name) =>
        name.Equals("HED_Addn", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Brow", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Diff", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Norm", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scar", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scalp_Norm", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scalp_Spec", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Tang", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHumanScalpTexture(string parameterName) =>
        parameterName.Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("HED_Scalp_Norm", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("HED_Scalp_Spec", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("HED_Tang", StringComparison.OrdinalIgnoreCase);

    internal static readonly IReadOnlyDictionary<string, Player2DaPalette> Player2DaParameters =
        new Dictionary<string, Player2DaPalette>(StringComparer.OrdinalIgnoreCase)
        {
            ["SkinTone"] = Player2DaPalette.SkinTone,
            ["EYE_White_Colour_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Spec_Add_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Scar_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Teeth_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Hair_Colour_Vector"] = Player2DaPalette.HairColour,
            ["HAIR_Shine_Desaturate_Scalar"] = Player2DaPalette.HairColour,
            ["HED_Scalp_PhongSpec_Scalar"] = Player2DaPalette.HairColour,
            ["Highlight1SpecExp_Scalar"] = Player2DaPalette.HairColour,
            ["Highlight2SpecExp_Scalar"] = Player2DaPalette.HairColour,
            ["Hair_Spec_Aniso_Exp_Scalar"] = Player2DaPalette.HairColour,
            ["HAIR_Spec_Contribution_Scalar"] = Player2DaPalette.HairColour,
            ["HAIR_SPwr_Scalar"] = Player2DaPalette.HairColour,
            ["HED_Addn_Colour_Vector"] = Player2DaPalette.FacialHairColour,
            ["blonde"] = Player2DaPalette.FacialHairColour,
            ["HED_Addn_Colour_02_Scalar"] = Player2DaPalette.FacialHairColour,
            ["HED_Addn_Blowout_Scalar"] = Player2DaPalette.FacialHairColour,
            ["EYE_Iris_Colour_Vector"] = Player2DaPalette.IrisColour,
            ["HED_Blush_Scalar"] = Player2DaPalette.Blush,
            ["HED_Blush_Vector"] = Player2DaPalette.Blush,
            ["HED_EyeShadow_Tint_Scalar"] = Player2DaPalette.EyeMakeup,
            ["HED_EyeShadow_Tint_Vector"] = Player2DaPalette.EyeMakeup,
            ["HED_Brow_Tint_Scalar"] = Player2DaPalette.EyeMakeup,
            ["HED_Brow_Tint_Vector"] = Player2DaPalette.EyeMakeup,
            ["HED_Lash_Diff"] = Player2DaPalette.EyeMakeup,
            ["HED_Lips_Tint_Scalar"] = Player2DaPalette.Lips,
            ["HED_Lips_Tint_Vector"] = Player2DaPalette.Lips,
            ["HED_Addn_SPwr_Lips_Scalar"] = Player2DaPalette.Lips,
            ["HED_Addn_Spec_Lips_Scalar"] = Player2DaPalette.Lips
        };

}
