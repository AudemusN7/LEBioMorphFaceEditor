using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Core.Randomisation;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiRandomisationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("normal roll clears the Cursed state atomically and undo restores it", NormalRollClearsCursedState),
        new("awaitable randomisation propagates seed failures without partial edits", AwaitableRandomisationPropagatesFailure),
        new("failed Cursed randomisation rolls back its partial edit", FailedCursedRandomisationRollsBack),
        new("manual scalp preparation keeps its required pair when an optional map fails", ManualHairScalpPromptKeepsRequiredPairWhenOptionalMapFails),
        new("exhausted texture randomisation retains numeric values", ExhaustedTextureRandomisationIsReported)
    ];

    private static void NormalRollClearsCursedState()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader, randomSeedFactory: () => 771,
            includeUnusedMaterialParameters: true);
        using var clean = CreateRandomisationEditor(reader, randomSeedFactory: () => 771,
            includeUnusedMaterialParameters: true);
        editor.CursedMode = true;
        editor.RandomisationStrength = 100;
        editor.RandomiseAsync().GetAwaiter().GetResult();
        var cursed = editor.CreateDraft();
        TestAssert.True(editor.Material.Scalars.Single(value => value.Name == "HED_Scalp_Mask_OverlayKill_Scalar").Value != 1,
            "The Cursed setup did not affect a parameter absent from the normal corpus.");
        editor.CursedMode = false;
        TestAssert.True(MorphFaceEditorStateComparer.Equals(cursed, editor.CreateDraft()),
            "Changing modes rewrote the authored head before another roll.");

        clean.RandomisationStrength = 100;
        clean.RandomiseMaterials = true;
        clean.MaterialRandomisationStrength = editor.MaterialRandomisationStrength = 37;
        clean.SetToDefaultsCommand.Execute(null);
        clean.RandomiseAsync().GetAwaiter().GetResult();
        editor.RandomiseAsync().GetAwaiter().GetResult();
        var normal = editor.CreateDraft();
        var expected = clean.CreateDraft();
        TestAssert.True(expected.MorphFeatures.SequenceEqual(normal.MorphFeatures), "Normal morph values retained Cursed state.");
        foreach (var bone in expected.FinalSkeleton)
            TestAssert.Near(bone.Translation, normal.FinalSkeleton.Single(value => value.BoneName == bone.BoneName).Translation, 0.0001f);
        foreach (var scalar in clean.Material.Scalars)
            TestAssert.Near(scalar.Value, editor.Material.Scalars.Single(value => value.Name == scalar.Name).Value, 0);
        foreach (var vector in clean.Material.Vectors)
            TestAssert.Equal(vector.Value, editor.Material.Vectors.Single(value => value.Name == vector.Name).Value);
        editor.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(cursed, editor.CreateDraft()), "Undo did not restore the complete Cursed state.");
        editor.RedoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(normal, editor.CreateDraft()), "Redo did not restore the normal state.");
    }

    private static void AwaitableRandomisationPropagatesFailure()
    {
        using var reader = new MorphFacePackageReader();
        var expected = new InvalidOperationException("QA seed factory failure");
        using var editor = CreateRandomisationEditor(reader, randomSeedFactory: () => throw expected);
        editor.CursedMode = true;
        var before = editor.CreateDraft();
        try
        {
            editor.RandomiseAsync().GetAwaiter().GetResult();
            throw new Exception("Awaited randomisation swallowed its failure.");
        }
        catch (InvalidOperationException actual)
        {
            TestAssert.True(ReferenceEquals(actual, expected), "Automation lost the original randomisation error.");
        }
        TestAssert.True(MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()) && !editor.UndoCommand.CanExecute(null),
            "A failed awaited randomisation committed a partial edit.");
    }

    private static void FailedCursedRandomisationRollsBack()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader, extremeBoneOffset: true);
        editor.CursedMode = true;
        editor.RandomisationStrength = 100;

        editor.RandomiseCommand.Execute(null);

        var draft = editor.CreateDraft();
        TestAssert.Near(0, draft.GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.True(draft.FinalSkeleton.All(value =>
                float.IsFinite(value.Translation.X) && float.IsFinite(value.Translation.Y) &&
                float.IsFinite(value.Translation.Z)),
            "A failed cursed click left a non-finite skeleton behind.");
        TestAssert.True(!editor.UndoCommand.CanExecute(null),
            "A failed cursed click committed a partial history step.");
    }

    private static void ManualHairScalpPromptKeepsRequiredPairWhenOptionalMapFails()
    {
        using var sourceFile = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        var family = MorphRandomisationCatalog.LoadEmbedded()
            .CompatibleMaterialDonors("le3-human-male")
            .Select(value => value.MaterialTextureFamilies.GetValueOrDefault("human-scalp"))
            .First(value => value is not null && value.TryGetValue("HED_Scalp_Spec", out var spec) &&
                            spec.Contains("GBL_ARM_ALL_Black", StringComparison.OrdinalIgnoreCase))!;
        const string packagePath = "working.pcc";
        var currentTextures = family.Keys.ToDictionary(
            name => name,
            name => new MaterialTextureBinding(name, CreateTestTexture(packagePath, $"Current.{name}", name)),
            StringComparer.OrdinalIgnoreCase);
        var materialIdentity = new AssetIdentity(packagePath, "Working.ScalpMaterial", 1, "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity), materialIdentity, "HMM_HED_PRONPC_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(), new Dictionary<string, Vector4>(), currentTextures)
        {
            SupportedTextures = family.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        var session = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
        var candidates = family.Values.Select((path, index) =>
        {
            var occurrence = new TextureCatalogOccurrence(
                sourceFile.Name, 42 + index, 0, TextureCatalogOrigin.BaseGame, 1, 1,
                "PF_B8G8R8A8", "TEXTUREGROUP_Character", false, null);
            return new TextureCatalogCandidate(MorphFaceGame.LE3, path, occurrence, [occurrence])
            { IsEligibleOfficialDefault = true };
        }).ToArray();
        using var editor = new MaterialEditorViewModel(
            session, new StubColorDialog(), new ImmediateTextureLoader("GBL_ARM_ALL_Black"), packagePath, [],
            message => throw new Exception(message), new HumanMaleFeatureMetadataCatalog(),
            candidates, TextureCatalogProfile.Empty, true);

        var prepared = editor.PrepareRandomisationAsync(
                new Dictionary<string, float>(),
                new Dictionary<string, Vector4>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                    { ["manual|human-scalp"] = family })
            .GetAwaiter().GetResult();

        TestAssert.Equal(1, prepared.AppliedTextureFamilies);
        TestAssert.True(prepared.FailedTextureFamilySignatures.Count == 0,
            "An unresolved optional scalp map rejected the manual prompt family.");
        TestAssert.True(prepared.Textures.ContainsKey("HED_Scalp_Diff") &&
                        prepared.Textures.ContainsKey("HED_Scalp_Norm"),
            "The manual prompt must still apply the required Diff/Norm pair.");
        TestAssert.True(!prepared.Textures.ContainsKey("HED_Scalp_Spec"),
            "The unresolved optional Mask should be omitted from the prompt result.");
    }

    private static void ExhaustedTextureRandomisationIsReported()
    {
        TestAssert.True(EditorRandomisationService.WereAllTextureFamiliesRejected(
                eligibleSignatureCount: 2,
                excludedSignatureCount: 2,
                appliedTextureFamilies: 0),
            "Exhausting every eligible texture family was silently treated as a complete randomisation.");
        TestAssert.True(!EditorRandomisationService.WereAllTextureFamiliesRejected(2, 1, 1),
            "A successful fallback texture family was incorrectly reported as exhausted.");
        TestAssert.True(!EditorRandomisationService.WereAllTextureFamiliesRejected(0, 0, 0),
            "A numeric-only material randomisation was incorrectly reported as a texture failure.");
    }
}
