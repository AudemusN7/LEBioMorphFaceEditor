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
        new("morph proposals preserve donor zeros and scope and reject invalid bounds", ZeroStrengthReproducesDonor),
        new("LE1 Batarian specular power stays between 0.4 and 0.7", Le1BatarianSpecularPowerIsBounded),
        new("LE3 HMF highlight colours share one complete donor and interpolation", Le3FemaleHighlightsStayPaired),
        new("missing selected hair highlights fall back to compatible corpus colours", MissingSelectedHairHighlightsUseCompatibleDonors),
        new("material safety policy constrains or excludes hazardous numeric parameters", MaterialSafetyPolicyProtectsNumericParameters),
        new("material randomisation rejects unsafe and failed texture families", RejectedTextureFamiliesCannotReenterProposal),
        new("texture-dependent selector rules remain valid", TextureDependentSelectorsRemainValid),
        new("LE1 female makeup randomisation applies its required mask", Le1FemaleMakeupRandomisationAppliesMask),
        new("installed texture randomisation keeps only complete resolvable families", InstalledTextureRandomisationFiltersFamilies),
        new("material seed families retain the installed projection", MaterialSeedUsesInstalledProjection),
        new("rejected material seed families use compatible target parameters", RejectedSeedUsesCompatibleFamily),
        new("texture family scope honours disabled texture categories", TextureFamilyScopeHonoursDisabledCategories),
        new("cursed randomisation wakes zero morphs within Mgamerz ranges", CursedRandomisationWakesZeroMorphs),
        new("cursed randomisation wakes zero material and facial-bone channels", CursedRandomisationWakesZeroExtraChannels),
        new("cursed randomisation fuzzes only Mgamerz facial bones and numeric materials", CursedRandomisationFuzzesBonesAndMaterials),
        new("zero-strength cursed randomisation is an exact no-op", ZeroStrengthCursedRandomisationIsNoOp),
        new("cursed randomisation keeps extreme finite inputs finite", CursedRandomisationKeepsExtremeInputsFinite),
        new("feature batches ignore no-ops and reject invalid input atomically", InvalidFeatureBatchIsAtomic),
    ];

    private static void Le1FemaleMakeupRandomisationAppliesMask()
    {
        const string scalar = "HED_Lips_Tint_Scalar";
        var donor = PolicyDonor("le1-human-female") with
        {
            MaterialScalars = Values((scalar, 0.75f))
        };
        var proposal = MaterialRandomiser.CreateProposal(
            donor,
            [donor],
            PolicyProfile("le1-human-female", [(scalar, 0, 1, 0, 1)], []),
            Values((scalar, 0)),
            new Dictionary<string, Vector4>(),
            [new MaterialRandomisationScalarBounds(scalar, 0, 1)],
            new HashSet<string>([scalar], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(),
            new HashSet<string>(),
            0,
            17,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Makeup_Mask"] = "BIOG_GBL_ARM_ALL_T.Black.GBL_ARM_ALL_Black"
            });

        TestAssert.Equal(
            "BIOG_HMF_HED_PROMorph_R.Masks.HMF_HED_PROCustom_MKup_01",
            proposal.TextureFamilies["human-makeup-required"]["HED_Makeup_Mask"]);
    }

    private static void InstalledTextureRandomisationFiltersFamilies()
    {
        var donor = new MorphRandomisationDonor(
            "LE3.HMM.Registry",
            "le3-human-male",
            new HashSet<string>(),
            new Dictionary<string, float>())
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["human-scalp"] = new Dictionary<string, string>
                {
                    ["HED_Scalp_Diff"] = "Installed.Scalp_Diff",
                    ["HED_Scalp_Norm"] = "Installed.Scalp_Norm",
                    ["HED_Scalp_Spec"] = "Missing.Optional_Spec"
                },
                ["human-face"] = new Dictionary<string, string>
                {
                    ["HED_Diff"] = "Installed.Face_Diff",
                    ["HED_Norm"] = "Missing.Face_Norm"
                },
                ["parameter-a"] = new Dictionary<string, string>
                {
                    ["Parameter_A"] = "Shared.Instanced"
                },
                ["parameter-b"] = new Dictionary<string, string>
                {
                    ["Parameter_B"] = "Shared.Instanced"
                }
            }
        };
        var secondDonor = donor with { Id = "LE3.HMM.Registry.Second" };
        var corpus = new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
            {
                [MorphRandomisationPoolKey.HumanMaleLe3] = [donor, secondDonor]
            })
        {
            MaterialProfiles = new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["le3-human-male"] = new MaterialRandomisationProfile(
                    "le3-human-male",
                    new Dictionary<string, MaterialScalarStatistics>(),
                    new Dictionary<string, MaterialVectorStatistics>(),
                    new HashSet<string>(["human-scalp", "human-face"], StringComparer.OrdinalIgnoreCase))
            }
        };
        var catalog = new MorphRandomisationCatalog(corpus);
        var available = new HashSet<(string Parameter, string Path)>
        {
            ("HED_Scalp_Diff", "Installed.Scalp_Diff"),
            ("HED_Scalp_Norm", "Installed.Scalp_Norm"),
            ("HED_Diff", "Installed.Face_Diff"),
            ("Parameter_A", "Shared.Instanced")
        };
        var resolverCalls = new Dictionary<(string Parameter, string Path), int>();

        var projected = catalog.ProjectInstalledMaterialDonors("le3-human-male", (parameter, path) =>
        {
            var key = (parameter, path);
            resolverCalls[key] = resolverCalls.GetValueOrDefault(key) + 1;
            return available.Contains(key);
        }).ToArray();

        TestAssert.Equal("LE3.HMM.Registry|LE3.HMM.Registry.Second",
            string.Join('|', projected.Select(value => value.Id)));
        TestAssert.Equal(1, resolverCalls[("HED_Scalp_Diff", "Installed.Scalp_Diff")]);
        TestAssert.True(projected[0].MaterialTextureFamilies.ContainsKey("human-scalp"),
            "A family with both required installed members was removed because an optional member was unavailable.");
        TestAssert.Equal("Installed.Scalp_Diff",
            projected[1].MaterialTextureFamilies["human-scalp"]["HED_Scalp_Diff"]);
        TestAssert.True(!projected[0].MaterialTextureFamilies["human-scalp"].ContainsKey("HED_Scalp_Spec"),
            "An unavailable optional member remained in the installed family projection.");
        TestAssert.True(projected[0].MaterialTextureFamilies.ContainsKey("parameter-a") &&
                        !projected[0].MaterialTextureFamilies.ContainsKey("parameter-b"),
            "The same installed path was not resolved independently for each material parameter.");
        TestAssert.True(!projected[0].MaterialTextureFamilies.ContainsKey("human-face"),
            "A family missing a required normal map remained eligible for randomisation.");

        available.Remove(("HED_Scalp_Diff", "Installed.Scalp_Diff"));
        var refreshed = catalog.ProjectInstalledMaterialDonors(
            "le3-human-male", (parameter, path) => available.Contains((parameter, path)));
        TestAssert.True(!refreshed[0].MaterialTextureFamilies.ContainsKey("human-scalp"),
            "A later projection reused source availability from the previous call.");
    }

    private static void MaterialSeedUsesInstalledProjection()
    {
        var raw = new MorphRandomisationDonor("seed", "le2-human-male", new HashSet<string>(),
            new Dictionary<string, float>())
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["human-eyes"] = new Dictionary<string, string>
                {
                    ["EYE_Diff"] = "Installed.Eye_Diff",
                    ["EYE_Lens_Norm"] = "Unavailable.OtherGame_Lens"
                }
            }
        };
        var projected = raw with
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["human-eyes"] = new Dictionary<string, string> { ["EYE_Diff"] = "Installed.Eye_Diff" }
            }
        };
        foreach (var strength in new[] { 0, 100 })
        {
            var proposal = TextureOnlyProposal(raw, [projected], "le1-human-male", "human-eyes", strength);
            TestAssert.True(!proposal.TextureFamilies["human-eyes"].ContainsKey("EYE_Lens_Norm"),
                "The raw seed reintroduced a member removed from the installed projection.");
            TestAssert.Equal("Installed.Eye_Diff", proposal.TextureFamilies["human-eyes"]["EYE_Diff"]);
        }
    }

    private static void RejectedSeedUsesCompatibleFamily()
    {
        var raw = new MorphRandomisationDonor("LE3.Turian", "le3-turian", new HashSet<string>(),
            new Dictionary<string, float>())
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["turian-eyes"] = new Dictionary<string, string> { ["EYE_Diff"] = "Unavailable.ForeignEye" }
            }
        };
        var projected = raw with { MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>() };
        var compatible = raw with
        {
            Id = "LE1.Turian", SourceProfileKey = "le1-turian",
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["turian-eyes"] = new Dictionary<string, string> { ["TUR_EYE_Diff"] = "Installed.TargetEye" }
            }
        };
        foreach (var strength in new[] { 0, 100 })
        {
            var proposal = TextureOnlyProposal(raw, [projected, compatible], "le1-turian", "turian-eyes", strength);
            TestAssert.True(proposal.TextureFamilies["turian-eyes"].ContainsKey("TUR_EYE_Diff") &&
                !proposal.TextureFamilies["turian-eyes"].ContainsKey("EYE_Diff"),
                "A rejected seed family bypassed the target-game parameter set.");
            TestAssert.Equal("Installed.TargetEye", proposal.TextureFamilies["turian-eyes"]["TUR_EYE_Diff"]);
        }
    }

    private static MaterialRandomisationProposal TextureOnlyProposal(MorphRandomisationDonor donor,
        IReadOnlyList<MorphRandomisationDonor> projected, string profile, string family, int strength) =>
        MaterialRandomiser.CreateProposal(donor, projected,
            new MaterialRandomisationProfile(profile, new Dictionary<string, MaterialScalarStatistics>(),
                new Dictionary<string, MaterialVectorStatistics>(), new HashSet<string> { family }),
            new Dictionary<string, float>(), new Dictionary<string, Vector4>(), [], new HashSet<string>(),
            new HashSet<string>(), new HashSet<string> { family }, strength, 123);

    private static void TextureFamilyScopeHonoursDisabledCategories()
    {
        var family = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Diff"] = "Installed.Face_Diff",
            ["HED_Norm"] = "Installed.Face_Norm",
            ["HED_Mask"] = "Installed.Face_Mask"
        };
        var available = family.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allRequested = family.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maskCategoryDisabled = new HashSet<string>(["HED_Diff", "HED_Norm"],
            StringComparer.OrdinalIgnoreCase);

        TestAssert.True(MaterialTextureFamilyPolicy.IsInScope(family, allRequested, available),
            "A fully enabled coherent texture family was excluded.");
        TestAssert.True(!MaterialTextureFamilyPolicy.IsInScope(family, maskCategoryDisabled, available),
            "A coherent texture family crossed into a disabled texture category.");
    }

    public static IReadOnlyList<TestCase> Tooling { get; } =
    [
        new("randomisation bundle round-trips deterministically", BundleRoundTripsDeterministically),
        new("compiler retains native hair aliases and their observed statistics", CompilationPreservesNativeHairAliases),
        new("corpus compilation distinguishes unavailable zero and nonzero features", CompilationPreservesFeatureStates),
        new("corpus audit keeps same-path faces from separate packages distinct", CompilationReportsOverlappingPackages),
        new("reviewed exclusions follow source faces into ports and preserve material-only seeds", ReviewedExclusionsFollowSource),
        new("corpus drops mismatched HMM scalp families without losing other donor data", CompilationRejectsConflictingScalps),
    ];

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
            corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12].Single(), current, bounds,
            new HashSet<string>(["Eyes", "Nose"], StringComparer.OrdinalIgnoreCase), 0, 17);

        TestAssert.Equal("LE2.HMM.Seed", result.DonorId);
        TestAssert.Near(0.75f, result.Values["Eyes"], 0);
        TestAssert.Near(0, result.Values["Nose"], 0);
        TestAssert.Near(0.8f, result.Values["Jaw"], 0);
        var donor = corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12].Single();
        var scope = new HashSet<string>(current.Keys, StringComparer.OrdinalIgnoreCase);
        AssertThrows<ArgumentOutOfRangeException>(() => MorphRandomiser.CreateProposal(
            donor, current, bounds, scope, 101, 1));
        AssertThrows<ArgumentException>(() => MorphRandomiser.CreateProposal(
            donor, current, [.. bounds.Skip(1), new MorphRandomisationFeatureBounds("Eyes", 2, 1)], scope, 50, 1));
    }

    private static void Le1BatarianSpecularPowerIsBounded()
    {
        const string name = "BAT_HED_SPwr_Scalar";
        float Sample(string profileKey, int strength, int seed)
        {
            var donor = PolicyDonor(profileKey, (name, 0.6f));
            var profile = PolicyProfile(profileKey, [(name, 0, 1, 0.6f, 0.6f)], []);
            return MaterialRandomiser.CreateProposal(
                donor, [donor], profile, Values((name, 0.6f)),
                new Dictionary<string, Vector4>(), [new(name, 0, 1)],
                new HashSet<string>([name], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(), new HashSet<string>(), strength, seed).Scalars[name];
        }

        TestAssert.Near(0.6f, Sample("le1-batarian", 0, 1), 0);
        var le1Rolls = Enumerable.Range(0, 64).Select(seed => Sample("le1-batarian", 100, seed)).ToArray();
        TestAssert.True(le1Rolls.All(value => value is >= 0.4f and <= 0.7f) &&
                        le1Rolls.Any(value => value < 0.6f) &&
                        le1Rolls.Any(value => value > 0.6f),
            "LE1 Batarian specular power escaped or stopped varying inside its requested interval.");
        TestAssert.True(Enumerable.Range(0, 64)
                .Select(seed => Sample("le2-batarian", 100, seed))
                .Any(value => value is < 0.4f or > 0.7f),
            "The LE1-only rule changed another game's material randomisation.");
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

    private static void RejectedTextureFamiliesCannotReenterProposal()
    {
        foreach (var (profile, family, parameter, rejectedPath, decodeFailed) in new[]
        {
            ("le2-asari", "addition:ASA_HED_Addn", "ASA_HED_Addn", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Norm", false),
            ("le3-human-male", "human-face", "HED_Diff", "BIOG.Joker.Unsafe", false),
            ("le1-human-female", "human-scalp", "HED_Scalp_Diff", "BIOG.Ashley.Unsafe", false),
            ("le3-human-male", "human-face", "HED_Diff", "Installed.Failed_Diff", true)
        })
        {
            MorphRandomisationDonor Donor(string id, string path) => PolicyDonor(profile) with
            {
                Id = id,
                MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
                    { [family] = new Dictionary<string, string> { [parameter] = path } }
            };
            var rejected = Donor("rejected", rejectedPath);
            var safe = Donor("safe", "BIOG.Average.Safe");
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (decodeFailed)
                excluded.Add(MaterialRandomiser.TextureFamilySignature(rejected.MaterialTextureFamilies[family]));
            var proposal = MaterialRandomiser.CreateProposal(
                rejected, [rejected, safe], PolicyProfile(profile, [], []), Values(),
                new Dictionary<string, Vector4>(), [], new HashSet<string>(), new HashSet<string>(),
                new HashSet<string> { family }, 100, 17, excludedTextureSignatures: excluded);
            TestAssert.Equal("BIOG.Average.Safe", proposal.TextureFamilies[family][parameter]);
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

    private static void CompilationPreservesNativeHairAliases()
    {
        var raw = RawFace(MorphFaceGame.LE1, "Human.Hair", ("A", 0.5f)) with
        {
            MaterialVectors = new Dictionary<string, Vector4>
                { ["HED_Hair_Colour_Vector"] = Vector4.One, ["Hair_Color"] = Vector4.UnitX }
        };
        var other = raw with { FacePath = "Human.Other", MaterialVectors =
            new Dictionary<string, Vector4> { ["HED_Hair_Colour_Vector"] = Vector4.One } };
        var definition = Definition("le1-human-male", MorphFaceGame.LE1, new HashSet<string>(["A"]));
        var corpus = RandomisationCorpusCompiler.Compile([raw, other], [definition], []).Corpus;
        var donors = corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12];
        TestAssert.Equal(2, donors.Count);
        TestAssert.Equal(Vector4.UnitX, donors.Single(value => value.Id.EndsWith(":Human.Hair")).MaterialVectors["Hair_Color"]);
        TestAssert.Equal(Vector4.One, donors.Single(value => value.Id.EndsWith(":Human.Other")).MaterialVectors["Hair_Color"]);
        var statistics = corpus.MaterialProfiles[definition.ProfileKey].Vectors["Hair_Color"];
        TestAssert.Equal(Vector4.UnitX, statistics.Minimum);
        TestAssert.Equal(Vector4.UnitX, statistics.Maximum);
        TestAssert.Equal(Vector4.One, raw.MaterialVectors["HED_Hair_Colour_Vector"]);
    }

    private static void Le3FemaleHighlightsStayPaired()
    {
        static MaterialVectorStatistics Stats(string name) => new(
            name, MaterialVectorRandomisationKind.PerceptualColour,
            Vector4.Zero, Vector4.One, []);
        MorphRandomisationDonor Donor(string id, Vector4? first, Vector4? second) =>
            PolicyDonor("le3-human-female") with
            {
                Id = id,
                MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Highlight1Color"] = first ?? Vector4.Zero,
                    ["Highlight2Colour_Vector"] = second ?? Vector4.Zero
                }.Where((_, index) => index == 0 ? first.HasValue : second.HasValue)
                 .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase)
            };

        var seedValue = new Vector4(0.12f, 0.22f, 0.32f, 1);
        var targetValue = new Vector4(0.82f, 0.62f, 0.42f, 1);
        var seed = Donor("seed", seedValue, null);
        var pairedTarget = Donor("paired-target", targetValue, targetValue);
        var alternateValue = new Vector4(0.42f, 0.72f, 0.22f, 1);
        var alternatePair = Donor("alternate-pair", alternateValue, alternateValue);
        var incomplete = Donor("incomplete", new Vector4(0.95f, 0.05f, 0.7f, 1), null);
        var profile = PolicyProfile("le3-human-female", [],
            [Stats("Highlight1Color"), Stats("Highlight2Colour_Vector")]);
        var current = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Highlight1Color"] = seedValue,
            ["Highlight2Colour_Vector"] = seedValue
        };

        MaterialRandomisationProposal Roll(int randomSeed) => MaterialRandomiser.CreateProposal(
            seed, [pairedTarget, alternatePair, incomplete], profile, Values(), current, [],
            new HashSet<string>(),
            new HashSet<string>(["Highlight1Color", "Highlight2Colour_Vector"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(), 50, randomSeed);
        var paired = Roll(438);
        TestAssert.Equal(paired.Vectors["Highlight1Color"], paired.Vectors["Highlight2Colour_Vector"]);
        var recoveredRoll = Enumerable.Range(0, 128).Select(Roll).FirstOrDefault(value =>
            value.Vectors["Highlight1Color"] != seedValue ||
            value.Vectors["Highlight2Colour_Vector"] != seedValue);
        TestAssert.True(recoveredRoll is not null,
            "An incomplete selected donor prevented a roll despite complete compatible pairs.");
        TestAssert.Equal(recoveredRoll!.Vectors["Highlight1Color"],
            recoveredRoll.Vectors["Highlight2Colour_Vector"]);

        var single = MaterialRandomiser.CreateProposal(
            pairedTarget, [pairedTarget], profile, Values(), current, [],
            new HashSet<string>(), new HashSet<string>(["Highlight1Color"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(), 50, 438);
        TestAssert.Equal(seedValue, single.Vectors["Highlight2Colour_Vector"]);
        TestAssert.True(single.Vectors["Highlight1Color"] != seedValue,
            "The in-scope highlight did not randomise by itself.");

        var mixed = new Dictionary<string, Vector4>(current)
        {
            ["Aniso_Color_1"] = seedValue,
            ["Aniso_Color_2"] = seedValue
        };
        var mixedResult = MaterialRandomiser.CreateProposal(pairedTarget, [pairedTarget], profile,
            Values(), mixed, [], new HashSet<string>(), new HashSet<string>(mixed.Keys), new HashSet<string>(), 50, 438);
        TestAssert.Equal(mixedResult.Vectors["Highlight1Color"], mixedResult.Vectors["Aniso_Color_1"]);
        TestAssert.Equal(mixedResult.Vectors["Highlight2Colour_Vector"], mixedResult.Vectors["Aniso_Color_2"]);
    }

    private static void MissingSelectedHairHighlightsUseCompatibleDonors()
    {
        static MaterialVectorStatistics Colour(string name) => new(
            name, MaterialVectorRandomisationKind.PerceptualColour, Vector4.Zero, Vector4.One, []);
        static MaterialVectorStatistics Selector(string name) => new(
            name, MaterialVectorRandomisationKind.Selector, Vector4.Zero, Vector4.One,
            [new(Vector4.UnitX, 1), new(Vector4.UnitY, 1)]);

        const string profileKey = "le2-human-female";
        var selectedDonor = PolicyDonor(profileKey) with
        {
            Id = "selected-without-highlights",
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Hair_Colour_Vector"] = new(0.2f, 0.1f, 0.05f, 1)
            }
        };
        var firstHighlightDonor = PolicyDonor(profileKey) with
        {
            Id = "highlight-donor-a",
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["Highlight1Colour_Vector"] = new(0.1f, 0.8f, 0.2f, 1),
                ["Highlight2Colour_Vector"] = new(0.8f, 0.1f, 0.2f, 1),
                ["UnrelatedMissingVector"] = new(0.9f, 0.2f, 0.1f, 1),
                ["UnrelatedSelector"] = Vector4.UnitY
            }
        };
        var secondHighlightDonor = PolicyDonor(profileKey) with
        {
            Id = "highlight-donor-b",
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["Highlight1Colour_Vector"] = new(0.8f, 0.2f, 0.7f, 1),
                ["Highlight2Colour_Vector"] = new(0.2f, 0.7f, 0.8f, 1),
                ["UnrelatedMissingVector"] = new(0.1f, 0.8f, 0.2f, 1),
                ["UnrelatedSelector"] = Vector4.UnitY
            }
        };
        var compatibleDonors = new[] { selectedDonor, firstHighlightDonor, secondHighlightDonor };
        var profile = PolicyProfile(profileKey, [],
        [
            Colour("Highlight1Colour_Vector"), Colour("Highlight2Colour_Vector"),
            Colour("UnrelatedMissingVector"), Selector("UnrelatedSelector")
        ]);
        var current = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Highlight1Colour_Vector"] = new(0.9f, 0.2f, 0.15f, 1),
            ["Highlight2Colour_Vector"] = new(0.2f, 0.1f, 0.02f, 1),
            ["UnrelatedMissingVector"] = new(0.3f, 0.4f, 0.5f, 1),
            ["UnrelatedSelector"] = Vector4.UnitX
        };
        var vectorScope = new HashSet<string>(current.Keys, StringComparer.OrdinalIgnoreCase);

        var fullStrength = MaterialRandomiser.CreateProposal(
            selectedDonor, compatibleDonors, profile, Values(), current, [],
            new HashSet<string>(), vectorScope, new HashSet<string>(), 100, 4321);
        TestAssert.True(fullStrength.Vectors["Highlight1Colour_Vector"] != current["Highlight1Colour_Vector"],
            "Highlight 1 stayed unchanged when the selected donor lacked it but compatible donors supplied it.");
        TestAssert.True(fullStrength.Vectors["Highlight2Colour_Vector"] != current["Highlight2Colour_Vector"],
            "Highlight 2 stayed unchanged when the selected donor lacked it but compatible donors supplied it.");
        TestAssert.Equal(current["UnrelatedMissingVector"], fullStrength.Vectors["UnrelatedMissingVector"]);
        TestAssert.Equal(current["UnrelatedSelector"], fullStrength.Vectors["UnrelatedSelector"]);

        var zeroStrength = MaterialRandomiser.CreateProposal(
            selectedDonor, compatibleDonors, profile, Values(), current, [],
            new HashSet<string>(), vectorScope, new HashSet<string>(), 0, 4321);
        TestAssert.Equal(current["Highlight1Colour_Vector"], zeroStrength.Vectors["Highlight1Colour_Vector"]);
        TestAssert.Equal(current["Highlight2Colour_Vector"], zeroStrength.Vectors["Highlight2Colour_Vector"]);
        TestAssert.Equal(current["UnrelatedMissingVector"], zeroStrength.Vectors["UnrelatedMissingVector"]);
        TestAssert.Equal(current["UnrelatedSelector"], zeroStrength.Vectors["UnrelatedSelector"]);
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

    private static void InvalidFeatureBatchIsAtomic()
    {
        var session = CreateEditingSession();
        var evaluations = 0;
        session.EvaluationChanged += (_, _) => evaluations++;

        session.SetFeatures(Values(("First", 0), ("Second", 0)));
        AssertThrows<KeyNotFoundException>(() => session.SetFeatures(
            Values(("First", 0.5f), ("Missing", 0.75f))));

        TestAssert.Near(0, session.GetFeature("First"), 0);
        TestAssert.Equal(0, evaluations);
        TestAssert.True(!session.CanUndo, "A rejected batch created history.");
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
        TestAssert.True(session.CanEditMorphFeatures, session.EditBlockReason ?? "Fixture session was not editable.");
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

    private static void CompilationReportsOverlappingPackages()
    {
        var profile = Definition("le1-human-male", MorphFaceGame.LE1, new HashSet<string>(["A"]));
        var native = RawFace(MorphFaceGame.LE1, "HMM.Shared", ("A", 0.25f)) with
        {
            PackagePath = "LE1 GlobalMorphs.pcc",
            MaterialScalars = Values(("Skin", 0.25f))
        };
        var duplicate = native with
        {
            PackagePath = "LE2 to LE1 GlobalMorphs.pcc"
        };
        var materialVariant = native with
        {
            PackagePath = "LE3 to LE1 GlobalMorphs.pcc",
            MaterialScalars = Values(("Skin", 0.75f))
        };

        var result = RandomisationCorpusCompiler.Compile(
            [materialVariant, duplicate, native], [profile], []);
        var reordered = RandomisationCorpusCompiler.Compile(
            [native, duplicate, materialVariant], [profile], []);

        TestAssert.Equal(3, result.Faces.Count);
        TestAssert.Equal(2, result.Corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12].Count);
        TestAssert.True(result.Corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12]
                .Select(donor => donor.Id)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(["LE1:LE1:HMM.Shared", "LE3:LE1:HMM.Shared"]),
            "Source/target IDs or the material-distinct port were lost.");
        TestAssert.True(result.Faces.Single(face => face.PackagePath == "LE2 to LE1 GlobalMorphs.pcc")
                .ExclusionReason!.Contains("Identical randomised data", StringComparison.Ordinal),
            "An identical port was not accounted for as a duplicate.");
        TestAssert.True(result.Markdown.Contains("LE2 to LE1 GlobalMorphs.pcc", StringComparison.Ordinal),
            "The port source was omitted from the audit.");
        TestAssert.Equal(result.Markdown, reordered.Markdown);
        TestAssert.Equal(result.Csv, reordered.Csv);
    }

    private static void ReviewedExclusionsFollowSource()
    {
        var profiles = new[]
        {
            Definition("le1-human-male", MorphFaceGame.LE1, new HashSet<string>(["A"])),
            Definition("le2-human-male", MorphFaceGame.LE2, new HashSet<string>(["A"]))
        };
        RawMorphRandomisationFace Face(MorphFaceGame game, string package, string path) =>
            RawFace(game, path, ("A", 0.5f)) with
            {
                PackagePath = package,
                MaterialScalars = Values(("Skin", 0.75f))
            };
        var faces = new[]
        {
            Face(MorphFaceGame.LE1, "LE1 GlobalMorphs.pcc", "HMM.Bad"),
            Face(MorphFaceGame.LE2, "LE1 to LE2 GlobalMorphs.pcc", "HMM.Bad"),
            Face(MorphFaceGame.LE1, "LE1 GlobalMorphs.pcc", "HMM.Sparse"),
            Face(MorphFaceGame.LE2, "LE1 to LE2 GlobalMorphs.pcc", "HMM.Sparse"),
            Face(MorphFaceGame.LE2, "LE2 GlobalMorphs.pcc", "HMM.Sparse"),
            Face(MorphFaceGame.LE1, "LE1 GlobalMorphs.pcc", "HMM.MaterialBad"),
            Face(MorphFaceGame.LE2, "LE1 to LE2 GlobalMorphs.pcc", "HMM.MaterialBad")
        };
        var exclusions = new[]
        {
            new RandomisationDonorExclusion(MorphFaceGame.LE1, "HMM.Bad", "Reviewed full exclusion."),
            new RandomisationDonorExclusion(MorphFaceGame.LE1, "HMM.Sparse", "Reviewed morph exclusion.")
            {
                Scope = RandomisationDonorExclusionScope.Morph
            },
            new RandomisationDonorExclusion(MorphFaceGame.LE1, "HMM.MaterialBad", "Reviewed material exclusion.")
            {
                Scope = RandomisationDonorExclusionScope.Material
            }
        };

        var result = RandomisationCorpusCompiler.Compile(faces, profiles, exclusions);
        var donors = result.Corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12];

        TestAssert.Equal(5, donors.Count);
        TestAssert.True(donors.All(donor => !donor.Id.EndsWith("HMM.Bad", StringComparison.Ordinal)),
            "The fully excluded source or its port remained a donor.");
        TestAssert.True(donors.Where(donor => donor.Id.StartsWith("LE1:", StringComparison.Ordinal) &&
                                              donor.Id.EndsWith("HMM.Sparse", StringComparison.Ordinal))
                .All(donor => donor.AvailableFeatures.Count == 0 &&
                              donor.NonZeroValues.Count == 0 && donor.MaterialScalars.Count > 0),
            "A reviewed morph-only source or port retained morph data or lost its materials.");
        TestAssert.True(donors.Single(donor => donor.Id == "LE2:LE2:HMM.Sparse")
                .AvailableFeatures.Count > 0,
            "A native face with the same path was excluded by the port's source rule.");
        TestAssert.True(donors.Where(donor => donor.Id.EndsWith("HMM.MaterialBad", StringComparison.Ordinal))
                .All(donor => donor.AvailableFeatures.Count > 0 &&
                              donor.NonZeroValues.Count > 0 && !donor.HasMaterialEvidence),
            "A reviewed material-only exclusion lost morph data or retained materials.");
    }

    private static void CompilationRejectsConflictingScalps()
    {
        var profile = Definition("le3-human-male", MorphFaceGame.LE3, new HashSet<string>(["A"]));
        RawMorphRandomisationFace Face(string name, string diffuse) =>
            RawFace(MorphFaceGame.LE3, $"HMM.{name}", ("A", 0.5f)) with
            {
                MaterialScalars = Values(("Skin", 0.4f)),
                MaterialTextures = new Dictionary<string, string>
                {
                    ["HED_Diff"] = "Face_Diff",
                    ["HED_Norm"] = "Face_Norm",
                    ["HED_Scalp_Diff"] = $"BIOG_HMM_HIR_PRO_R.Hair.HMM_HIR_{diffuse}_Diff",
                    ["HED_Scalp_Norm"] = "BIOG_HMM_HIR_PRO_R.Hair.HMM_HIR_Rol_Norm",
                    ["HED_Scalp_Spec"] = "BIOG_HMM_HIR_PRO_R.Hair.HMM_HIR_Rol_Mask"
                }
            };
        var compilation = RandomisationCorpusCompiler.Compile(
            [Face("Mixed", "Wil"), Face("Coherent", "Rol")], [profile], []);
        var donors = compilation.Corpus.Pools[MorphRandomisationPoolKey.HumanMaleLe12];
        var mixed = donors.Single(value => value.Id.EndsWith("HMM.Mixed", StringComparison.Ordinal));
        var coherent = donors.Single(value => value.Id.EndsWith("HMM.Coherent", StringComparison.Ordinal));
        TestAssert.True(!mixed.MaterialTextureFamilies.ContainsKey("human-scalp") &&
                        mixed.MaterialTextureFamilies.ContainsKey("human-face") &&
                        mixed.MaterialScalars["Skin"] == 0.4f && mixed.NonZeroValues["A"] == 0.5f,
            "The mixed scalp family must be dropped without excluding the donor's other data.");
        TestAssert.True(coherent.MaterialTextureFamilies.ContainsKey("human-scalp"),
            "A coherent Rollins scalp family was dropped.");
        TestAssert.True(compilation.Markdown.Contains("HMM.Mixed", StringComparison.Ordinal) &&
                        compilation.Markdown.Contains("HMM_HIR diffuse/normal style conflicts: 1", StringComparison.Ordinal),
            "The rejected family was omitted from the audit.");
    }

    private static void CursedRandomisationWakesZeroExtraChannels()
    {
        var result = CursedMorphRandomiser.CreateExtrasProposal(
            [new BoneTranslation("eye_left", Vector3.Zero)],
            Values(("Emis_Scalar", 0), ("HED_EYE_FX_Scalar", 0), ("ZeroCentred", 0)),
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["Emis_Color"] = Vector4.Zero,
                ["HED_EYE_FX_Vector"] = Vector4.Zero
            },
            100,
            321,
            [
                new MaterialRandomisationScalarBounds("Emis_Scalar", 0, 10),
                new MaterialRandomisationScalarBounds("HED_EYE_FX_Scalar", -1, 1),
                new MaterialRandomisationScalarBounds("ZeroCentred", -4, 4)
            ]);

        TestAssert.True(result.ScalarValues["Emis_Scalar"] is > 0 and <= 10,
            "Zero eye emissive remained asleep in cursed mode.");
        TestAssert.True(result.ScalarValues["HED_EYE_FX_Scalar"] is >= -1 and <= 1 and not 0,
            "Zero eye FX strength remained asleep in cursed mode.");
        TestAssert.True(result.ScalarValues["ZeroCentred"] is >= -4 and <= 4 and not 0,
            "A generic zero-centred scalar remained asleep in cursed mode.");
        TestAssert.True(result.VectorValues.Values.All(value =>
                value.X != 0 && value.Y != 0 && value.Z != 0 && value.W != 0),
            "A zero material-vector component remained asleep in cursed mode.");
        var eye = result.BoneValues.Single().Translation;
        TestAssert.True(eye.X != 0 && eye.Y != 0 && eye.Z != 0,
            "A zero facial-bone axis remained asleep in cursed mode.");
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
