using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Randomisation;

public enum PlayerHairSex
{
    Male,
    Female
}

public enum PlayerHairMeshAction
{
    /// <summary>Keep the currently selected attachment, used for scalp-only styles under a mesh lock.</summary>
    Keep,
    /// <summary>Remove any existing attachment for an unlocked scalp-only style.</summary>
    Clear,
    /// <summary>Select one of the exact resolved attachment identities.</summary>
    Set
}

/// <summary>A fully resolved, equally weighted Player hairstyle outcome.</summary>
public sealed record PlayerHairStyleOption(
    string Id,
    PlayerHairSex Sex,
    IReadOnlyDictionary<string, string> ScalpTextures,
    PlayerHairMeshAction MeshAction,
    IReadOnlyList<AssetIdentity> MeshVariants,
    IReadOnlyList<string> HairMorphNames,
    float HairMorphValue,
    bool IncludesNoMeshOutcome = false,
    bool HasDedicatedScalpPair = false,
    bool RequiresMeshForScalp = false)
{
    public bool RequiresMeshSelection => MeshAction == PlayerHairMeshAction.Set;
}

/// <summary>
/// Resolves the reviewed Player HMM/HMF scalp and hair inventory against one game's installed
/// base-game and official-DLC MFTR candidates. It intentionally knows no package or UI implementation details.
/// </summary>
public static class PlayerHairStyleCatalog
{
    private const string ScalpDiffuseParameter = "HED_Scalp_Diff";
    private const string ScalpNormalParameter = "HED_Scalp_Norm";
    private const string ScalpMaskParameter = "HED_Scalp_Spec";
    private const string ScalpTangentParameter = "HED_Scalp_Tang";

    private static readonly IReadOnlySet<TextureCatalogGame> AllGames =
        new HashSet<TextureCatalogGame> { TextureCatalogGame.LE1, TextureCatalogGame.LE2, TextureCatalogGame.LE3 };
    private static readonly IReadOnlySet<TextureCatalogGame> Le12 =
        new HashSet<TextureCatalogGame> { TextureCatalogGame.LE1, TextureCatalogGame.LE2 };
    private static readonly IReadOnlySet<TextureCatalogGame> Le23 =
        new HashSet<TextureCatalogGame> { TextureCatalogGame.LE2, TextureCatalogGame.LE3 };
    private static readonly IReadOnlySet<TextureCatalogGame> Le1 = new HashSet<TextureCatalogGame> { TextureCatalogGame.LE1 };
    private static readonly IReadOnlySet<TextureCatalogGame> Le3 = new HashSet<TextureCatalogGame> { TextureCatalogGame.LE3 };

    private static readonly StyleDefinition[] HmmStyles = CreateHmmStyles();
    private static readonly StyleDefinition[] HmfStyles = CreateHmfStyles();

    /// <summary>
    /// Returns resolvable styles in stable catalogue order. Every returned element is one pool
    /// entry; mesh variants inside an entry do not add weight. A mesh lock removes outcomes that
    /// require changing the attachment, while retaining independently selectable scalp styles.
    /// </summary>
    public static IReadOnlyList<PlayerHairStyleOption> Resolve(
        TextureCatalogGame game,
        PlayerHairSex sex,
        IEnumerable<TextureCatalogCandidate> textures,
        IEnumerable<AttachmentMeshCandidate> meshes,
        bool meshLocked = false)
    {
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(meshes);
        if (!Enum.IsDefined(game)) throw new ArgumentOutOfRangeException(nameof(game));
        if (!Enum.IsDefined(sex)) throw new ArgumentOutOfRangeException(nameof(sex));

        var gameTextures = textures.Where(value => value.Game == game).ToArray();
        var gameMeshes = meshes.ToArray();
        var definitions = sex == PlayerHairSex.Male ? HmmStyles : HmfStyles;
        var resolvedScalps = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PlayerHairStyleOption>();
        foreach (var definition in definitions)
        {
            if (!definition.Games.Contains(game)) continue;
            if (meshLocked && definition.MeshNames.Count > 0) continue;

            IReadOnlyDictionary<string, string>? scalp = null;
            if (definition.Scalp is not null)
            {
                if (!resolvedScalps.TryGetValue(definition.Scalp.CacheKey, out scalp))
                {
                    scalp = ResolveScalp(gameTextures, definition.Scalp);
                    resolvedScalps.Add(definition.Scalp.CacheKey, scalp);
                }
                if (scalp is null) continue;
            }

            var meshVariants = definition.MeshNames.Count == 0
                ? Array.Empty<AssetIdentity>()
                : ResolveMeshes(gameMeshes, definition.MeshNames);
            if (definition.MeshNames.Count > 0 && meshVariants.Length == 0) continue;

            result.Add(new PlayerHairStyleOption(
                definition.Id,
                sex,
                scalp ?? EmptyScalp,
                definition.MeshNames.Count > 0
                    ? PlayerHairMeshAction.Set
                    : meshLocked ? PlayerHairMeshAction.Keep : PlayerHairMeshAction.Clear,
                meshVariants,
                definition.HairMorphNames,
                definition.HairMorphNames.Count > 0 ? 1f : 0f,
                definition.IncludesNoMeshOutcome,
                definition.MeshNames.Count > 0 && definition.Scalp is not null &&
                    !definition.Scalp.Stem.Equals("Cru", StringComparison.OrdinalIgnoreCase),
                definition.MeshNames.Count > 0 && definition.Scalp is not null &&
                    !definition.Scalp.Stem.Equals("Cru", StringComparison.OrdinalIgnoreCase) &&
                    !definition.IncludesNoMeshOutcome));
        }

        return result;
    }

    /// <summary>
    /// Returns true when the current scalp diffuse resolves to a reviewed style whose matching
    /// mesh is also installed for this game and sex. Used to preserve both sides of a paired
    /// style when hair-mesh randomisation is locked.
    /// </summary>
    public static bool IsCurrentScalpPairedStyle(
        TextureCatalogGame game,
        PlayerHairSex sex,
        IEnumerable<TextureCatalogCandidate> textures,
        IEnumerable<AttachmentMeshCandidate> meshes,
        string? currentScalpDiffusePath,
        AssetIdentity? currentMeshIdentity)
    {
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(meshes);
        if (!Enum.IsDefined(game)) throw new ArgumentOutOfRangeException(nameof(game));
        if (!Enum.IsDefined(sex)) throw new ArgumentOutOfRangeException(nameof(sex));
        if (string.IsNullOrWhiteSpace(currentScalpDiffusePath) || currentMeshIdentity is null) return false;

        var gameTextures = textures.Where(value => value.Game == game).ToArray();
        var gameMeshes = meshes.ToArray();
        var definitions = sex == PlayerHairSex.Male ? HmmStyles : HmfStyles;
        foreach (var definition in definitions)
        {
            if (!definition.Games.Contains(game) || definition.Scalp is null || definition.MeshNames.Count == 0)
                continue;
            var scalp = ResolveScalp(gameTextures, definition.Scalp);
            var pairedMeshes = ResolveMeshes(gameMeshes, definition.MeshNames);
            if (scalp is null || !pairedMeshes.Any(mesh => IdentityMatches(mesh, currentMeshIdentity))) continue;
            var resolvedPath = scalp[ScalpDiffuseParameter];
            if (resolvedPath.Equals(currentScalpDiffusePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Identifies a manually selected mesh as one of the reviewed paired styles. Match its
    /// canonical instance path because an installed override can live in a different package
    /// and export slot than the built-in donor occurrence. This is prompt classification only;
    /// it does not change the selected mesh identity or make the override a randomisation donor.
    /// </summary>
    public static bool MatchesDedicatedScalpPair(
        PlayerHairStyleOption style,
        AssetIdentity meshIdentity)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(meshIdentity);
        if (!style.HasDedicatedScalpPair) return false;

        var selectedPath = TextureCatalogPicker.CanonicalPath(
            meshIdentity.InstancedPath, meshIdentity.PackagePath);
        return style.MeshVariants.Any(variant =>
            TextureCatalogPicker.CanonicalPath(variant.InstancedPath, variant.PackagePath)
                .Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IdentityMatches(AssetIdentity left, AssetIdentity right) =>
        left.UIndex == right.UIndex &&
        left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase) &&
        left.PackagePath.Equals(right.PackagePath, StringComparison.OrdinalIgnoreCase) &&
        TextureCatalogPicker.CanonicalPath(left.InstancedPath, left.PackagePath)
            .Equals(TextureCatalogPicker.CanonicalPath(right.InstancedPath, right.PackagePath),
                StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> EmptyScalp =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string>? ResolveScalp(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        ScalpDefinition scalp)
    {
        var diffuse = ResolveTexture(candidates, scalp.DiffuseNames);
        var normal = ResolveTexture(candidates,
            scalp.NormalNames ?? ComponentNames(scalp.Prefix, scalp.Stem, "Norm"));
        if (diffuse is null || normal is null) return null;

        var mask = ResolveTexture(candidates, scalp.MaskNames ?? ComponentNames(scalp.Prefix, scalp.Stem, "Mask")) ??
                   ResolveTexture(candidates, ["GBL_ARM_ALL_Black"]);
        var tangent = ResolveTexture(candidates, scalp.TangentNames ?? ComponentNames(scalp.Prefix, scalp.Stem, "Tang")) ??
                      ResolveTexture(candidates, ["GBL_ARM_ALL_Norm"]);
        if (mask is null || tangent is null) return null;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ScalpDiffuseParameter] = diffuse,
            [ScalpNormalParameter] = normal,
            [ScalpMaskParameter] = mask,
            [ScalpTangentParameter] = tangent
        };
    }

    private static string? ResolveTexture(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        IReadOnlyList<string> objectNames)
    {
        foreach (var name in objectNames)
        {
            var hits = candidates
                .SelectMany(candidate => BaseGameTextureOccurrences(candidate)
                    .Select(occurrence => (Candidate: candidate, Occurrence: occurrence)))
                .Where(value => ObjectName(value.Candidate.InstancedPath)
                    .Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(value => TextureCatalogPicker.CanonicalPath(
                    value.Candidate.InstancedPath, value.Occurrence.PackagePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            // Duplicate object names at different UE3 paths are ambiguous without package
            // evidence in the reviewed inventory, so fail closed instead of guessing.
            if (hits.Length == 1) return hits[0];
            if (hits.Length > 1) return null;
        }
        return null;
    }

    private static TextureCatalogOccurrence[] BaseGameTextureOccurrences(TextureCatalogCandidate candidate) =>
        candidate.Occurrences.Count == 0
            ? IsCoreGameOrigin(candidate.EffectiveOccurrence.Origin)
                ? [candidate.EffectiveOccurrence]
                : []
            : candidate.Occurrences.Where(value => IsCoreGameOrigin(value.Origin)).ToArray();

    private static AssetIdentity[] ResolveMeshes(
        IReadOnlyList<AttachmentMeshCandidate> candidates,
        IReadOnlyList<IReadOnlyList<string>> aliasGroups)
    {
        foreach (var aliasGroup in aliasGroups)
        {
            var matches = candidates
                .SelectMany(candidate => candidate.Occurrences
                    .Where(value => IsCoreGameOrigin(value.Origin) &&
                                    aliasGroup.Any(alias => ObjectName(value.InstancedPath)
                                        .Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    .Select(occurrence => new AssetIdentity(
                        occurrence.PackagePath,
                        TextureCatalogPicker.CanonicalPath(
                            occurrence.InstancedPath, occurrence.PackagePath),
                        occurrence.ExportUIndex,
                        "SkeletalMesh")))
                .GroupBy(value => (value.PackagePath, value.InstancedPath, value.UIndex),
                    AssetIdentityKeyComparer.Instance)
                .Select(group => group.First())
                .OrderBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matches.Length > 0) return matches;
        }
        return [];
    }

    private static string ObjectName(string path) => path[(path.LastIndexOf('.') + 1)..];

    private static bool IsCoreGameOrigin(TextureCatalogOrigin origin) =>
        origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc;

    private static IReadOnlyList<string> ComponentNames(string prefix, string stem, string component) =>
        [$"{prefix}_HIR_{stem}_{component}"];

    private static IReadOnlyList<string> ScalpComponentNames(string prefix, string stem, string component) =>
        [$"{prefix}_HIR_{stem}_Scalp_{component}", $"{prefix}_HIR_{stem}_{component}"];

    private static ScalpDefinition HmScalp(string sexPrefix, string stem) => new(
        $"{sexPrefix}:{stem}",
        sexPrefix,
        stem,
        PairScalpComponentNames(sexPrefix, stem, "Diff"),
        stem.Equals("Cru", StringComparison.OrdinalIgnoreCase)
            ? ["HMM_HED_PROBase_Scalp_Norm_Stack"]
            : PairScalpComponentNames(sexPrefix, stem, "Norm"),
        PairScalpComponentNames(sexPrefix, stem, "Mask"),
        PairScalpComponentNames(sexPrefix, stem, "Tang"));

    private static IReadOnlyList<string> PairScalpComponentNames(string prefix, string stem, string component)
    {
        if (stem.Equals("Mhk", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PROCustomBraids01", StringComparison.OrdinalIgnoreCase))
        {
            return component.Equals("Tang", StringComparison.OrdinalIgnoreCase)
                ? []
                : [$"{prefix}_HIR_{stem}_Scalp_{component}"];
        }

        return ScalpComponentNames(prefix, stem, component);
    }

    private static ScalpDefinition HmfBaldScalp() => new(
        "HMF:bald",
        "HMF",
        "PROBase_Scalp_Bald",
        ["HMF_HED_PROBase_Scalp_Bald_Diff"],
        ["HMF_HED_PROBase_Scalp_Norm"],
        ["HMF_HED_PROBase_Scalp_Mask"],
        ["HMF_HED_PROBase_Scalp_Tang"]);

    private static ScalpDefinition HmfScalpAliases(string key, string stem, params string[] aliases) => new(
        $"HMF:{key}",
        "HMF",
        stem,
        aliases.Select(value => $"HMF_HIR_{value}_Scalp_Diff")
            .Concat(aliases.Select(value => $"HMF_HIR_{value}_Diff")).ToArray(),
        key.Equals("scp-cte", StringComparison.OrdinalIgnoreCase)
            ? ["HMF_HED_PROBase_Scalp_Norm"]
            : key.Equals("cru", StringComparison.OrdinalIgnoreCase)
                ? aliases.Select(value => $"HMF_HIR_{value}_Scalp_Norm")
                    .Concat(aliases.Select(value => $"HMF_HIR_{value}_Norm"))
                    .Append("HMF_HED_PROBase_Scalp_Norm").ToArray()
            : aliases.Select(value => $"HMF_HIR_{value}_Scalp_Norm")
                .Concat(aliases.Select(value => $"HMF_HIR_{value}_Norm")).ToArray(),
        aliases.Select(value => $"HMF_HIR_{value}_Scalp_Mask")
            .Concat(aliases.Select(value => $"HMF_HIR_{value}_Mask")).ToArray(),
        aliases.Select(value => $"HMF_HIR_{value}_Scalp_Tang")
            .Concat(aliases.Select(value => $"HMF_HIR_{value}_Tang")).ToArray());

    private static ScalpDefinition HmfMomScalp() => new(
        "HMF:mom-scalp",
        "HMF",
        "Mom_Scalp",
        ["HMF_HIR_Mom_Scalp_Diff"],
        ["HMF_HIR_Mom_Scalp_Norm"],
        [],
        []);

    private static ScalpDefinition HmfPairedScalp(string stem) => new(
        $"HMF:{stem}",
        "HMF",
        stem,
        [$"HMF_HIR_{stem}_Scalp_Diff"],
        [$"HMF_HIR_{stem}_Scalp_Norm"],
        [$"HMF_HIR_{stem}_Scalp_Mask"],
        []);

    private static IReadOnlyList<IReadOnlyList<string>> MeshAliases(string sexPrefix, params string[] stems) =>
        stems.SelectMany(stem => new[]
            {
                $"{sexPrefix}_HIR_{stem}_MDL",
                $"{sexPrefix}_HIR_{stem}"
            })
            .Select(name => (IReadOnlyList<string>)[name])
            .ToArray();

    private static IReadOnlyList<IReadOnlyList<string>> MeshVariantGroup(string sexPrefix, params string[] stems) =>
        [stems.SelectMany(stem => new[]
            {
                $"{sexPrefix}_HIR_{stem}_MDL",
                $"{sexPrefix}_HIR_{stem}"
            }).ToArray()];

    private static StyleDefinition[] CreateHmmStyles()
    {
        var result = new List<StyleDefinition>();
        void Scalp(string id, ScalpDefinition scalp, string[]? morphs = null,
            IReadOnlyList<IReadOnlyList<string>>? meshes = null,
            IReadOnlySet<TextureCatalogGame>? games = null,
            bool includesNoMeshOutcome = false) => result.Add(new StyleDefinition(
                id, games ?? AllGames, scalp, meshes ?? Array.Empty<IReadOnlyList<string>>(), morphs ?? [],
                includesNoMeshOutcome));
        void MeshOnly(string id, string[] meshes, IReadOnlySet<TextureCatalogGame>? games = null) =>
            Scalp(id, HmScalp("HMM", "Cru"), meshes: [meshes], games: games);

        Scalp("hmm-afr", HmScalp("HMM", "Afr"), ["Afro", "deiter"]);
        Scalp("hmm-cru", HmScalp("HMM", "Cru"));
        Scalp("hmm-for", HmScalp("HMM", "For"), ["flatTop"]);
        Scalp("hmm-gez", HmScalp("HMM", "Gez"), ["Geezer"]);
        Scalp("hmm-mhk", HmScalp("HMM", "Mhk"), meshes: [MeshAliases("HMM", "Mhk")[0].ToArray()]);
        Scalp("hmm-braids", HmScalp("HMM", "PROCustomBraids01"),
            meshes: [MeshAliases("HMM", "PROCustomBraids01")[0].ToArray()]);
        foreach (var fade in new[] { "PROCustomFade01", "PROCustomFade02", "PROCustomFade03" })
            Scalp($"hmm-{fade.ToLowerInvariant()}", HmScalp("HMM", fade));
        Scalp("hmm-short-afro", HmScalp("HMM", "PROCustomShortAfro"),
            meshes: [MeshAliases("HMM", "PROCustomShortAfro")[0].ToArray()]);
        Scalp("hmm-rol", HmScalp("HMM", "Rol"), ["rollins"]);
        Scalp("hmm-sar-ssk", HmScalp("HMM", "Sar"), ["flatTop"],
            meshes: [MeshVariantGroup("HMM", "Ssk_01", "Ssk_02")[0]], includesNoMeshOutcome: true);
        Scalp("hmm-sar", HmScalp("HMM", "Sar"));
        Scalp("hmm-short-scalp", HmScalp("HMM", "Short_Scalp"));
        Scalp("hmm-slk", HmScalp("HMM", "Slk"));
        Scalp("hmm-spa", HmScalp("HMM", "Spa"), ["widowsPeak"]);
        Scalp("hmm-wil", HmScalp("HMM", "Wil"), ["Willis"]);
        Scalp("hmm-wls", HmScalp("HMM", "Wls"));
        MeshOnly("hmm-fsk", ["HMM_HIR_Fsk_MDL", "HMM_HIR_Fsk"]);
        MeshOnly("hmm-proshort01", ["HMM_HIR_PROShort01_MDL", "HMM_HIR_PROShort01"], Le1);
        MeshOnly("hmm-proshort02", ["HMM_HIR_PROShort02_MDL", "HMM_HIR_PROShort02"]);
        MeshOnly("hmm-proshort03", ["HMM_HIR_PROShort03_MDL", "HMM_HIR_PROShort03"]);
        MeshOnly("hmm-rsk", ["HMM_HIR_Rsk_MDL", "HMM_HIR_Rsk"]);
        return result.ToArray();
    }

    private static StyleDefinition[] CreateHmfStyles()
    {
        var result = new List<StyleDefinition>();
        void Scalp(string id, ScalpDefinition scalp, IReadOnlyList<IReadOnlyList<string>>? meshes = null,
            IReadOnlySet<TextureCatalogGame>? games = null) => result.Add(new StyleDefinition(
                id, games ?? AllGames, scalp, meshes ?? Array.Empty<IReadOnlyList<string>>(), []));
        void MeshOnly(string id, string[] aliases, IReadOnlySet<TextureCatalogGame>? games = null) =>
            Scalp(id, HmfScalpAliases("cru", "Cru", "Cru"), OrderedAliasGroups(aliases), games);
        void Mesh(string id, string[] aliases, IReadOnlySet<TextureCatalogGame>? games = null) =>
            MeshOnly(id, aliases, games);

        Scalp("hmf-bald", HmfBaldScalp());
        Scalp("hmf-braids", HmfPairedScalp("PROCustomBraids01"),
            [MeshVariantGroup("HMF", "PROCustomBraids01")[0]]);
        Scalp("hmf-cru", HmfScalpAliases("cru", "Cru", "Cru"));
        foreach (var fade in new[] { "PROCustomFade01", "PROCustomFade02", "PROCustomFade03" })
            Scalp($"hmf-{fade.ToLowerInvariant()}", HmfScalpAliases(fade, fade, fade));
        Scalp("hmf-mhk", HmfPairedScalp("Mhk"),
            [MeshAliases("HMF", "Mhk")[0].ToArray()]);
        Scalp("hmf-mom-scalp", HmfMomScalp(), games: Le12);
        Scalp("hmf-short-afro", HmfScalpAliases("short-afro", "PROCustomShortAfro", "PROCustomShortAfro"),
            [MeshVariantGroup("HMF", "PROCustomShortAfro")[0]]);
        Scalp("hmf-sar", HmfScalpAliases("sar", "Sar", "Sar"));
        Scalp("hmf-scp-cte", HmfScalpAliases("scp-cte", "SCP_CUSTOM_Cte", "SCP_CUSTOM_Cte", "SCP_Pll"));
        Scalp("hmf-scp-pll02", HmfScalpAliases("scp-pll02", "SCP_Pll02", "SCP_Pll02"));
        Scalp("hmf-shepard-scalp", HmfScalpAliases("shepard-scalp", "Shepard", "Shepard"));

        MeshOnly("hmf-afr-mesh", ["HMF_HIR_PROCustom_Afr_MDL", "HMF_HIR_PROCustom_Afr", "HMF_HIR_Afr_MDL", "HMF_HIR_Afr"]);
        Mesh("hmf-cls", ["HMF_HIR_Cls_MDL", "HMF_HIR_Cls"]);
        MeshOnly("hmf-cte-mesh", ["HMF_HIR_PROCustom_Cute_MDL", "HMF_HIR_PROCustom_Cute", "HMF_HIR_Cte_MDL", "HMF_HIR_Cte"]);
        Mesh("hmf-cyb", ["HMF_HIR_Cyb_MDL", "HMF_HIR_Cyb"]);
        Mesh("hmf-ftl-long-hair", ["HMF_HIR_FTL_LongHair_MDL", "HMF_HIR_FTL_LongHair"], Le3);
        Mesh("hmf-hbn", ["HMF_HIR_Hbn_MDL", "HMF_HIR_Hbn"]);
        Mesh("hmf-ibun", ["HMF_HIR_IBun_MDL", "HMF_HIR_IBun"]);
        Mesh("hmf-lbn", ["HMF_HIR_Lbn_MDL", "HMF_HIR_Lbn"]);
        Mesh("hmf-mbn", ["HMF_HIR_Mbn_MDL", "HMF_HIR_Mbn"]);
        Mesh("hmf-mir", ["HMF_HIR_MIR_MDL", "HMF_HIR_MIR", "HMF_HIR_PROMiranda_MDL",
            "HMF_HIR_PROMiranda"], Le23);
        Mesh("hmf-mom", ["HMF_HIR_Mom_MDL", "HMF_HIR_Mom"]);
        Mesh("hmf-ptl", ["HMF_HIR_Ptl_MDL", "HMF_HIR_Ptl"]);
        Mesh("hmf-pulled", ["HMF_HIR_Pulled_MDL", "HMF_HIR_Pulled"]);
        Mesh("hmf-pro-ashley", ["HMF_HIR_PROAshley_MDL", "HMF_HIR_PROAshley"]);
        Mesh("hmf-short01", ["HMF_HIR_PROCustomShort01_MDL", "HMF_HIR_PROCustomShort01",
            "HMF_HIR_PROCustom_Short01_MDL", "HMF_HIR_PROCustom_Short01"]);
        Mesh("hmf-short02", ["HMF_HIR_PROCustomShort02_MDL", "HMF_HIR_PROCustomShort02",
            "HMF_HIR_PROCustom_Short02_MDL", "HMF_HIR_PROCustom_Short02"]);
        Mesh("hmf-eva", ["HMF_HIR_PROEva_MDL", "HMF_HIR_PROEva"], Le3);
        Mesh("hmf-jessica", ["HMF_HIR_PROJessica_MDL", "HMF_HIR_PROJessica"], Le3);
        Mesh("hmf-jack", ["HMF_HIR_PROJack_MDL", "HMF_HIR_PROJack"], Le3);
        Mesh("hmf-shepard-mesh", ["HMF_HIR_PROShepard_MDL", "HMF_HIR_PROShepard"]);
        Mesh("hmf-short", ["HMF_HIR_PROShort_MDL", "HMF_HIR_PROShort"]);
        MeshOnly("hmf-sxy-mesh", ["HMF_HIR_PROCustom_Sexy_MDL", "HMF_HIR_PROCustom_Sexy", "HMF_HIR_Sxy_MDL", "HMF_HIR_Sxy"]);
        return result.ToArray();
    }

    private sealed record ScalpDefinition(
        string CacheKey,
        string Prefix,
        string Stem,
        IReadOnlyList<string> DiffuseNames,
        IReadOnlyList<string>? NormalNames,
        IReadOnlyList<string>? MaskNames = null,
        IReadOnlyList<string>? TangentNames = null);

    private static IReadOnlyList<IReadOnlyList<string>> OrderedAliasGroups(string[] aliases) =>
        aliases.Select(alias => (IReadOnlyList<string>)[alias]).ToArray();

    private sealed record StyleDefinition(
        string Id,
        IReadOnlySet<TextureCatalogGame> Games,
        ScalpDefinition? Scalp,
        IReadOnlyList<IReadOnlyList<string>> MeshNames,
        IReadOnlyList<string> HairMorphNames,
        bool IncludesNoMeshOutcome = false);

    private sealed class AssetIdentityKeyComparer : IEqualityComparer<(string PackagePath, string InstancedPath, int UIndex)>
    {
        public static AssetIdentityKeyComparer Instance { get; } = new();
        public bool Equals((string PackagePath, string InstancedPath, int UIndex) x,
            (string PackagePath, string InstancedPath, int UIndex) y) =>
            x.UIndex == y.UIndex &&
            x.PackagePath.Equals(y.PackagePath, StringComparison.OrdinalIgnoreCase) &&
            x.InstancedPath.Equals(y.InstancedPath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string PackagePath, string InstancedPath, int UIndex) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.PackagePath),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.InstancedPath), value.UIndex);
    }
}
