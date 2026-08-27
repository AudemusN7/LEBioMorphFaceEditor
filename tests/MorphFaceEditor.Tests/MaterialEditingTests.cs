using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the material editing regression set.
public static class MaterialEditingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("material scalar drag and HDR colour use semantic history", MaterialHistoryIsSemantic),
        new("material overrides are effective before the first edit", MaterialOverridesAreInitiallyEffective),
        new("material history tolerates redundant and reordered pointer completion", MaterialHistoryToleratesPointerCompletion),
        new("material randomisation batch is atomic and reversible", MaterialRandomisationBatchIsAtomic),
        new("HDR picker previews live and commits once on Apply", HdrPreviewCommitsOnce),
        new("package texture reference updates detached bindings and undoes", PackageTextureReferenceUpdatesBindings),
        new("package texture reference supports None and undo", PackageTextureReferenceSupportsNone),
        new("None restores the rendered material default after replacement", NoneRestoresRenderedDefault),
        new("failed attachment replacement can restore material state", AttachmentMaterialStateRestores)
    ];

    private static void MaterialHistoryIsSemantic()
    {
        var session = CreateSession();
        session.BeginScalarEdit("HED_Norm_Blend");
        session.SetScalar("HED_Norm_Blend", 0.25f);
        session.SetScalar("HED_Norm_Blend", 0.75f);
        TestAssert.True(!session.CanUndo, "Material slider drag was committed before release.");
        session.EndScalarEdit("HED_Norm_Blend");
        TestAssert.True(session.CanUndo, "Material slider drag did not create history.");
        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);

        var edited = new Vector4(2, 0.2f, 0.1f, 1);
        session.SetVector("SkinTone", edited);
        TestAssert.Equal(edited, session.GetVector("SkinTone"));
        session.Undo();
        TestAssert.Equal(Vector4.One, session.GetVector("SkinTone"));
    }

    private static void MaterialOverridesAreInitiallyEffective()
    {
        var identity = TestFixtures.CreateIdentity("TurianHead", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "TUR_HED_PRO_MASTER_MAT",
            HeadMaterialFamily.TurianSkin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["TUR_HED_Spwr_Skin_Scalar"] = 0 },
            new Dictionary<string, Vector4> { ["TUR_HED_Spec_Colour"] = Vector4.Zero },
            new Dictionary<string, MaterialTextureBinding>());
        var overrides = new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("TUR_HED_Spwr_Skin_Scalar", 0.75f)],
            [new VectorMaterialOverride("TUR_HED_Spec_Colour", Vector4.One)],
            []);

        var session = new MaterialEditingSession(overrides, new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
        var effective = session.Materials.Materials.Values.Single();

        TestAssert.Near(0.75f, effective.Scalars["TUR_HED_Spwr_Skin_Scalar"], 1e-6f);
        TestAssert.Equal(Vector4.One, effective.Vectors["TUR_HED_Spec_Colour"]);
    }

    private static void MaterialHistoryToleratesPointerCompletion()
    {
        var session = CreateSession();
        session.BeginScalarEdit("HED_Norm_Blend");
        session.SetScalar("HED_Norm_Blend", 0.75f);
        session.EndScalarEdit("HED_Norm_Blend");
        session.EndScalarEdit("HED_Norm_Blend");
        TestAssert.True(session.CanUndo, "A redundant pointer release discarded the edit.");
        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);

        session.BeginScalarEdit("HED_Norm_Blend");
        session.SetScalar("HED_Norm_Blend", 0.25f);
        session.BeginScalarEdit("HED_Addn_Blend_Scalar");
        session.SetScalar("HED_Addn_Blend_Scalar", 0.8f);
        session.EndScalarEdit("HED_Norm_Blend");
        session.EndScalarEdit("HED_Addn_Blend_Scalar");
        session.Undo();
        TestAssert.Near(0f, session.GetScalar("HED_Addn_Blend_Scalar"), 1e-6f);
        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
    }

    private static void HdrPreviewCommitsOnce()
    {
        var session = CreateSession();
        var commits = 0;
        session.EditCommitted += (_, _) => commits++;
        var before = session.GetVector("SkinTone");
        session.PreviewVector("SkinTone", new Vector4(0.8f, 0.2f, 0.1f, 1));
        session.PreviewVector("SkinTone", new Vector4(1.4f, 0.4f, 0.2f, 1));
        TestAssert.True(!session.CanUndo, "Live HDR preview created undo entries before Apply.");
        var after = session.GetVector("SkinTone");
        session.CompleteVectorPreview("SkinTone", before, after, apply: true);
        TestAssert.Equal(1, commits);
        session.Undo();
        TestAssert.Equal(before, session.GetVector("SkinTone"));

        session.PreviewVector("SkinTone", new Vector4(4, 3, 2, 1));
        session.CompleteVectorPreview("SkinTone", before, session.GetVector("SkinTone"), apply: false);
        TestAssert.Equal(before, session.GetVector("SkinTone"));
        TestAssert.Equal(1, commits);
    }

    private static void MaterialRandomisationBatchIsAtomic()
    {
        var session = CreateSession();
        var replacementIdentity = TestFixtures.CreateIdentity("RandomisedDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            replacementIdentity, 1, 1, [48, 96, 144, 255], "PF_B8G8R8A8",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false,
            "randomised");
        var commits = 0;
        var refreshes = 0;
        session.EditCommitted += (_, _) => commits++;
        session.MaterialsChanged += (_, args) =>
        {
            if (args.Kind == MaterialChangeKind.Full) refreshes++;
        };

        session.SetValues(
            new Dictionary<string, float>
            {
                ["HED_Norm_Blend"] = 0.2f,
                ["HED_Addn_Blend_Scalar"] = 0.7f
            },
            new Dictionary<string, Vector4> { ["SkinTone"] = new(0.3f, 0.5f, 0.8f, 1) },
            new Dictionary<string, DecodedTextureAsset?> { ["HED_Diff"] = replacement });

        TestAssert.Equal(1, commits);
        TestAssert.Equal(1, refreshes);
        TestAssert.Near(0.2f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        TestAssert.Equal("randomised", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        TestAssert.Near(0f, session.GetScalar("HED_Addn_Blend_Scalar"), 1e-6f);
        TestAssert.Equal(Vector4.One, session.GetVector("SkinTone"));
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        session.Redo();
        TestAssert.Near(0.2f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        TestAssert.Equal("randomised", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        try
        {
            session.SetValues(
                new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.9f },
                new Dictionary<string, Vector4> { ["UnknownVector"] = Vector4.Zero },
                new Dictionary<string, DecodedTextureAsset?>());
            throw new InvalidOperationException("Invalid material batch was accepted.");
        }
        catch (ArgumentException)
        {
            TestAssert.Near(0.2f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        }
    }

    private static void PackageTextureReferenceUpdatesBindings()
    {
        var session = CreateSession();
        var identity = TestFixtures.CreateIdentity("ReplacementDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            identity, 2, 2, Enumerable.Repeat((byte)200, 16).ToArray(), "PF_DXT1",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "package:test");
        session.SetTextureReference("HED_Diff", replacement);
        TestAssert.Equal("package:test", session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey);
        TestAssert.Equal(1, session.ChangedTextureCount);
        session.Undo();
        TestAssert.Equal("original", session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey);
        TestAssert.Equal(0, session.ChangedTextureCount);
    }

    private static void PackageTextureReferenceSupportsNone()
    {
        var session = CreateSession();
        session.SetTextureReference("HED_Diff", null);

        TestAssert.True(
            session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey == "original",
            "Selecting None did not restore the material's default texture in the preview.");
        TestAssert.True(
            session.CreateOverrides().Textures.All(value => value.Name != "HED_Diff"),
            "Selecting None retained a package override instead of using the material default.");
        TestAssert.Equal(1, session.ChangedTextureCount);

        session.Undo();
        TestAssert.Equal("original", session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey);
        TestAssert.Equal(0, session.ChangedTextureCount);
        session.Redo();
        TestAssert.True(
            session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey == "original",
            "Redo did not restore the material default for None.");
    }

    private static void NoneRestoresRenderedDefault()
    {
        var session = CreateSession();
        var baseline = ToPreview(session.Materials.Materials.Values.Single());
        var identity = TestFixtures.CreateIdentity("ReplacementDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            identity, 1, 1, [16, 220, 24, 255], "PF_B8G8R8A8",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "replacement");
        var (renderer, camera) = CreateTriangleRenderer(baseline);
        using (renderer)
        {
            var originalFrame = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            session.SetTextureReference("HED_Diff", replacement);
            var changed = ToPreview(session.Materials.Materials.Values.Single());
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [changed.Key] = changed });
            var changedFrame = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            TestAssert.True(!originalFrame.SequenceEqual(changedFrame), "The replacement texture did not render.");

            session.SetTextureReference("HED_Diff", null);
            var restored = ToPreview(session.Materials.Materials.Values.Single());
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [restored.Key] = restored });
            var restoredFrame = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(originalFrame.SequenceEqual(restoredFrame),
                "Selecting None left the previously selected texture in the renderer.");
        }

        static HeadPreviewMaterial ToPreview(ResolvedHeadMaterial material) => new(
            material.Key,
            material.Source.InstancedPath,
            HeadMaterialFamily.Unknown,
            HeadMaterialBlendMode.Opaque,
            false,
            material.Scalars,
            material.Vectors,
            material.Textures.ToDictionary(
                value => value.Key,
                value => new HeadPreviewTexture(
                    value.Value.Texture.CacheKey,
                    value.Key,
                    value.Value.Texture.Width,
                    value.Value.Texture.Height,
                    value.Value.Texture.Rgba8,
                    value.Value.Texture.Role,
                    value.Value.Texture.ColorSpace,
                    value.Value.Texture.AlphaPolicy,
                    value.Value.Texture.HasMeaningfulAlpha),
                StringComparer.OrdinalIgnoreCase));
    }

    private static void AttachmentMaterialStateRestores()
    {
        var session = CreateSession();
        var original = session.Materials;
        var snapshot = session.CaptureAttachmentState();
        session.ReplaceAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase)));
        session.RestoreAttachmentState(snapshot);

        TestAssert.Equal(original.Materials.Count, session.Materials.Materials.Count);
        TestAssert.Equal(original.Materials.Keys.Single(), session.Materials.Materials.Keys.Single());
        TestAssert.Equal(snapshot.TextureReferences.Count, session.ChangedTextureCount);
    }
}
