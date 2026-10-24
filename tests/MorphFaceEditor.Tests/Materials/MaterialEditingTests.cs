using System.Numerics;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
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
        new("material history tolerates redundant and reordered pointer completion", MaterialHistoryToleratesPointerCompletion),
        new("material randomisation batch is atomic and reversible", MaterialRandomisationBatchIsAtomic),
        new("material vector preview applies once and cancellation preserves inheritance", VectorPreviewPreservesAuthoredOwnership),
        new("cancelled imports and normal cleanup preserve complete protected material state", CleanupAndCancellationPreserveMaterialState),
        new("LE2 and LE3 inherited eye emissive defaults stay disabled until authored", InheritedEyeEmissiveDefaultsToZero),
        new("transient texture preview stays outside authored state and history", TransientTexturePreviewStaysOutsideAuthoredState),
        new("None restores the rendered material default after replacement", NoneRestoresRenderedDefault),
        new("attachment selection rebases controls without publishing candidates or losing undo", AttachmentSelectionLifecycle),
        new("compiled empty hair support hides ineffective highlight controls and preserves authored overrides", EmptyCompiledHairSupportHidesHighlights),
        new("multi-slot attachment keeps each material's own hair diffuse", MultiSlotAttachmentKeepsOwnDiffuse),
        new("duplicate face texture overrides remain loadable", DuplicateTextureOverridesRemainLoadable)
    ];

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
        session.History.HistoryChanged += (_, _) => commits++;
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

    private static void VectorPreviewPreservesAuthoredOwnership()
    {
        var source = CreateSession();
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, source.BaseMaterials);
        var before = session.GetVector("SkinTone");
        var after = new Vector4(0.2f, 0.4f, 0.6f, 1);
        session.SetScalar("HED_Norm_Blend", 0.25f);
        session.Undo();
        session.PreviewVector("SkinTone", after);
        session.CompleteVectorPreview("SkinTone", before, after, apply: false);
        TestAssert.Equal(before, session.GetVector("SkinTone"));
        TestAssert.True(!session.CanUndo && session.CreateOverrides().Vectors.Count == 0,
            "Cancelling a vector dialog authored its transient preview.");
        TestAssert.True(session.CanRedo, "Cancelling a vector dialog discarded the previous redo branch.");
        session.Redo();
        TestAssert.Equal(0.25f, session.GetScalar("HED_Norm_Blend"));
        session.Undo();

        session.PreviewVector("SkinTone", after);
        session.CompleteVectorPreview("SkinTone", before, after, apply: true);
        TestAssert.Equal(after, session.CreateOverrides().Vectors.Single().Value);
        session.Undo();
        TestAssert.Equal(before, session.GetVector("SkinTone"));
        TestAssert.True(!session.CanUndo && session.CreateOverrides().Vectors.Count == 0,
            "Undo retained a vector override that was originally inherited.");
        session.Redo();
        TestAssert.Equal(after, session.CreateOverrides().Vectors.Single().Value);
    }

    private static void AssertOverridesEqual(MorphFaceMaterialOverrides expected, MorphFaceMaterialOverrides actual)
    {
        TestAssert.True(expected.Scalars.SequenceEqual(actual.Scalars) &&
                        expected.Vectors.SequenceEqual(actual.Vectors) &&
                        expected.Textures.SequenceEqual(actual.Textures),
            "Material history did not restore the complete authored override payload.");
    }

    private static void CleanupAndCancellationPreserveMaterialState()
    {
        var source = CreateSession();
        var material = source.BaseMaterials.Materials.Values.Single();
        var seed = material.Textures["HED_Diff"].Texture;
        DecodedTextureAsset Texture(string name) => seed with
        {
            Source = TestFixtures.CreateIdentity(name, "Texture2D"), CacheKey = name
        };
        var maps = new Dictionary<string, MaterialTextureBinding>(material.Textures, StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Tang" })
            maps[name] = new(name, Texture($"Native_{name}"));
        var defaults = new Dictionary<string, MaterialTextureBinding>(maps, StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Scalp_Diff"] = new("HED_Scalp_Diff", Texture("DefaultScalpDiffuse")),
            ["HED_Scalp_Spec"] = new("HED_Scalp_Spec", Texture("DefaultScalpSpec"))
        };
        var original = source.CreateOverrides();
        var unknown = TestFixtures.CreateIdentity("UnresolvedScalp", "Texture2D");
        var session = new MaterialEditingSession(original with
        {
            Scalars = original.Scalars.Append(new ScalarMaterialOverride("UnknownScalar", 7)).ToArray(),
            Vectors = original.Vectors.Append(new VectorMaterialOverride("UnknownVector", Vector4.UnitX)).ToArray(),
            Textures = original.Textures.Concat(new[]
            {
                new TextureMaterialOverride("HED_Scalp_Diff", maps["HED_Scalp_Diff"].Texture.Source),
                new TextureMaterialOverride("HED_Scalp_Norm", maps["HED_Scalp_Norm"].Texture.Source),
                new TextureMaterialOverride("HED_Scalp_Spec", maps["HED_Scalp_Spec"].Texture.Source),
                new TextureMaterialOverride("UnknownTexture", unknown)
            }).ToArray()
        }, new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
        {
            [material.Key] = material with { Textures = maps, DefaultTextures = defaults }
        }));
        session.SetTextureReference("HED_Scalp_Norm", Texture("EditedScalpNormal"));
        session.SetTextureReference("HED_Scalp_Spec", null);
        session.SetValues(new Dictionary<string, float> { ["HED_Norm_Blend"] = 9 },
            new Dictionary<string, Vector4> { ["SkinTone"] = new(8, 7, 6, 1) },
            new Dictionary<string, DecodedTextureAsset?> { ["HED_Diff"] = Texture("CursedFaceDiffuse") },
            MaterialBatchKind.CursedRandomisation);
        var cursed = session.CreateOverrides();
        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Tang", "UnknownTexture" };
        var references = protectedNames.ToDictionary(name => name, session.GetTextureReference);
        var previews = protectedNames.ToDictionary(name => name, name => session.GetPreviewTexture(name)?.Source);

        using (session.History.BeginAggregate())
        {
            session.ApplyMaterialData(new MorphFaceMaterialData(
                [new ScalarMaterialOverride("Imported:Scalar", 0.8f)],
                [new VectorMaterialOverride("Imported:Vector", Vector4.Zero)],
                [new TextureMaterialOverride("Imported:Texture", unknown)]),
                new Dictionary<string, DecodedTextureAsset?>());
            TestAssert.Equal(unknown,
                session.CreateOverrides().Textures.Single(value => value.Name == "Imported:Texture").TextureReference);
            session.SetScalar("Imported:Scalar", 0.6f);
            session.SetVector("Imported:Vector", Vector4.One);
            session.ResetToDefaults();
        }
        AssertOverridesEqual(cursed, session.CreateOverrides());
        AssertProtectedMaps();
        TestAssert.True(session.HasCursedRandomisation, "Cancelling an import lost the prior Cursed state.");

        var scope = new MaterialRandomisationScope(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HED_Norm_Blend" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        session.SetValues(new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.2f },
            new Dictionary<string, Vector4>(), new Dictionary<string, DecodedTextureAsset?>(),
            MaterialBatchKind.NormalRandomisation, resetScope: scope);
        var firstScoped = session.CreateOverrides();
        session.SetValues(new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.3f },
            new Dictionary<string, Vector4>(), new Dictionary<string, DecodedTextureAsset?>(),
            MaterialBatchKind.NormalRandomisation, resetScope: scope);
        var secondScoped = session.CreateOverrides();
        TestAssert.True(session.HasCursedRandomisation &&
            cursed.Scalars.Where(value => value.Name != "HED_Norm_Blend").SequenceEqual(secondScoped.Scalars.Where(value => value.Name != "HED_Norm_Blend")),
            "A partial normal roll lost remaining Cursed state or changed an unselected scalar.");
        TestAssert.True(cursed.Vectors.SequenceEqual(secondScoped.Vectors) && cursed.Textures.SequenceEqual(secondScoped.Textures),
            "A scoped normal roll reset unselected original, edited, null or unresolved material rows.");
        AssertProtectedMaps();
        session.Undo();
        AssertOverridesEqual(firstScoped, session.CreateOverrides());
        session.Undo();
        AssertOverridesEqual(cursed, session.CreateOverrides());
        session.Redo();
        session.Redo();
        AssertOverridesEqual(secondScoped, session.CreateOverrides());
        session.Undo();
        session.Undo();
        AssertOverridesEqual(cursed, session.CreateOverrides());

        session.SetValues(new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.2f },
            new Dictionary<string, Vector4>(),
            new Dictionary<string, DecodedTextureAsset?> { ["HED_Scalp_Diff"] = Texture("IgnoredProtectedDiffuse") },
            MaterialBatchKind.NormalRandomisation, protectedNames);
        var normal = session.CreateOverrides();
        AssertProtectedMaps();
        TestAssert.True(!session.HasCursedRandomisation && session.GetScalar("HED_Norm_Blend") == 0.2f &&
                        session.GetVector("SkinTone") == Vector4.One &&
                        normal.Scalars.All(value => value.Name != "UnknownScalar") &&
                        normal.Vectors.All(value => value.Name != "UnknownVector") &&
                        normal.Textures.All(value => value.Name != "HED_Diff") &&
                        session.GetPreviewTexture("HED_Diff")?.Source == seed.Source,
            "Protecting the scalp prevented unrelated Cursed material cleanup.");
        TestAssert.True(cursed.Textures.Where(value => protectedNames.Contains(value.Name))
                .SequenceEqual(normal.Textures.Where(value => protectedNames.Contains(value.Name))),
            "Normal cleanup changed the protected authored texture rows.");
        TestAssert.Equal(3, session.ChangedTextureCount);

        session.Undo();
        AssertOverridesEqual(cursed, session.CreateOverrides());
        AssertProtectedMaps();
        TestAssert.True(session.HasCursedRandomisation, "Undo cleanup did not restore Cursed state.");
        session.Redo();
        AssertOverridesEqual(normal, session.CreateOverrides());
        AssertProtectedMaps();
        TestAssert.True(!session.HasCursedRandomisation, "Redo cleanup retained Cursed state.");

        void AssertProtectedMaps()
        {
            TestAssert.True(protectedNames.All(name => session.GetTextureReference(name) == references[name] &&
                                session.GetPreviewTexture(name)?.Source == previews[name]),
                "Normal cleanup changed a protected native, edited, inherited or unresolved scalp binding.");
        }
    }

    private static void DuplicateTextureOverridesRemainLoadable()
    {
        var first = TestFixtures.CreateIdentity("FirstMask", "Texture2D");
        var second = TestFixtures.CreateIdentity("SecondMask", "Texture2D");
        var session = new MaterialEditingSession(
            new MorphFaceMaterialOverrides(null, [], [],
                [new TextureMaterialOverride("HED_Mask", first),
                 new TextureMaterialOverride("HED_Mask", second)]),
            ResolvedHeadMaterialSet.Empty);

        var overrides = session.CreateOverrides().Textures;
        TestAssert.Equal(1, overrides.Count);
        TestAssert.Equal(first, overrides[0].TextureReference);
    }

    private static void InheritedEyeEmissiveDefaultsToZero()
    {
        foreach (var game in new[] { MEGame.LE2, MEGame.LE3 })
        {
            AssertInheritedEyeEmissiveDefaultsToZero(game);
        }
    }

    private static void AssertInheritedEyeEmissiveDefaultsToZero(MEGame game)
    {
        var materialScalars = new Dictionary<string, float>
        {
            ["Emis_Scalar"] = 1.5f
        };
        var defaults = MorphFaceMaterialReader.GetEditorDefaultScalars(
            game, HeadMaterialFamily.Eyes, materialScalars);

        TestAssert.True(defaults["Emis_Scalar"] == 0,
            "LE2 inherited eye emissive strength was exposed as an active editor default.");
        TestAssert.True(materialScalars["Emis_Scalar"] == 1.5f,
            "The LE2 material scalar was rewritten instead of preserving its authored graph value.");
        TestAssert.True(MorphFaceMaterialReader.GetEditorDefaultScalars(
                MEGame.LE1, HeadMaterialFamily.Eyes, materialScalars)["Emis_Scalar"] == 1.5f,
            "The LE2/LE3 editor default policy changed LE1.");

        var identity = TestFixtures.CreateIdentity("Le2Eye", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "HMM_EYE_MASTER_OVRD_MAT",
            HeadMaterialFamily.Eyes, HeadMaterialBlendMode.Opaque, false,
            materialScalars,
            new Dictionary<string, Vector4> { ["Emis_Color"] = Vector4.UnitX },
            new Dictionary<string, MaterialTextureBinding>())
        {
            DefaultScalars = defaults
        };
        var session = new MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [material.Key] = material
            }));

        TestAssert.True(session.GetScalar("Emis_Scalar") == 0,
            "The editor session did not consume the safe LE2 eye default.");
        TestAssert.True(session.Materials.Materials.Single().Value.Scalars["Emis_Scalar"] == 0,
            "The preview still received inherited LE2 eye emissive strength.");
        TestAssert.True(session.CreateOverrides().Scalars.Count == 0,
            "An inherited LE2 emissive default became an authored face override.");

        session.SetScalar("Emis_Scalar", 1.5f);
        TestAssert.True(session.Materials.Materials.Single().Value.Scalars["Emis_Scalar"] == 1.5f,
            "An explicitly authored LE2 eye emissive value was not applied.");
        TestAssert.True(
            session.CreateOverrides().Scalars.Single(value => value.Name == "Emis_Scalar").Value == 1.5f,
            "An explicitly authored LE2 eye emissive value was not retained for save.");
    }

    private static void TransientTexturePreviewStaysOutsideAuthoredState()
    {
        var session = CreateSession();
        var replacementIdentity = TestFixtures.CreateIdentity("PreviewDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            replacementIdentity, 1, 1, [24, 192, 72, 255], "PF_B8G8R8A8",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "preview");
        var commits = 0;
        var historyChanges = 0;
        session.History.HistoryChanged += (_, _) => commits++;
        session.HistoryChanged += (_, _) => historyChanges++;

        session.PreviewTexture("HED_Diff", replacement);
        TestAssert.Equal("preview", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("FaceDiffuse", session.GetTextureReference("HED_Diff")?.InstancedPath);
        TestAssert.Equal("FaceDiffuse", session.CreateOverrides().Textures.Single().TextureReference?.InstancedPath);
        TestAssert.True(!session.CanUndo && commits == 0 && historyChanges == 0,
            "Transient texture preview changed authored history or dirty state.");

        session.PreviewTexture("HED_Diff", null);
        TestAssert.Equal("original", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        session.PreviewTexture("HED_Diff", replacement);
        session.SetTextureReference("HED_Diff", replacement);
        TestAssert.Equal("preview", session.GetSelectedTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal(1, commits);
        TestAssert.Equal(1, historyChanges);
        TestAssert.True(session.CanUndo, "Committing a preview candidate did not create one normal edit.");

        session.PreviewTexture("HED_Diff", null);
        session.Undo();
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("original", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.True(!session.CanUndo, "Undo did not return to the previous committed texture.");
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

    private static void AttachmentSelectionLifecycle()
    {
        var session = CreateSession();
        var colourA = new Vector4(0.2f, 0.1f, 0.05f, 1);
        var colourB = new Vector4(0.4f, 0.3f, 0.2f, 1);
        var hairA = HairMaterial("HairMaterialA", 1f, 2f, colourA);
        var hairB = HairMaterial("HairMaterialB", 3f, 8f, colourB);
        TestAssert.True(!session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase),
            "Hair highlight scalar appeared before a supporting attachment was selected.");
        foreach (var (materials, intensity1, intensity2, colour) in new[]
                 { (hairA, 1f, 2f, colourA), (hairB, 3f, 8f, colourB) })
        {
            session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(materials));
            TestAssert.True(session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase) &&
                            session.ScalarNames.Contains("Highlight1SpecExp_Scalar", StringComparer.OrdinalIgnoreCase) &&
                            session.VectorNames.Contains("Highlight1Color", StringComparer.OrdinalIgnoreCase),
                "Selecting a supporting attachment did not add its highlight controls.");
            TestAssert.Equal(intensity1, session.GetScalar("Hightlight1Intensity"));
            TestAssert.Equal(intensity2, session.GetScalar("Hightlight2Intensity"));
            TestAssert.Equal(colour, session.GetVector("Highlight1Color"));
            TestAssert.True(!session.CanUndo, "Selecting an unedited attachment created material history.");
        }

        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(hairA));
        session.BeginScalarEdit("Hightlight1Intensity");
        session.SetScalar("Hightlight1Intensity", 4f);
        session.EndScalarEdit("Hightlight1Intensity");
        TestAssert.True(session.CanUndo, "Editing an attachment highlight did not create semantic history.");
        var texture = session.GetSelectedTexture("HED_Diff")!;
        session.PreviewTexture("HED_Diff", texture with { CacheKey = "hover-only" });
        var original = session.Materials;
        var changes = 0;
        var historyChanges = 0;
        session.MaterialsChanged += (_, _) => changes++;
        session.HistoryChanged += (_, _) => historyChanges++;

        var empty = session.PrepareAttachmentMaterials(ResolvedHeadMaterialSet.Empty);
        TestAssert.True(!empty.Materials.Materials.Values.Any(material => material.Family == HeadMaterialFamily.Hair),
            "The candidate retained a removed attachment.");
        TestAssert.True(ReferenceEquals(original, session.Materials) &&
                        session.ScalarNames.Contains("Hightlight1Intensity"),
            "Preparing a candidate changed the live preview or controls.");
        TestAssert.Equal(0, changes);
        TestAssert.Equal(0, historyChanges);

        var replacement = session.PrepareAttachmentMaterials(hairB);
        var hair = replacement.Materials.Materials.Values.Single(material => material.Family == HeadMaterialFamily.Hair);
        TestAssert.Equal(4f, hair.Scalars["Hightlight1Intensity"]);
        TestAssert.Equal(8f, hair.Scalars["Hightlight2Intensity"]);
        session.ApplyMaterialSurface(replacement);
        TestAssert.True(ReferenceEquals(replacement.Materials, session.Materials), "Publication rebuilt the prepared materials.");
        TestAssert.Equal(texture.CacheKey, session.GetPreviewTexture("HED_Diff")?.CacheKey);
        session.ClearTexturePreview("HED_Diff");
        TestAssert.Equal(1, changes);
        TestAssert.Equal(1, historyChanges);
        TestAssert.Equal(4f, session.GetScalar("Hightlight1Intensity"));
        TestAssert.Equal(8f, session.GetScalar("Hightlight2Intensity"));
        TestAssert.True(session.CanUndo, "Replacing the attachment discarded the numeric undo entry.");
        session.Undo();
        TestAssert.Equal(1f, session.GetScalar("Hightlight1Intensity"));
        TestAssert.Equal(8f, session.GetScalar("Hightlight2Intensity"));
        TestAssert.True(!session.CanUndo, "Surface publication added a material edit to history.");
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(ResolvedHeadMaterialSet.Empty));
        TestAssert.True(!session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase) &&
                        !session.VectorNames.Contains("Highlight1Color", StringComparer.OrdinalIgnoreCase),
            "Clearing the hair attachment left unsupported highlight controls visible.");
    }

    private static void EmptyCompiledHairSupportHidesHighlights()
    {
        var identity = new AssetIdentity("C:\\game\\BioGame\\CookedPCConsole\\BioP_Char.pcc",
            "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT", -1, "Material");
        var source = new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity,
            "HMN_HED_PRO_MASTER_HAIR_MAT", HeadMaterialFamily.Hair, HeadMaterialBlendMode.Translucent, true,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Highlight1SpecExp_Scalar"] = 80,
                ["Hightlight1Intensity"] = 1.5f
            },
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["Highlight1Color"] = new(0.3f, 0.2f, 0.1f, 1)
            },
            new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase))
        {
            SupportedScalars = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SupportedVectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            HasCompiledParameterSupport = true
        };
        var authored = new MorphFaceMaterialOverrides(null,
            [new ScalarMaterialOverride("Highlight1SpecExp_Scalar", 80),
             new ScalarMaterialOverride("Hightlight1Intensity", 1.5f)],
            [new VectorMaterialOverride("Highlight1Color", new(0.3f, 0.2f, 0.1f, 1))],
            []);
        var session = new MaterialEditingSession(authored, new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase) { [source.Key] = source }));

        TestAssert.True(!session.ScalarNames.Contains("Highlight1SpecExp_Scalar", StringComparer.OrdinalIgnoreCase) &&
                        !session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase),
            "Scalars absent from the compiled Hair graph appeared as controls.");
        TestAssert.True(!session.VectorNames.Contains("Highlight1Color", StringComparer.OrdinalIgnoreCase),
            "Vectors absent from the compiled Hair graph appeared as controls.");
        var roundTrip = session.CreateOverrides();
        TestAssert.Equal(80f, roundTrip.Scalars.Single(value => value.Name == "Highlight1SpecExp_Scalar").Value);
        TestAssert.Equal(1.5f, roundTrip.Scalars.Single(value => value.Name == "Hightlight1Intensity").Value);
        TestAssert.Equal(new Vector4(0.3f, 0.2f, 0.1f, 1),
            roundTrip.Vectors.Single(value => value.Name == "Highlight1Color").Value);
    }

    private static ResolvedHeadMaterialSet HairMaterial(
        string name,
        float highlight1Intensity,
        float highlight2Intensity,
        Vector4 highlight1Color)
    {
        var identity = TestFixtures.CreateIdentity(name, "MaterialInstanceConstant");
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hightlight1Intensity"] = highlight1Intensity,
            ["Hightlight2Intensity"] = highlight2Intensity,
            ["Highlight1SpecExp_Scalar"] = 55,
            ["Highlight2SpecExp_Scalar"] = 85
        };
        var vectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Highlight1Color"] = highlight1Color,
            ["Highlight2Color"] = Vector4.One
        };
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, name,
            HeadMaterialFamily.Hair, HeadMaterialBlendMode.Translucent, true,
            scalars, vectors, new Dictionary<string, MaterialTextureBinding>())
        {
            DefaultScalars = scalars,
            DefaultVectors = vectors,
            SupportedScalars = scalars.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            SupportedVectors = vectors.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        return new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
        {
            [material.Key] = material
        });
    }

    private static void MultiSlotAttachmentKeepsOwnDiffuse()
    {
        var firstTexture = new DecodedTextureAsset(TestFixtures.CreateIdentity("HairA", "Texture2D"),
            1, 1, [255, 0, 0, 255], "PF_B8G8R8A8", TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "hair-a");
        var secondTexture = firstTexture with
        {
            Source = TestFixtures.CreateIdentity("HairB", "Texture2D"),
            CacheKey = "hair-b"
        };
        ResolvedHeadMaterial Hair(string name, DecodedTextureAsset texture)
        {
            var identity = TestFixtures.CreateIdentity(name, "MaterialInstanceConstant");
            var binding = new MaterialTextureBinding("HAIR_Diff", texture);
            var textures = new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["HAIR_Diff"] = binding
            };
            return new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity, name,
                HeadMaterialFamily.Hair, HeadMaterialBlendMode.Masked, false,
                new Dictionary<string, float>(), new Dictionary<string, Vector4>(), textures)
            { DefaultTextures = textures };
        }
        var first = Hair("HairMaterialA", firstTexture);
        var second = Hair("HairMaterialB", secondTexture);
        var materials = new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
        {
            [first.Key] = first,
            [second.Key] = second
        });
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [first.Key] = first })));
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(materials));

        TestAssert.Equal(0, session.ChangedTextureCount);
        TestAssert.Equal(0, session.CreateOverrides().Textures.Count);
        TestAssert.Equal(firstTexture.Source,
            session.Materials.Materials[first.Key].Textures["HAIR_Diff"].Texture.Source);
        TestAssert.Equal(secondTexture.Source,
            session.Materials.Materials[second.Key].Textures["HAIR_Diff"].Texture.Source);

        ResolvedHeadMaterialSet Scoped(ResolvedHeadMaterial material) => new(
            new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material with { ParameterScopeKey = "human" } });
        var custom = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, Scoped(first));
        var name = MaterialParameterControlKey.Create("human", "HAIR_Diff");
        custom.SetTextureReference(name, secondTexture);
        custom.RebaseMaterialSurface(ResolvedHeadMaterialSet.Empty);
        custom.RebaseMaterialSurface(Scoped(first));
        TestAssert.Equal(secondTexture.Source, custom.Materials.Materials[first.Key].Textures["HAIR_Diff"].Texture.Source);
        TestAssert.Equal(secondTexture.Source, custom.CreateOverrides().Textures.Single().TextureReference);
        custom.Undo();
        TestAssert.Equal(firstTexture.Source, custom.Materials.Materials[first.Key].Textures["HAIR_Diff"].Texture.Source);

    }
}
