using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.DataCompiler;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using System.Numerics;

namespace MorphFaceEditor.Tests;

/// <summary>
/// Protects runtime randomisation semantics separately from corpus compiler/tooling contracts.
/// The split keeps expensive phase tooling out of the default edit-feedback loop.
/// </summary>
public static class RandomisationTests
{
    public static IReadOnlyList<TestCase> Runtime { get; } =
    [
        new("randomisation pools follow approved game and species grouping", PoolsFollowApprovedGrouping),
        new("zero strength reproduces donor and preserves its zero mask", ZeroStrengthReproducesDonor),
        new("strength expands nonzero donors toward metadata bounds", StrengthExpandsTowardBounds),
        new("subcategory randomisation leaves values outside its scope unchanged", ScopeLeavesOtherValuesUnchanged),
        new("randomisation rejects invalid strength and metadata bounds", InvalidInputsAreRejected),
        new("material zero strength reproduces the donor including texture families", MaterialZeroStrengthReproducesDonor),
        new("material scalars use safe and experimental envelopes", MaterialScalarsUseTwoEnvelopes),
        new("material colours interpolate perceptually while selectors stay discrete", MaterialVectorsRespectSemantics),
        new("material safety policy constrains or excludes hazardous numeric parameters", MaterialSafetyPolicyProtectsNumericParameters),
        new("material texture selection balances variants and rejects unsafe families", MaterialTextureSelectionIsCurated),
        new("texture-dependent selector rules remain valid", TextureDependentSelectorsRemainValid),
        new("LE1 Batarian material randomisation falls back to compatible pooled donors", Le1BatarianUsesCompatibleMaterialDonors),
        new("material donor compatibility follows cross-game species and human pools", MaterialDonorsUseApprovedCrossGamePools),
        new("cursed randomisation wakes zero morphs within Mgamerz ranges", CursedRandomisationWakesZeroMorphs),
        new("cursed randomisation fuzzes only Mgamerz facial bones and numeric materials", CursedRandomisationFuzzesBonesAndMaterials),
        new("zero-strength cursed randomisation is an exact no-op", ZeroStrengthCursedRandomisationIsNoOp),
        new("cursed randomisation keeps extreme finite inputs finite", CursedRandomisationKeepsExtremeInputsFinite),
        new("feature batches evaluate once and undo as one exact edit", FeatureBatchIsOneExactEdit),
        new("invalid feature batches do not partially mutate the session", InvalidFeatureBatchIsAtomic),
        new("unchanged feature batches create no history", UnchangedFeatureBatchCreatesNoHistory)
    ];

    public static IReadOnlyList<TestCase> Tooling { get; } =
    [
        new("randomisation bundle round-trips deterministically", BundleRoundTripsDeterministically),
        new("corpus compilation distinguishes unavailable zero and nonzero features", CompilationPreservesFeatureStates),
        new("corpus compilation reports and excludes empty and reviewed donors", CompilationReportsExclusions),
        new("corpus compilation classifies material vectors and atomic texture families", CompilationClassifiesMaterials),
        new("default compiler profiles expose all approved donor pools", DefaultDefinitionsExposeApprovedPools)
    ];

    private static void PoolsFollowApprovedGrouping()
    {
        TestAssert.Equal(MorphRandomisationPoolKey.HumanMaleLe12,
            MorphRandomisationPoolRouter.Resolve("le1-human-male"));
        TestAssert.Equal(MorphRandomisationPoolKey.HumanMaleLe12,
            MorphRandomisationPoolRouter.Resolve("le2-human-male"));
        TestAssert.Equal(MorphRandomisationPoolKey.HumanMaleLe3,
            MorphRandomisationPoolRouter.Resolve("le3-human-male"));
        TestAssert.Equal(MorphRandomisationPoolKey.HumanFemaleLe12,
            MorphRandomisationPoolRouter.Resolve("le2-human-female"));
        TestAssert.Equal(MorphRandomisationPoolKey.HumanFemaleLe3,
            MorphRandomisationPoolRouter.Resolve("le3-human-female"));
        TestAssert.Equal(MorphRandomisationPoolKey.Asari,
            MorphRandomisationPoolRouter.Resolve("le1-asari"));
        TestAssert.Equal(MorphRandomisationPoolKey.Asari,
            MorphRandomisationPoolRouter.Resolve("le3-asari"));
        TestAssert.Equal(MorphRandomisationPoolKey.Salarian,
            MorphRandomisationPoolRouter.Resolve("le2-salarian"));
        TestAssert.Equal(MorphRandomisationPoolKey.Turian,
            MorphRandomisationPoolRouter.Resolve("le3-turian"));
        TestAssert.Equal(MorphRandomisationPoolKey.Krogan,
            MorphRandomisationPoolRouter.Resolve("le1-krogan"));
        TestAssert.Equal(MorphRandomisationPoolKey.Batarian,
            MorphRandomisationPoolRouter.Resolve("le3-batarian"));
    }

    private static void ZeroStrengthReproducesDonor()
    {
        var corpus = Corpus(new MorphRandomisationDonor(
            "LE2.HMM.Seed",
            "le2-human-male",
            new HashSet<string>(["Eyes", "Nose", "Jaw"], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Eyes"] = 0.75f,
                ["Jaw"] = 0.25f
            }));
        var current = Values(("Eyes", 0.1f), ("Nose", 0.9f), ("Jaw", 0.8f));
        var bounds = Bounds("Eyes", "Nose", "Jaw");

        var result = MorphRandomiser.CreateProposal(
            corpus, MorphRandomisationPoolKey.HumanMaleLe12, current, bounds,
            new HashSet<string>(current.Keys, StringComparer.OrdinalIgnoreCase), 0, 17);

        TestAssert.Equal("LE2.HMM.Seed", result.DonorId);
        TestAssert.Near(0.75f, result.Values["Eyes"], 0);
        TestAssert.Near(0, result.Values["Nose"], 0);
        TestAssert.Near(0.25f, result.Values["Jaw"], 0);
    }

    private static void StrengthExpandsTowardBounds()
    {
        var corpus = Corpus(new MorphRandomisationDonor(
            "Seed",
            "le1-human-male",
            new HashSet<string>(["Target"], StringComparer.OrdinalIgnoreCase),
            Values(("Target", 0.8f))));
        var current = Values(("Target", 0.2f));
        var bounds = new[] { new MorphRandomisationFeatureBounds("Target", 0, 1) };
        var scope = new HashSet<string>(["Target"], StringComparer.OrdinalIgnoreCase);

        var full = MorphRandomiser.CreateProposal(
            corpus, MorphRandomisationPoolKey.HumanMaleLe12, current, bounds, scope, 100, 91);
        var half = MorphRandomiser.CreateProposal(
            corpus, MorphRandomisationPoolKey.HumanMaleLe12, current, bounds, scope, 50, 91);

        TestAssert.True(full.Values["Target"] is >= 0 and <= 1,
            "Full-strength value escaped its normal metadata range.");
        TestAssert.Near(0.8f + ((full.Values["Target"] - 0.8f) * 0.5f),
            half.Values["Target"], 0.000001f);
    }

    private static void ScopeLeavesOtherValuesUnchanged()
    {
        var donor = new MorphRandomisationDonor(
            "Seed", "le3-turian",
            new HashSet<string>(["Bridge", "Eyes"], StringComparer.OrdinalIgnoreCase),
            Values(("Bridge", 0.6f), ("Eyes", 0.9f)));
        var current = Values(("Bridge", 0.1f), ("Eyes", 0.2f));

        var result = MorphRandomiser.CreateProposal(
            Corpus(donor, MorphRandomisationPoolKey.Turian), MorphRandomisationPoolKey.Turian,
            current, Bounds("Bridge", "Eyes"),
            new HashSet<string>(["Bridge"], StringComparer.OrdinalIgnoreCase), 0, 3);

        TestAssert.Near(0.6f, result.Values["Bridge"], 0);
        TestAssert.Near(0.2f, result.Values["Eyes"], 0);
    }

    private static void InvalidInputsAreRejected()
    {
        var corpus = Corpus(new MorphRandomisationDonor(
            "Seed", "le1-human-male",
            new HashSet<string>(["Target"], StringComparer.OrdinalIgnoreCase),
            Values(("Target", 0.5f))));
        var current = Values(("Target", 0));
        var scope = new HashSet<string>(["Target"], StringComparer.OrdinalIgnoreCase);

        AssertThrows<ArgumentOutOfRangeException>(() => MorphRandomiser.CreateProposal(
            corpus, MorphRandomisationPoolKey.HumanMaleLe12, current, Bounds("Target"), scope, 101, 1));
        AssertThrows<ArgumentException>(() => MorphRandomiser.CreateProposal(
            corpus, MorphRandomisationPoolKey.HumanMaleLe12, current,
            [new MorphRandomisationFeatureBounds("Target", 2, 1)], scope, 50, 1));
    }

    private static void MaterialZeroStrengthReproducesDonor()
    {
        var donor = MaterialDonor();
        var proposal = MaterialRandomiser.CreateProposal(
            donor, [donor], MaterialProfile(),
            Values(("Roughness", 9)),
            new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.Zero, ["Mask"] = Vector4.Zero },
            [new MaterialRandomisationScalarBounds("Roughness", 0, 10)],
            new HashSet<string>(["Roughness"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["SkinTone", "Mask"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["face"], StringComparer.OrdinalIgnoreCase),
            0, 44);

        TestAssert.Near(4, proposal.Scalars["Roughness"], 0);
        TestAssert.Equal(new Vector4(0.4f, 0.2f, 0.1f, 1), proposal.Vectors["SkinTone"]);
        TestAssert.Equal(Vector4.UnitX, proposal.Vectors["Mask"]);
        TestAssert.Equal("HMM_Face_Diff", proposal.TextureFamilies["face"]["HED_Diff"]);
    }

    private static void MaterialScalarsUseTwoEnvelopes()
    {
        var donor = MaterialDonor();
        var bounds = new[] { new MaterialRandomisationScalarBounds("Roughness", 0, 10) };
        var scope = new HashSet<string>(["Roughness"], StringComparer.OrdinalIgnoreCase);
        MaterialRandomisationProposal Sample(int strength) => MaterialRandomiser.CreateProposal(
            donor, [donor], MaterialProfile(), Values(("Roughness", 0)),
            new Dictionary<string, Vector4>(), bounds, scope,
            new HashSet<string>(), new HashSet<string>(), strength, 12);

        var safe = Sample(50).Scalars["Roughness"];
        var experimental = Sample(100).Scalars["Roughness"];
        TestAssert.True(safe is >= 2 and <= 6, "The 50% scalar escaped its audited P10/P90 envelope.");
        TestAssert.True(experimental is >= 0 and <= 10, "The 100% scalar escaped its metadata bounds.");
    }

    private static void MaterialVectorsRespectSemantics()
    {
        var donor = MaterialDonor();
        var alternate = donor with
        {
            Id = "Alternate",
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["SkinTone"] = new(0.8f, 0.6f, 0.4f, 1),
                ["Mask"] = Vector4.UnitY
            }
        };
        var current = new Dictionary<string, Vector4>
        {
            ["SkinTone"] = Vector4.Zero,
            ["Mask"] = Vector4.Zero
        };
        var scope = new HashSet<string>(current.Keys, StringComparer.OrdinalIgnoreCase);
        var proposal = MaterialRandomiser.CreateProposal(
            donor, [alternate], MaterialProfile(), Values(), current, [],
            new HashSet<string>(), scope, new HashSet<string>(), 100, 1);

        var colour = proposal.Vectors["SkinTone"];
        TestAssert.True(colour.X is >= 0.4f and <= 0.9f && colour.Y is >= 0.2f and <= 0.7f &&
                        colour.Z is >= 0.1f and <= 0.5f,
            "Perceptual colour escaped the audited experimental envelope.");
        TestAssert.True(proposal.Vectors["Mask"] is var selector &&
                        (selector == Vector4.UnitX || selector == Vector4.UnitY || selector == Vector4.UnitZ),
            "Selector randomisation invented a non-canonical channel state.");
    }

    private static void MaterialSafetyPolicyProtectsNumericParameters()
    {
        var hmmDonor = PolicyDonor("le3-human-male",
            ("U_Offset", 0.0642f), ("Emis_Scalar", 1), ("HED_Addn_Add_Scalar", 1));
        hmmDonor = hmmDonor with
        {
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["Emis_Color"] = new(1, 0, 0, 1)
            }
        };
        var hmmProfile = PolicyProfile("le3-human-male",
            [("U_Offset", -2, 2, 0.06f, 0.07f), ("Emis_Scalar", 0, 10, 0, 0),
             ("HED_Addn_Add_Scalar", 0, 2, 0, 2)],
            [new MaterialVectorStatistics("Emis_Color", MaterialVectorRandomisationKind.PerceptualColour,
                Vector4.Zero, Vector4.One, [])]);
        var hmm = MaterialRandomiser.CreateProposal(
            hmmDonor, [hmmDonor], hmmProfile,
            Values(("U_Offset", 0), ("Emis_Scalar", 0.4f), ("HED_Addn_Add_Scalar", 0)),
            new Dictionary<string, Vector4> { ["Emis_Color"] = new(0.2f, 0.3f, 0.4f, 1) },
            [new("U_Offset", -5, 5), new("Emis_Scalar", 0, 20), new("HED_Addn_Add_Scalar", 0, 2)],
            new HashSet<string>(hmmDonor.MaterialScalars.Keys, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["Emis_Color"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(), 100, 9);
        TestAssert.True(hmm.Scalars["U_Offset"] is >= 0.06f and <= 0.07f,
            "Human eye offset escaped its audited safety interval.");
        TestAssert.Near(0.4f, hmm.Scalars["Emis_Scalar"], 0);
        TestAssert.Near(1, hmm.Scalars["HED_Addn_Add_Scalar"], 0);
        TestAssert.Equal(new Vector4(0.2f, 0.3f, 0.4f, 1), hmm.Vectors["Emis_Color"]);

        AssertExcludedScalar("le2-human-female", "Mask");
        AssertExcludedScalar("le2-asari", "Mask");
        AssertExcludedScalar("le3-turian", "Mask");
        AssertExcludedScalar("le3-krogan", "Wrex_Spec_Scalar");
        AssertConstrainedScalar("le2-human-male", "Sclera_Darken", 0.5f, 0.5f);
        AssertConstrainedScalar("le3-human-female", "Sclera_Darken", 0.5f, 0.5f);
        AssertConstrainedScalar("le3-asari", "Sclera_Darken", 0.5f, 0.5f);
        AssertConstrainedScalar("le2-salarian", "SAL_HED_EYE_Emis", 0.15f, 0.15f);
        AssertConstrainedScalar("le1-salarian", "SAL_HED_Spec_Scalar", 0.9f, 1.5f);
        AssertConstrainedScalar("le1-turian", "TUR_HED_Spwr_Skin_Scalar", 0.108f, 1);
        AssertConstrainedScalar("le3-turian", "TUR_HED_Spwr_Bone_Scalar", 1, 1);
        AssertConstrainedScalar("le2-turian", "TUR_EYE_Lens_SPwr_Scalar", 300, 300);

        static void AssertExcludedScalar(string profileKey, string name)
        {
            var donor = PolicyDonor(profileKey, (name, 0));
            var proposal = MaterialRandomiser.CreateProposal(
                donor, [donor], PolicyProfile(profileKey, [(name, 0, 10, 0, 10)], []),
                Values((name, 0.75f)), new Dictionary<string, Vector4>(), [new(name, 0, 10)],
                new HashSet<string>([name], StringComparer.OrdinalIgnoreCase), new HashSet<string>(),
                new HashSet<string>(), 100, 2);
            TestAssert.Near(0.75f, proposal.Scalars[name], 0);
        }

        static void AssertConstrainedScalar(string profileKey, string name, float safeMin, float safeMax)
        {
            var donor = PolicyDonor(profileKey, (name, safeMin));
            var proposal = MaterialRandomiser.CreateProposal(
                donor, [donor], PolicyProfile(profileKey, [(name, 0, 1000, safeMin, safeMax)], []),
                Values((name, 0)), new Dictionary<string, Vector4>(), [new(name, -1000, 2000)],
                new HashSet<string>([name], StringComparer.OrdinalIgnoreCase), new HashSet<string>(),
                new HashSet<string>(), 100, 3);
            TestAssert.True(proposal.Scalars[name] >= safeMin && proposal.Scalars[name] <= safeMax,
                $"{profileKey}:{name} escaped {safeMin}..{safeMax}.");
        }
    }

    private static void MaterialTextureSelectionIsCurated()
    {
        const string family = "addition:ASA_HED_Addn";
        MorphRandomisationDonor Donor(string id, string texture) => PolicyDonor("le2-asari") with
        {
            Id = id,
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [family] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["ASA_HED_Addn"] = texture }
            }
        };
        var common = Donor("common", "ASA_Add_Common");
        var rare = Donor("rare", "ASA_Add_Rare");
        var invalid = Donor("invalid", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Norm");
        var donors = Enumerable.Repeat(common, 20).Append(rare).Append(invalid).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var seed = 0; seed < 120; seed++)
        {
            var proposal = MaterialRandomiser.CreateProposal(
                common, donors, PolicyProfile("le2-asari", [], []), Values(),
                new Dictionary<string, Vector4>(), [], new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([family], StringComparer.OrdinalIgnoreCase), 100, seed);
            var texture = proposal.TextureFamilies[family]["ASA_HED_Addn"];
            counts[texture] = counts.GetValueOrDefault(texture) + 1;
        }
        TestAssert.True(!counts.ContainsKey("BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Norm"),
            "The invalid Asari armour normal remained eligible as complexion.");
        TestAssert.True(counts.GetValueOrDefault("ASA_Add_Common") > 35 &&
                        counts.GetValueOrDefault("ASA_Add_Rare") > 35,
            "Texture variants still inherited their donor-frequency imbalance.");

        AssertNamedFamilyBlacklisted("le3-human-male", "human-face", "Joker");
        AssertNamedFamilyBlacklisted("le1-human-female", "human-scalp", "Ashley");

        static void AssertNamedFamilyBlacklisted(string profileKey, string textureFamily, string unsafeName)
        {
            MorphRandomisationDonor Candidate(string id, string path) => PolicyDonor(profileKey) with
            {
                Id = id,
                MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [textureFamily] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        { [textureFamily == "human-face" ? "HED_Diff" : "HED_Scalp_Diff"] = path }
                }
            };
            var unsafeDonor = Candidate("unsafe", $"BIOG.{unsafeName}.Unsafe");
            var safeDonor = Candidate("safe", "BIOG.Average.Safe");
            var proposal = MaterialRandomiser.CreateProposal(
                unsafeDonor, [unsafeDonor, safeDonor], PolicyProfile(profileKey, [], []), Values(),
                new Dictionary<string, Vector4>(), [], new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([textureFamily], StringComparer.OrdinalIgnoreCase), 100, 1);
            TestAssert.True(proposal.TextureFamilies[textureFamily].Values.All(value =>
                    !value.Contains(unsafeName, StringComparison.OrdinalIgnoreCase)),
                $"{unsafeName} remained in the eligible {textureFamily} set.");
        }
    }

    private static void TextureDependentSelectorsRemainValid()
    {
        var hmm = PolicyDonor("le3-human-male") with
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["human-face-mask"] = new Dictionary<string, string>
                    { ["HED_Mask"] = "HMM_HED_PRO_Mask3" }
            }
        };
        var current = new Dictionary<string, Vector4> { ["HED_Mask_Vector"] = new(1, 0, 0, 1) };
        var proposal = MaterialRandomiser.CreateProposal(
            hmm, [hmm], PolicyProfile("le3-human-male", [], []), Values(), current, [],
            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(["human-face-mask"]), 100, 8);
        var mask = proposal.Vectors["HED_Mask_Vector"];
        TestAssert.True(mask == new Vector4(0, 0, 0, 1) || mask == new Vector4(0, 0, 1, 1) ||
                        mask == new Vector4(1, 1, 0, 1) || mask == new Vector4(1, 1, 1, 1),
            "HMM Mask3 received an invalid blended channel state.");
        TestAssert.True(MaterialRandomiser.DependentVectorNames(
            "le3-human-male", proposal.TextureFamilies).Contains("HED_Mask_Vector"),
            "The Mask3 selector dependency was not surfaced to the editor batch.");
        var vectorOnly = MaterialRandomiser.CreateProposal(
            PolicyDonor("le3-human-male"), [], PolicyProfile("le3-human-male", [], []), Values(),
            current, [], new HashSet<string>(), new HashSet<string>(["HED_Mask_Vector"]),
            new HashSet<string>(), 100, 11,
            new Dictionary<string, string> { ["HED_Mask"] = "CurrentlySelected_Mask3" });
        TestAssert.True(vectorOnly.Vectors["HED_Mask_Vector"] != new Vector4(1, 0, 0, 1),
            "Vector-only randomisation ignored the currently selected Mask3 texture.");

        var turian = PolicyDonor("le2-turian") with
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["addition:TUR_HED_Addn"] = new Dictionary<string, string>
                    { ["TUR_HED_Addn"] = "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Norm" }
            }
        };
        var turianProposal = MaterialRandomiser.CreateProposal(
            turian, [turian], PolicyProfile("le2-turian", [], []), Values(),
            new Dictionary<string, Vector4> { ["TUR_HED_Addn_Mask_Vector"] = Vector4.One }, [],
            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(["addition:TUR_HED_Addn"]), 100, 4);
        TestAssert.Equal(new Vector4(0, 0, 0, 1), turianProposal.Vectors["TUR_HED_Addn_Mask_Vector"]);
    }

    private static void Le1BatarianUsesCompatibleMaterialDonors()
    {
        var donor = PolicyDonor("le2-batarian", ("BAT_HED_Tmis_Scalar", 0.3f));
        var corpus = new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
                { [MorphRandomisationPoolKey.Batarian] = [donor] })
        {
            MaterialProfiles = new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["le2-batarian"] = PolicyProfile("le2-batarian",
                    [("BAT_HED_Tmis_Scalar", 0.3f, 0.3f, 0.3f, 0.3f)], [])
            }
        };
        var catalog = new MorphRandomisationCatalog(corpus);
        TestAssert.True(catalog.HasMaterialDonors("le1-batarian"),
            "LE1 Batarian did not find the compatible pooled material donor.");
        TestAssert.Equal("le2-batarian", catalog.GetMaterialProfile("le1-batarian")?.ProfileKey);
        TestAssert.Equal(donor.Id, catalog.SelectDonor("le1-batarian", 1, requireMaterial: true).Id);
    }

    private static void MaterialDonorsUseApprovedCrossGamePools()
    {
        MorphRandomisationDonor Donor(string profile) => PolicyDonor(profile, ("Shared", 1));
        var asari = new[] { Donor("le1-asari"), Donor("le2-asari"), Donor("le3-asari") };
        var humanLe12 = new[] { Donor("le1-human-male"), Donor("le2-human-male") };
        var humanLe3 = Donor("le3-human-male");
        var profiles = asari.Concat(humanLe12).Append(humanLe3)
            .Select(value => value.SourceProfileKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value,
                value => PolicyProfile(value, [("Shared", 0, 1, 0, 1)], []),
                StringComparer.OrdinalIgnoreCase);
        var corpus = new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
            {
                [MorphRandomisationPoolKey.Asari] = asari,
                [MorphRandomisationPoolKey.HumanMaleLe12] = humanLe12,
                [MorphRandomisationPoolKey.HumanMaleLe3] = [humanLe3]
            })
        {
            MaterialProfiles = profiles
        };
        var catalog = new MorphRandomisationCatalog(corpus);

        TestAssert.Equal(3, catalog.CompatibleMaterialDonors("le1-asari").Count);
        TestAssert.Equal(3, catalog.CompatibleMaterialDonors("le3-asari").Count);
        TestAssert.Equal(2, catalog.CompatibleMaterialDonors("le1-human-male").Count);
        TestAssert.Equal(2, catalog.CompatibleMaterialDonors("le2-human-male").Count);
        TestAssert.Equal(1, catalog.CompatibleMaterialDonors("le3-human-male").Count);
    }

    private static MorphRandomisationDonor PolicyDonor(
        string profileKey,
        params (string Name, float Value)[] scalars) => new(
        $"{profileKey}.seed", profileKey, new HashSet<string>(), Values())
    {
        MaterialScalars = scalars.ToDictionary(value => value.Name, value => value.Value,
            StringComparer.OrdinalIgnoreCase)
    };

    private static MaterialRandomisationProfile PolicyProfile(
        string profileKey,
        IReadOnlyList<(string Name, float Minimum, float Maximum, float P10, float P90)> scalars,
        IReadOnlyList<MaterialVectorStatistics> vectors) => new(
        profileKey,
        scalars.ToDictionary(value => value.Name,
            value => new MaterialScalarStatistics(value.Name, value.Minimum, value.Maximum, value.P10, value.P90),
            StringComparer.OrdinalIgnoreCase),
        vectors.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>());

    private static MorphRandomisationDonor MaterialDonor() => new(
        "Seed", "le1-human-male", new HashSet<string>(), Values())
    {
        MaterialScalars = Values(("Roughness", 4)),
        MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["SkinTone"] = new(0.4f, 0.2f, 0.1f, 1),
            ["Mask"] = Vector4.UnitX
        },
        MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["face"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Diff"] = "HMM_Face_Diff",
                ["HED_Norm"] = "HMM_Face_Norm"
            }
        }
    };

    private static MaterialRandomisationProfile MaterialProfile() => new(
        "le1-human-male",
        new Dictionary<string, MaterialScalarStatistics>(StringComparer.OrdinalIgnoreCase)
        {
            ["Roughness"] = new("Roughness", 0, 10, 2, 6)
        },
        new Dictionary<string, MaterialVectorStatistics>(StringComparer.OrdinalIgnoreCase)
        {
            ["SkinTone"] = new("SkinTone", MaterialVectorRandomisationKind.PerceptualColour,
                new Vector4(0.4f, 0.2f, 0.1f, 1), new Vector4(0.8f, 0.6f, 0.4f, 1), []),
            ["Mask"] = new("Mask", MaterialVectorRandomisationKind.Selector,
                Vector4.Zero, Vector4.One,
                [new(Vector4.UnitX, 10), new(Vector4.UnitY, 5), new(Vector4.UnitZ, 1)])
        },
        new HashSet<string>(["face"], StringComparer.OrdinalIgnoreCase));

    private static void CursedRandomisationWakesZeroMorphs()
    {
        var features = Values(
            ("eyes_Big", 0),
            ("jaw_Width", 0),
            ("mouth_Width", 0),
            ("nose_BridgeIn", 0),
            ("cheek_Gaunt", 0));

        var first = CursedMorphRandomiser.CreateProposal(
            features, [], Values(), new Dictionary<string, Vector4>(), 100, 123);
        var repeated = CursedMorphRandomiser.CreateProposal(
            features, [], Values(), new Dictionary<string, Vector4>(), 100, 123);

        TestAssert.True(first.FeatureValues.SequenceEqual(repeated.FeatureValues),
            "The same cursed seed did not reproduce the morph proposal.");
        TestAssert.True(first.FeatureValues.Values.All(value => value != 0),
            "A zero-valued morph remained asleep in cursed mode.");
        TestAssert.True(first.FeatureValues["eyes_Big"] is >= -1 and <= 5,
            "Eye morph escaped Mgamerz's range.");
        TestAssert.True(first.FeatureValues["nose_BridgeIn"] is >= -15 and <= 15,
            "Nose morph escaped Mgamerz's widest range.");
        TestAssert.True(first.FeatureValues["cheek_Gaunt"] is >= -7 and <= 7,
            "Unclassified morph escaped the shared cursed range.");
    }

    private static void CursedRandomisationFuzzesBonesAndMaterials()
    {
        var bones = new[]
        {
            new BoneTranslation("nose_tip", new Vector3(2, 4, 6)),
            new BoneTranslation("root", new Vector3(10, 20, 30))
        };
        var scalars = Values(("Skin_Spec", 4));
        var vectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skin_Tint"] = new Vector4(2, 4, 6, 8)
        };

        var result = CursedMorphRandomiser.CreateProposal(
            Values(("eyes_Big", 0)), bones, scalars, vectors, 100, 77);

        var nose = result.BoneValues.Single(value => value.BoneName == "nose_tip").Translation;
        TestAssert.True(nose.X is >= 1 and <= 4 && nose.Y is >= 2 and <= 8 && nose.Z is >= 3 and <= 12,
            "Facial bone multiplier escaped Mgamerz's 0.5x to 2x range.");
        TestAssert.Equal(new Vector3(10, 20, 30),
            result.BoneValues.Single(value => value.BoneName == "root").Translation);
        TestAssert.True(result.ScalarValues["Skin_Spec"] is >= 2 and <= 8,
            "Material scalar multiplier escaped Mgamerz's range.");
        var tint = result.VectorValues["Skin_Tint"];
        TestAssert.True(tint.X is >= 1 and <= 4 && tint.Y is >= 2 and <= 8 &&
                        tint.Z is >= 3 and <= 12 && tint.W is >= 4 and <= 16,
            "Material vector multiplier escaped Mgamerz's range.");
    }

    private static void ZeroStrengthCursedRandomisationIsNoOp()
    {
        var features = Values(("eyes_Big", 0.25f));
        var bones = new[] { new BoneTranslation("eye_left", new Vector3(1, 2, 3)) };
        var scalars = Values(("Skin_Spec", 4));
        var vectors = new Dictionary<string, Vector4> { ["Skin_Tint"] = Vector4.One };

        var result = CursedMorphRandomiser.CreateProposal(
            features, bones, scalars, vectors, 0, 999);

        TestAssert.Near(0.25f, result.FeatureValues["eyes_Big"], 0);
        TestAssert.Equal(new Vector3(1, 2, 3), result.BoneValues.Single().Translation);
        TestAssert.Near(4, result.ScalarValues["Skin_Spec"], 0);
        TestAssert.Equal(Vector4.One, result.VectorValues["Skin_Tint"]);
    }

    private static void CursedRandomisationKeepsExtremeInputsFinite()
    {
        var result = CursedMorphRandomiser.CreateProposal(
            Values(("cheek_Gaunt", float.MaxValue)),
            [new BoneTranslation("jaw", new Vector3(float.MaxValue))],
            Values(("Skin_Spec", float.MaxValue)),
            new Dictionary<string, Vector4> { ["Skin_Tint"] = new Vector4(float.MaxValue) },
            100,
            5);

        TestAssert.True(result.FeatureValues.Values.All(float.IsFinite),
            "An extreme cursed morph overflowed to a non-finite value.");
        TestAssert.True(result.BoneValues.All(value =>
                float.IsFinite(value.Translation.X) && float.IsFinite(value.Translation.Y) && float.IsFinite(value.Translation.Z)),
            "An extreme cursed bone overflowed to a non-finite value.");
        TestAssert.True(result.ScalarValues.Values.All(float.IsFinite) &&
                        result.VectorValues.Values.All(value =>
                            float.IsFinite(value.X) && float.IsFinite(value.Y) &&
                            float.IsFinite(value.Z) && float.IsFinite(value.W)),
            "An extreme cursed material overflowed to a non-finite value.");
    }

    private static void FeatureBatchIsOneExactEdit()
    {
        var session = CreateEditingSession();
        var evaluations = 0;
        var commits = 0;
        session.EvaluationChanged += (_, _) => evaluations++;
        session.EditCommitted += (_, _) => commits++;

        session.SetFeatures(Values(("First", 0.25f), ("Second", 0.75f)));

        TestAssert.Near(0.25f, session.GetFeature("First"), 0);
        TestAssert.Near(0.75f, session.GetFeature("Second"), 0);
        TestAssert.Equal(1, evaluations);
        TestAssert.Equal(1, commits);
        TestAssert.True(session.CanUndo, "The feature batch did not create an undo entry.");

        session.Undo();
        TestAssert.Near(0, session.GetFeature("First"), 0);
        TestAssert.Near(0, session.GetFeature("Second"), 0);
        TestAssert.Equal(2, evaluations);
        TestAssert.True(session.CanRedo, "Undo did not expose the exact batch for redo.");

        session.Redo();
        TestAssert.Near(0.25f, session.GetFeature("First"), 0);
        TestAssert.Near(0.75f, session.GetFeature("Second"), 0);
        TestAssert.Equal(3, evaluations);
    }

    private static void InvalidFeatureBatchIsAtomic()
    {
        var session = CreateEditingSession();
        var evaluations = 0;
        session.EvaluationChanged += (_, _) => evaluations++;

        AssertThrows<KeyNotFoundException>(() => session.SetFeatures(
            Values(("First", 0.5f), ("Missing", 0.75f))));

        TestAssert.Near(0, session.GetFeature("First"), 0);
        TestAssert.Equal(0, evaluations);
        TestAssert.True(!session.CanUndo, "A rejected batch created history.");
    }

    private static void UnchangedFeatureBatchCreatesNoHistory()
    {
        var session = CreateEditingSession();
        var evaluations = 0;
        session.EvaluationChanged += (_, _) => evaluations++;

        session.SetFeatures(Values(("First", 0), ("Second", 0)));

        TestAssert.Equal(0, evaluations);
        TestAssert.True(!session.CanUndo, "An unchanged batch created history.");
    }

    private static MorphFaceEditingSession CreateEditingSession()
    {
        var mesh = TestFixtures.CreateMesh();
        MorphTargetAsset Target(string name) => new(
            TestFixtures.CreateIdentity($"Set.{name}", "MorphTarget"),
            [new MorphTargetLod(0, 3, [])],
            []);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [new MorphFeatureValue("First", 0), new MorphFeatureValue("Second", 0)],
            [new BoneTranslation("root", System.Numerics.Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditingSession(document, mesh, [Target("First"), Target("Second")]);
        TestAssert.True(session.CanEdit, session.EditBlockReason ?? "Fixture session was not editable.");
        return session;
    }

    private static void BundleRoundTripsDeterministically()
    {
        var donor = new MorphRandomisationDonor(
            "LE1.Face", "le1-human-male",
            new HashSet<string>(["B", "A"], StringComparer.OrdinalIgnoreCase),
            Values(("B", 0.25f), ("A", 0.75f)))
        {
            MaterialScalars = Values(("Roughness", 0.5f)),
            MaterialVectors = new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One },
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["face"] = new Dictionary<string, string> { ["HED_Diff"] = "Face_Diff" }
            }
        };
        var corpus = Corpus(donor) with
        {
            MaterialProfiles = new Dictionary<string, MaterialRandomisationProfile>
            {
                ["le1-human-male"] = MaterialProfile()
            }
        };
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        MorphRandomisationBundleSerializer.Write(first, corpus);
        MorphRandomisationBundleSerializer.Write(second, corpus);
        TestAssert.True(first.ToArray().SequenceEqual(second.ToArray()),
            "Repeated bundle serialization changed bytes.");

        first.Position = 0;
        var roundTrip = MorphRandomisationBundleSerializer.Read(first);
        var actual = roundTrip.Pools[MorphRandomisationPoolKey.HumanMaleLe12].Single();
        TestAssert.Equal("LE1.Face", actual.Id);
        TestAssert.Equal("le1-human-male", actual.SourceProfileKey);
        TestAssert.True(actual.AvailableFeatures.SetEquals(["A", "B"]),
            "Available feature mask changed during bundle serialization.");
        TestAssert.Near(0.75f, actual.NonZeroValues["A"], 0);
        TestAssert.Near(0.25f, actual.NonZeroValues["B"], 0);
        TestAssert.Near(0.5f, actual.MaterialScalars["Roughness"], 0);
        TestAssert.Equal(Vector4.One, actual.MaterialVectors["SkinTone"]);
        TestAssert.Equal("Face_Diff", actual.MaterialTextureFamilies["face"]["HED_Diff"]);
        TestAssert.Equal(MaterialVectorRandomisationKind.Selector,
            roundTrip.MaterialProfiles["le1-human-male"].Vectors["Mask"].Kind);
    }

    private static void CompilationPreservesFeatureStates()
    {
        var profiles = new[]
        {
            Definition("le1-human-male", MorphFaceGame.LE1, new HashSet<string>(["A", "B"])),
            Definition("le2-human-male", MorphFaceGame.LE2, new HashSet<string>(["A", "B", "C"]))
        };
        var faces = new[]
        {
            RawFace(MorphFaceGame.LE1, "LE1.Face", ("A", 0.75f)),
            RawFace(MorphFaceGame.LE2, "LE2.Face", ("C", 0.5f))
        };

        var result = RandomisationCorpusCompiler.Compile(faces, profiles, []);

        TestAssert.Equal(RandomisationFeatureState.NonZero,
            result.Rows.Single(row => row.FacePath == "LE1.Face" && row.FeatureName == "A").State);
        TestAssert.Equal(RandomisationFeatureState.Zero,
            result.Rows.Single(row => row.FacePath == "LE1.Face" && row.FeatureName == "B").State);
        TestAssert.Equal(RandomisationFeatureState.Unavailable,
            result.Rows.Single(row => row.FacePath == "LE1.Face" && row.FeatureName == "C").State);
        var le1 = result.Corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12]
            .Single(donor => donor.Id.EndsWith("LE1.Face", StringComparison.Ordinal));
        TestAssert.True(le1.AvailableFeatures.SetEquals(["A", "B"]),
            "LE1 donor availability incorrectly included the LE2-only feature.");
    }

    private static void CompilationReportsExclusions()
    {
        var profile = Definition("le1-human-male", MorphFaceGame.LE1, new HashSet<string>(["A"]));
        var empty = RawFace(MorphFaceGame.LE1, "LE1.Empty");
        var broken = RawFace(MorphFaceGame.LE1, "LE1.Broke", ("A", 0.9f));
        var exclusion = new RandomisationDonorExclusion(
            MorphFaceGame.LE1, "LE1.Broke", "Known malformed corpus face.");

        var first = RandomisationCorpusCompiler.Compile([empty, broken], [profile], [exclusion]);
        var second = RandomisationCorpusCompiler.Compile([empty, broken], [profile], [exclusion]);

        TestAssert.Equal(0, first.Corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12].Count);
        TestAssert.True(first.Faces.Single(face => face.FacePath == "LE1.Empty").ExclusionReason!
                .Contains("no non-zero", StringComparison.OrdinalIgnoreCase),
            "All-zero donor did not receive the automatic exclusion reason.");
        TestAssert.Equal("Known malformed corpus face.",
            first.Faces.Single(face => face.FacePath == "LE1.Broke").ExclusionReason);
        TestAssert.Equal(first.Markdown, second.Markdown);
        TestAssert.Equal(first.Csv, second.Csv);
    }

    private static void CompilationClassifiesMaterials()
    {
        var profile = Definition("le1-human-male", MorphFaceGame.LE1, new HashSet<string>(["A"]));
        var face = RawFace(MorphFaceGame.LE1, "LE1.Material", ("A", 0.5f)) with
        {
            MaterialScalars = Values(("Roughness", 0.4f)),
            MaterialVectors = new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new(0.4f, 0.2f, 0.1f, 1),
                ["HED_Mask_Vector"] = Vector4.UnitX
            },
            MaterialTextures = new Dictionary<string, string>
            {
                ["HED_Diff"] = "HMM_Face_Diff",
                ["HED_Norm"] = "HMM_Face_Norm",
                ["HED_Mask"] = "HMM_Face_Mask3",
                ["HED_Addn"] = "HMM_Beard_Diff"
            }
        };

        var result = RandomisationCorpusCompiler.Compile([face], [profile], []);
        var donor = result.Corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12].Single();
        var material = result.Corpus.MaterialProfiles["le1-human-male"];

        TestAssert.Near(0.4f, donor.MaterialScalars["Roughness"], 0);
        TestAssert.Equal(MaterialVectorRandomisationKind.PerceptualColour,
            material.Vectors["SkinTone"].Kind);
        TestAssert.Equal(MaterialVectorRandomisationKind.Selector,
            material.Vectors["HED_Mask_Vector"].Kind);
        TestAssert.True(donor.MaterialTextureFamilies["human-face"].Count == 2,
            "Human Diff/Norm were not compiled as one atomic family.");
        TestAssert.Equal("HMM_Face_Mask3",
            donor.MaterialTextureFamilies["human-face-mask"]["HED_Mask"]);
        TestAssert.Equal("HMM_Beard_Diff",
            donor.MaterialTextureFamilies["addition:HED_Addn"]["HED_Addn"]);
    }

    private static void DefaultDefinitionsExposeApprovedPools()
    {
        var definitions = RandomisationProfileDefinitionFactory.CreateDefault(
            MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog());

        TestAssert.Equal(21, definitions.Count);
        TestAssert.Equal(9, definitions.Select(value => value.PoolKey).Distinct().Count());
        TestAssert.True(definitions.All(value => value.AvailableFeatures.Count > 0),
            "A supported profile exposed no randomisable morph targets.");
        TestAssert.Equal(MorphRandomisationPoolKey.HumanMaleLe12,
            definitions.Single(value => value.ProfileKey == "le1-human-male").PoolKey);
        TestAssert.Equal(MorphRandomisationPoolKey.HumanMaleLe12,
            definitions.Single(value => value.ProfileKey == "le2-human-male").PoolKey);
        TestAssert.Equal(MorphRandomisationPoolKey.HumanMaleLe3,
            definitions.Single(value => value.ProfileKey == "le3-human-male").PoolKey);
    }

    private static RandomisationProfileDefinition Definition(
        string profileKey,
        MorphFaceGame game,
        IReadOnlySet<string> available) =>
        new(profileKey, game, MorphRandomisationPoolKey.HumanMaleLe12,
            (_, _) => true,
            available,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static RawMorphRandomisationFace RawFace(
        MorphFaceGame game,
        string facePath,
        params (string Name, float Value)[] features) =>
        new(game, $"{game}.pcc", facePath, "BaseHead",
            features.Select(value => new MorphFeatureValue(value.Name, value.Value)).ToArray());

    private static MorphRandomisationCorpus Corpus(
        MorphRandomisationDonor donor,
        MorphRandomisationPoolKey key = MorphRandomisationPoolKey.HumanMaleLe12) =>
        new(MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
            {
                [key] = [donor]
            });

    private static Dictionary<string, float> Values(params (string Name, float Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<MorphRandomisationFeatureBounds> Bounds(params string[] names) =>
        names.Select(name => new MorphRandomisationFeatureBounds(name, 0, 1)).ToArray();

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        catch (T)
        {
        }
    }
}
