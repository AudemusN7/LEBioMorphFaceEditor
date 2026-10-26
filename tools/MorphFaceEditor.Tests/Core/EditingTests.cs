using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using System.Numerics;

namespace MorphFaceEditor.Tests;

/// <summary>Checks cross-domain history, dirty-state ownership and semantic profile transfer.</summary>
public static class EditingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("mixed morph material and bone history round-trips in semantic order", MixedMorphMaterialBoneHistoryRoundTrips),
        new("cancelled composite retains redo and restores values after a failing preview callback", CancelledCompositeRetainsRedo),
        new("morph paste replaces the redo branch with one complete undoable action", MorphPasteReplacesRedoWithOneAction),
        new("dirty comparison tracks authored state and ignores derived LODs", DirtyComparisonTracksAuthoredState),
        new("full import oracle checks lower LODs without changing rejected edits", FullImportOracleChecksEveryLod),
        new("Fix Morph rejects unsafe repair candidates", FixMorphRejectsUnsafeCandidates),
        new("fix morph rebuilds all compatible LODs and re-enables editing", FixMorphRebuildsCompatibleLods),
        new("Fix Morph remains atomic when a notification callback fails", FixMorphSurvivesNotificationFailure),
        new("semantic transfer rebakes destination geometry and preserves bone residuals", SemanticTransferRebakesDestinationProfile),
        new("fixed-bake mode preserves imported geometry and authored morphs while editing bones", FixedBakePreservesDraftAndEditsBones),
        new("per-bone reset restores loaded translation as one semantic edit", BoneTranslationResetRestoresLoadedValue),
        new("relative-bake preserves authored LODs and applies deltas from a stable baseline", RelativeBakeDoesNotAccumulate),
        new("relative-bake preserves an unproven lower LOD", RelativeBakePreservesUnprovenLowerLod),
        new("morph-only feature import restores target-driven bones and preserves independent bone edits", MorphOnlyPreservesBoneEdits),
        new("relative-bake disables on incompatible authored LOD0 topology", RelativeBakeRejectsIncompatibleTopology),
        new("base-mesh-only and blocked profiles do not expose bone editing", BaseMeshOnlyDoesNotExposeBones),
    ];

    private static void FullImportOracleChecksEveryLod()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        session.SetFeature("Target", 1f);
        var desired = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        session.Undo();
        var before = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        var lods = desired.BakedLods.Select(lod => lod.ToArray()).ToArray();
        lods[1][0] += Vector3.One;
        var events = 0;
        session.EvaluationChanged += (_, _) => events++;
        TestAssert.True(!session.TryImportMatchingMorphData(new(desired.MorphFeatures, desired.FinalSkeleton, lods)),
            "A lower-LOD mismatch was accepted by the lightweight import.");
        TestAssert.True(events == 0 && session.CanRedo && MorphFaceEditorStateComparer.Equals(before,
            session.CreateDraft(null, MorphFaceMaterialOverrides.Empty)), "Rejected comparison changed the live edit or its history.");
        lods[1][0] = desired.BakedLods[1][0] + new Vector3(0.005f, 0, 0);
        TestAssert.True(session.TryImportMatchingMorphData(new(desired.MorphFeatures, desired.FinalSkeleton, lods)),
            "A reproducible head within the standard tolerance was rejected.");
        session.Undo();
        TestAssert.True(MorphFaceEditorStateComparer.Equals(before, session.CreateDraft(null, MorphFaceMaterialOverrides.Empty)),
            "The matching geometry import was not undoable.");
    }

    private static void MorphPasteReplacesRedoWithOneAction()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        var contribution = new Vector3(2, 2, 4);
        session.SetRandomisedBones([new BoneTranslation("root", new Vector3(2, 3, 4))],
            new Dictionary<string, Vector3> { ["root"] = contribution });
        var before = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        var beforeGeometry = session.CaptureGeometryEdits();

        session.SetFeature("Target", 1f);
        var copiedDraft = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        var copied = new MorphFaceMorphData(copiedDraft.MorphFeatures, copiedDraft.FinalSkeleton, copiedDraft.BakedLods);
        session.Undo();
        AssertDraft(before, "Undo did not restore the pre-copy state.");
        TestAssert.True(session.CanRedo, "The setup did not retain the copied edit as a redo branch.");

        try
        {
            session.ApplyMorphData(copied with { FinalSkeleton = [new BoneTranslation("UnknownBone", Vector3.Zero)] });
            throw new Exception("The invalid paste was accepted.");
        }
        catch (InvalidOperationException) { }
        AssertDraft(before, "A rejected paste changed authored data.");
        TestAssert.True(session.CanRedo && session.GeometryEditsMatch(beforeGeometry) &&
                        session.CursedBoneOffsets["root"] == contribution,
            "A rejected paste changed redo or hidden bone state.");

        session.ApplyMorphData(copied);
        AssertDraft(copiedDraft, "Paste did not restore the copied features, final skeleton and baked LODs.");
        TestAssert.True(session.CanUndo && !session.CanRedo && session.CursedBoneOffsets.Count == 0,
            "Paste did not replace the redo branch or retained obsolete Cursed contributions.");
        var pastedGeometry = session.CaptureGeometryEdits();
        session.ApplyMorphData(copied);
        session.Undo();
        AssertDraft(before, "One Undo did not remove the paste, or an unchanged paste added an extra action.");
        TestAssert.True(session.GeometryEditsMatch(beforeGeometry) && session.CursedBoneOffsets["root"] == contribution,
            "Undo lost the original template, pasted-bone names or Cursed contributions.");
        session.Redo();
        AssertDraft(copiedDraft, "Redo did not restore the complete pasted state.");
        TestAssert.True(session.GeometryEditsMatch(pastedGeometry) && session.CursedBoneOffsets.Count == 0,
            "Redo lost the pasted bone representation or restored obsolete contributions.");
        void AssertDraft(MorphFaceDocument expected, string message)
        {
            var actual = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(expected, actual) &&
                            expected.BakedLods.Count == actual.BakedLods.Count &&
                            expected.BakedLods.Zip(actual.BakedLods).All(lod => lod.First.SequenceEqual(lod.Second)), message);
        }
    }

    private static void RelativeBakeDoesNotAccumulate()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        TestAssert.True(session.Evaluation.Geometry.Positions.SequenceEqual(document.BakedLods[0]) &&
                        session.Evaluation.LodGeometry[1].Positions.SequenceEqual(document.BakedLods[1]),
            "Opening a relative-bake session replaced authored geometry.");
        session.SetFeature("Target", 1f);
        session.SetFeature("Target", 2f);
        session.SetFeature("Target", 1f);

        TestAssert.Near(new Vector3(11, 2, 3), session.Evaluation.Geometry.Positions[1], 0.000001f);
        var draft = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        TestAssert.Near(new Vector3(11, 2, 3), draft.BakedLods[0][1], 0.000001f);
        TestAssert.Near(new Vector3(102, 0, 0), draft.BakedLods[1][1], 0.000001f);
    }

    private static void MorphOnlyPreservesBoneEdits()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        session.SetBoneAxis("root", 0, 3f);
        var baseline = session.Evaluation.FinalSkeleton.ToArray();
        var authored = session.CaptureGeometryEdits();
        session.SetFeature("Target", 1f);
        TestAssert.True(!baseline.SequenceEqual(session.Evaluation.FinalSkeleton), "Fixture did not exercise target-driven bone movement.");
        session.ImportFeatures(new Dictionary<string, float> { ["Target"] = 0.5f });
        TestAssert.True(session.GeometryEditsMatch(authored), "Feature import changed independent bone/geometry edits.");
        TestAssert.True(baseline.SequenceEqual(session.Evaluation.FinalSkeleton), "Restored features did not restore evaluated bones.");
        var beforeMetadata = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        session.ImportFeatures(new Dictionary<string, float> { ["UnknownAuthoredMetadata"] = 0f });
        session.Undo();
        TestAssert.True(MorphFaceEditorStateComparer.Equals(beforeMetadata,
                session.CreateDraft(null, MorphFaceMaterialOverrides.Empty)),
            "Undo retained the explicit-zero metadata introduced by the import.");
        session.Redo();
        TestAssert.True(session.CreateDraft(null, MorphFaceMaterialOverrides.Empty).MorphFeatures.Any(value =>
                value.Name == "UnknownAuthoredMetadata" && value.Offset == 0),
            "Redo lost imported explicit-zero metadata.");
    }

    private static void RelativeBakePreservesUnprovenLowerLod()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var unprovenTarget = target with
        {
            Lods =
            [
                target.Lods[0],
                new MorphTargetLod(1, 4, [new MorphVertexDelta(1, new Vector3(100, 0, 0), Vector3.Zero)])
            ]
        };
        var session = CreateRelativeSession(mesh, document, unprovenTarget);
        session.SetFeature("Target", 1f);

        TestAssert.True(session.CanEditMorphFeatures,
            "An incompatible lower LOD incorrectly disabled a compatible LOD0.");
        TestAssert.True(session.Evaluation.LodGeometry[1].Positions.SequenceEqual(document.BakedLods[1]),
            "An unproven lower LOD was modified by relative-bake editing.");
        TestAssert.True(session.CreateDraft(null, MorphFaceMaterialOverrides.Empty)
            .BakedLods[1].SequenceEqual(document.BakedLods[1]),
            "CreateDraft rewrote an unproven lower LOD.");
    }

    private static void RelativeBakeRejectsIncompatibleTopology()
    {
        var (mesh, document, _) = CreateRelativeBakeFixture();
        var incompatibleTarget = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Target", "MorphTarget"),
            [new MorphTargetLod(0, 4, [new MorphVertexDelta(1, Vector3.UnitX, Vector3.Zero)])],
            []);
        var session = CreateRelativeSession(mesh, document, incompatibleTarget);
        TestAssert.True(!session.CanEditMorphFeatures,
            "Incompatible authored LOD0 topology exposed relative-bake morph editing.");
        TestAssert.True(session.CanEditBones,
            "Incompatible morph topology disabled the independently verified Player bone rig.");
    }

    private static MorphFaceEditingSession CreateRelativeSession(
        SkeletalMeshAsset mesh,
        MorphFaceDocument document,
        MorphTargetAsset target) => new(
        document,
        mesh,
        [target],
        geometryMode: MorphFaceGeometryMode.RelativeBake);

    private static (SkeletalMeshAsset Mesh, MorphFaceDocument Document, MorphTargetAsset Target)
        CreateRelativeBakeFixture()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Target", "MorphTarget"),
            [
                new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(2, 0, 0), Vector3.Zero)]),
                new MorphTargetLod(1, 3, [new MorphVertexDelta(1, new Vector3(4, 0, 0), Vector3.Zero)])
            ],
            [new MorphTargetBoneOffset("root", new Vector3(0, 2, 0))]);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "relative-bake"),
            mesh.Source,
            null,
            [new MorphFeatureValue("Target", 0.5f)],
            [new BoneTranslation("root", new Vector3(0, 1, 0))],
            MorphFaceMaterialOverrides.Empty,
            [
                [new Vector3(10, 0, 0), new Vector3(10, 2, 3), new Vector3(9, -2, -3)],
                [new Vector3(100, 0, 0), new Vector3(100, 0, 0), new Vector3(100, 2, 0)]
            ],
            []);
        return (mesh, document, target);
    }

    private static void FixedBakePreservesDraftAndEditsBones()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var baked = new[]
        {
            new Vector3(20, 0, 0),
            new Vector3(21, 2, 3),
            new Vector3(19, -2, -3)
        };
        var document = CreateFixedBakeDocument(mesh, baked, featureOffset: 0.75f, bone: new Vector3(3, 4, 5));
        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [TestFixtures.CreateTarget(new MorphVertexDelta(1, new Vector3(100, 0, 0), Vector3.Zero))],
            geometryMode: MorphFaceGeometryMode.FixedBake);

        TestAssert.True(!session.CanEditMorphFeatures,
            "Fixed-bake mode exposed morph editing.");
        TestAssert.True(session.CanEditBones, "A fixed-bake session with a valid skeleton hid bone editing.");
        TestAssert.Near(baked[0], session.Evaluation.Geometry.Positions[0], 0.000001f);
        session.SetBoneAxis("root", 0, 7);

        var draft = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        TestAssert.True(draft.MorphFeatures.SequenceEqual(document.MorphFeatures),
            "Fixed-bake save changed authored morph features.");
        TestAssert.True(draft.BakedLods.Count == 1 && draft.BakedLods[0].SequenceEqual(baked),
            "Fixed-bake save changed imported baked geometry.");
        TestAssert.Near(new Vector3(7, 4, 5), draft.FinalSkeleton.Single().Translation, 0.000001f);
        TestAssert.Near(baked[0], session.Evaluation.LodGeometry[0].Positions[0], 0.000001f);
    }

    private static void BoneTranslationResetRestoresLoadedValue()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var document = CreateFixedBakeDocument(mesh, mesh.Positions, featureOffset: 0.25f, bone: new Vector3(3, 4, 5));
        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            geometryMode: MorphFaceGeometryMode.FixedBake);

        for (var value = 0; value < 5; value++)
        {
            session.SetBoneAxis("root", 0, 10 + value);
            session.SetBoneAxis("root", 1, 20 + value);
        }

        session.ResetBoneTranslation("root");
        TestAssert.Near(new Vector3(3, 4, 5), session.FinalSkeleton.Single().Translation, 0.000001f);
        session.Undo();
        TestAssert.Near(new Vector3(14, 24, 5), session.FinalSkeleton.Single().Translation, 0.000001f);
        session.Redo();
        TestAssert.Near(new Vector3(3, 4, 5), session.FinalSkeleton.Single().Translation, 0.000001f);

        var (relativeMesh, relativeDocument, target) = CreateRelativeBakeFixture();
        var relative = CreateRelativeSession(relativeMesh, relativeDocument, target);
        var initiallyLoaded = relative.FinalSkeleton.Single().Translation;
        relative.SetFeature("Target", 1f);
        relative.SetBoneAxis("root", 1, 9);
        relative.ResetBoneTranslation("root");
        TestAssert.Near(initiallyLoaded, relative.FinalSkeleton.Single().Translation, 0.000001f);
    }

    private static void BaseMeshOnlyDoesNotExposeBones()
    {
        var mesh = TestFixtures.CreateMesh();
        var document = CreateFixedBakeDocument(mesh, mesh.Positions, featureOffset: 1, bone: Vector3.Zero);
        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            geometryMode: MorphFaceGeometryMode.BaseMeshOnly);
        TestAssert.True(!session.CanEditMorphFeatures && !session.CanEditBones,
            "Base-mesh-only mode exposed an editing capability.");
        var threw = false;
        try
        {
            session.SetBoneAxis("root", 0, 1);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        TestAssert.True(threw, "Base-mesh-only mode accepted a bone edit.");

        var fixedWithoutWeights = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            geometryMode: MorphFaceGeometryMode.FixedBake);
        TestAssert.True(!fixedWithoutWeights.CanEditBones,
            "A fixed-bake mesh without verified render weights exposed bone editing.");

        var blocked = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            geometryEditBlockReason: "Material-only profile.");
        TestAssert.True(!blocked.CanEditBones,
            "A geometry-blocked morph session accidentally exposed bone editing.");
    }

    private static MorphFaceDocument CreateFixedBakeDocument(
        SkeletalMeshAsset mesh,
        IReadOnlyList<Vector3> baked,
        float featureOffset,
        Vector3 bone) => new(
        TestFixtures.CreateIdentity("Face", "BioMorphFace"),
        new PackageFingerprint(1, DateTime.UnixEpoch, "fixed-bake"),
        mesh.Source,
        null,
        [new MorphFeatureValue("Target", featureOffset)],
        [new BoneTranslation("root", bone)],
        MorphFaceMaterialOverrides.Empty,
        [baked.ToArray()],
        []);

    private static void FixMorphRebuildsCompatibleLods()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh(lowerLodIndex: 2);
        var hair = TestFixtures.CreateIdentity("Hair", "SkeletalMesh");
        var otherMesh = TestFixtures.CreateIdentity("Visor", "SkeletalMesh") with { UIndex = 2 };
        var material = new MorphFaceMaterialOverrides(
            TestFixtures.CreateIdentity("MaterialOverride", "BioMaterialOverride") with { UIndex = 3 },
            [new ScalarMaterialOverride("Complexion", 0.75f)],
            [new VectorMaterialOverride("SkinTone", new Vector4(0.1f, 0.2f, 0.3f, 1))],
            []);
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Shape", "MorphTarget"),
            [
                new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(2, 0, 0), Vector3.Zero)]),
                new MorphTargetLod(2, 3, [new MorphVertexDelta(1, new Vector3(0, 0, 2), Vector3.Zero)])
            ],
            []);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "face"),
            mesh.Source,
            hair,
            [new MorphFeatureValue("Shape", 0.5f)],
            [new BoneTranslation("root", new Vector3(3, 4, 5))],
            material,
            [
                [Vector3.Zero, new Vector3(3, 0, 0), Vector3.UnitY],
                [new Vector3(10, 0, 0), new Vector3(12.5f, 0, 0), new Vector3(10, 2, 0)]
            ],
            ["m_aMorphFeatures", "m_aFinalSkeleton"])
        {
            OtherMeshReferences = [otherMesh]
        };
        var session = new MorphFaceEditingSession(document, mesh, [target]);

        TestAssert.True(!session.CanEditMorphFeatures, "The intentionally malformed face was editable before repair.");
        TestAssert.True(session.CanFixMorph, session.EditBlockReason ?? "The malformed face was not offered repair.");

        session.FixMorph();
        var draft = session.CreateDraft(hair, [otherMesh], material);

        TestAssert.True(session.CanEditMorphFeatures, session.EditBlockReason ?? "Repair did not re-enable editing.");
        TestAssert.True(session.HasPendingRepair, "Repair did not remain pending until save.");
        TestAssert.True(!session.CanFixMorph, "A repaired face still offered the repair action.");
        TestAssert.Near(0, session.Evaluation.OriginalOracleReport!.MaximumError, 0.0001f);
        TestAssert.Near(new Vector3(2, 0, 0), draft.BakedLods[0][1], 0.0001f);
        TestAssert.Near(new Vector3(12, 0, 1), draft.BakedLods[1][1], 0.0001f);
        TestAssert.Near(0.5f, draft.GetFeatureOffset("Shape"), 0.000001f);
        AssertNonGeometryState(draft);

        session.SetFeature("Shape", 1f);
        var editedDraft = session.CreateDraft(hair, [otherMesh], material);
        TestAssert.Near(new Vector3(12, 0, 2), editedDraft.BakedLods[1][1], 0.0001f);

        session.SetFeature("Shape", 0f);
        var zeroedDraft = session.CreateDraft(hair, [otherMesh], material);
        TestAssert.Near(new Vector3(12, 0, 0), zeroedDraft.BakedLods[1][1], 0.0001f);

        session.Undo();
        session.Undo();
        TestAssert.True(session.CanEditMorphFeatures, "Undoing the slider edit unexpectedly undid the repair.");
        TestAssert.True(session.HasPendingRepair, "Undoing the slider edit cleared the pending repair state.");
        session.Undo();
        TestAssert.True(!session.CanEditMorphFeatures, "Undo did not restore the oracle-blocked state.");
        TestAssert.True(!session.HasPendingRepair, "Undo did not clear the pending repair state.");
        AssertNonGeometryState(session.CreateDraft(hair, [otherMesh], material));
        session.Redo();
        TestAssert.True(session.CanEditMorphFeatures, "Redo did not restore editability after repair.");
        TestAssert.True(session.HasPendingRepair, "Redo did not restore the pending repair state.");
        AssertNonGeometryState(session.CreateDraft(hair, [otherMesh], material));

        void AssertNonGeometryState(MorphFaceDocument actual)
        {
            TestAssert.Equal(hair, actual.HairMeshReference);
            TestAssert.True(actual.OtherMeshReferences.SequenceEqual([otherMesh]),
                "Repair changed m_oOtherMeshes references.");
            TestAssert.True(actual.FinalSkeleton.SequenceEqual(document.FinalSkeleton),
                "Repair changed the final skeleton.");
            TestAssert.True(actual.MaterialOverrides == material,
                "Repair changed material overrides.");
            TestAssert.True(actual.PropertyNames.SequenceEqual(document.PropertyNames),
                "Repair changed the serialized property set.");
        }
    }

    private static void FixMorphRejectsUnsafeCandidates()
    {
        var mesh = TestFixtures.CreateMesh();
        MorphFaceDocument CreateDocument(
            IReadOnlyList<MorphFeatureValue> features,
            IReadOnlyList<Vector3[]>? bakedLods = null) => new(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "face"),
            mesh.Source,
            null,
            features,
            [],
            MorphFaceMaterialOverrides.Empty,
            bakedLods ?? [[Vector3.Zero, new Vector3(2, 2, 3), new Vector3(-1, -2, -3)]],
            []);

        var unresolved = new MorphFaceEditingSession(
            CreateDocument([new MorphFeatureValue("Missing", 1)]),
            mesh,
            []);
        TestAssert.True(!unresolved.CanFixMorph, "An unresolved feature was offered destructive repair.");

        var blocked = new MorphFaceEditingSession(
            CreateDocument([]),
            mesh,
            [],
            geometryEditBlockReason: "Profile blocks geometry editing.");
        TestAssert.True(!blocked.CanFixMorph, "A geometry-blocked profile was offered destructive repair.");

        var incompatible = new MorphFaceEditingSession(
            CreateDocument([], [[Vector3.Zero]]),
            mesh,
            []);
        TestAssert.True(!incompatible.CanFixMorph, "Incompatible baked topology was offered destructive repair.");

        var invalidTarget = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Shape", "MorphTarget"),
            [new MorphTargetLod(0, 4, [])],
            []);
        var invalid = new MorphFaceEditingSession(
            CreateDocument([new MorphFeatureValue("Shape", 0)]),
            mesh,
            [invalidTarget]);
        TestAssert.True(invalid.ValidationErrors.Count > 0 && !invalid.CanFixMorph,
            "A target-validation failure was offered destructive repair.");

        var corrected = new MorphFaceEditingSession(
            CreateDocument([]),
            mesh,
            [],
            recognizesBaseVariant: _ => true);
        TestAssert.True(corrected.UsesBaseVariantCorrection && !corrected.CanFixMorph,
            "A recognized position-corrected base variant was offered destructive repair.");

        var lowerPositions = mesh.Positions.Select(position => position + Vector3.One).ToArray();
        var unevaluable = new MorphFaceEditingSession(
            CreateDocument([], [
                [Vector3.Zero, new Vector3(2, 2, 3), new Vector3(-1, -2, -3)],
                lowerPositions
            ]),
            mesh,
            []);
        TestAssert.True(!unevaluable.CanFixMorph,
            "An unevaluable stored lower LOD was offered destructive repair.");
    }

    private static void FixMorphSurvivesNotificationFailure()
    {
        var mesh = TestFixtures.CreateMesh();
        var target = TestFixtures.CreateTarget(
            new MorphVertexDelta(1, Vector3.UnitX, Vector3.Zero));
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "face"),
            mesh.Source,
            null,
            [new MorphFeatureValue("Target", 1)],
            [],
            MorphFaceMaterialOverrides.Empty,
            [[Vector3.Zero, new Vector3(5, 2, 3), new Vector3(-1, -2, -3)]],
            []);
        var session = new MorphFaceEditingSession(document, mesh, [target]);
        EventHandler throwingCallback = (_, _) => throw new InvalidOperationException("preview callback failed");
        session.EvaluationChanged += throwingCallback;

        Exception? failure = null;
        try
        {
            session.FixMorph();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        TestAssert.True(failure?.Message == "preview callback failed",
            "The throwing notification callback was not observed.");
        TestAssert.True(session.CanEditMorphFeatures && session.HasPendingRepair && session.CanUndo,
            "A notification failure partially rolled back a recorded repair.");
        session.EvaluationChanged -= throwingCallback;
        session.Undo();
        TestAssert.True(!session.CanEditMorphFeatures && !session.HasPendingRepair,
            "Undo could not restore the original face after a notification failure.");
    }

    private static void SemanticTransferRebakesDestinationProfile()
    {
        var sourceMesh = TestFixtures.CreateMesh();
        var sourceCommon = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Common", "MorphTarget"),
            [new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(2, 0, 0), Vector3.Zero)])],
            [new MorphTargetBoneOffset("root", new Vector3(0, 2, 0))]);
        var sourceDocument = new MorphFaceDocument(
            TestFixtures.CreateIdentity("SourceFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "source"),
            sourceMesh.Source,
            null,
            [new MorphFeatureValue("Common", 0.5f), new MorphFeatureValue("SourceOnly", 0.75f)],
            [new BoneTranslation("root", new Vector3(0, 1, 3))],
            MorphFaceMaterialOverrides.Empty,
            [[Vector3.Zero, new Vector3(2, 2, 3), new Vector3(-1, -2, -3)]],
            []);
        var source = new MorphFaceEditingSession(
            sourceDocument,
            sourceMesh,
            [sourceCommon],
            new HashSet<string>(["SourceOnly"], StringComparer.OrdinalIgnoreCase),
            "Source");
        TestAssert.True(source.CanEditMorphFeatures, source.EditBlockReason ?? "Source session was not editable.");

        var destinationBase = new[]
        {
            new Vector3(10, 0, 0),
            new Vector3(11, 2, 3),
            new Vector3(9, -2, -3)
        };
        var destinationMesh = sourceMesh with
        {
            Source = TestFixtures.CreateIdentity("DestinationBase", "SkeletalMesh"),
            Lods = [sourceMesh.Lods[0] with
            {
                Positions = destinationBase,
                Topology = sourceMesh.Topology with
                {
                    ReferenceSkeleton = [new ReferenceBone("root", 0, new Vector3(5, 0, 0), Quaternion.Identity)]
                }
            }]
        };
        var destinationCommon = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Common", "MorphTarget"),
            [new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(0, 4, 0), Vector3.Zero)])],
            [new MorphTargetBoneOffset("root", new Vector3(0, 6, 0))]);
        var destinationOnly = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.DestinationOnly", "MorphTarget"),
            [new MorphTargetLod(0, 3, [new MorphVertexDelta(2, Vector3.One, Vector3.Zero)])],
            []);
        var destinationDocument = new MorphFaceDocument(
            TestFixtures.CreateIdentity("DestinationFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "destination"),
            destinationMesh.Source,
            null,
            [],
            [new BoneTranslation("root", new Vector3(5, 0, 0))],
            MorphFaceMaterialOverrides.Empty,
            [destinationBase.ToArray()],
            []);
        var destination = new MorphFaceEditingSession(
            destinationDocument,
            destinationMesh,
            [destinationCommon, destinationOnly],
            profileName: "Destination");
        TestAssert.True(destination.CanEditMorphFeatures, destination.EditBlockReason ?? "Destination session was not editable.");

        destination.ApplySemanticTransferData(source.CaptureSemanticTransferData());
        var draft = destination.CreateDraft(null, [], MorphFaceMaterialOverrides.Empty);

        TestAssert.Near(0.5f, draft.GetFeatureOffset("Common"), 0.000001f);
        TestAssert.True(draft.MorphFeatures.All(value => value.Name != "SourceOnly"),
            "A source-only slider leaked into the destination profile.");
        TestAssert.True(draft.MorphFeatures.All(value => value.Name != "DestinationOnly" || value.Offset == 0),
            "A destination-only slider did not retain its neutral value.");
        TestAssert.Near(new Vector3(11, 4, 3), draft.BakedLods[0][1], 0.000001f);
        TestAssert.Near(new Vector3(5, 3, 3), draft.FinalSkeleton.Single().Translation, 0.000001f);
    }

    private static void MixedMorphMaterialBoneHistoryRoundTrips()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var morph = CreateRelativeSession(mesh, document, target);
        var materials = MaterialTestFixtures.CreateSession();
        var history = new EditorHistory();
        morph.History = materials.History = history;
        var historyChanges = 0;
        history.HistoryChanged += (_, _) => historyChanges++;

        morph.BeginFeatureEdit("Target");
        morph.SetFeature("Target", 1f);
        materials.BeginScalarEdit("HED_Norm_Blend");
        materials.SetScalar("HED_Norm_Blend", 0.6f);
        morph.EndFeatureEdit("Target"); // Old release must not end the newer material gesture.
        materials.SetScalar("HED_Norm_Blend", 0.75f);
        materials.EndScalarEdit("HED_Norm_Blend");
        morph.BeginBoneTranslationEdit("root");
        morph.SetBoneAxis("root", 0, 3f);
        morph.SetBoneAxis("root", 1, 4f);
        morph.SetBoneAxis("root", 2, 5f);
        morph.EndBoneTranslationEdit("root");
        TestAssert.True(historyChanges == 3,
            "The three semantic actions did not produce one history step each.");

        history.Undo();
        TestAssert.Near(new Vector3(0, 2, 0), morph.FinalSkeleton.Single().Translation, 0.000001f);
        history.Undo();
        TestAssert.Near(0.5f, materials.GetScalar("HED_Norm_Blend"), 0.000001f);
        history.Undo();
        TestAssert.Near(0.5f, morph.GetFeature("Target"), 0.000001f);
        TestAssert.True(!history.CanUndo, "A stale completion or bone axis split a semantic action.");

        history.Redo();
        TestAssert.Near(1f, morph.GetFeature("Target"), 0.000001f);
        history.Redo();
        TestAssert.Near(0.75f, materials.GetScalar("HED_Norm_Blend"), 0.000001f);
        history.Redo();
        TestAssert.Near(new Vector3(3, 4, 5), morph.FinalSkeleton.Single().Translation, 0.000001f);

        // A draft is the semantic save payload. Reopen fresh sessions from it to
        // prove the final mixed state is persisted independently of undo stacks.
        var draft = morph.CreateDraft(null, materials.CreateOverrides());
        var reopenedMorph = new MorphFaceEditingSession(
            draft,
            mesh,
            [target],
            geometryMode: MorphFaceGeometryMode.RelativeBake);
        var reopenedMaterials = new MaterialEditingSession(draft.MaterialOverrides, materials.BaseMaterials);
        TestAssert.Near(1f, reopenedMorph.GetFeature("Target"), 0.000001f);
        TestAssert.Near(new Vector3(3, 4, 5), reopenedMorph.FinalSkeleton.Single().Translation, 0.000001f);
        TestAssert.Near(0.75f, reopenedMaterials.GetScalar("HED_Norm_Blend"), 0.000001f);
    }

    private static void CancelledCompositeRetainsRedo()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var morph = CreateRelativeSession(mesh, document, target);
        var materials = MaterialTestFixtures.CreateSession();
        var history = new EditorHistory();
        morph.History = materials.History = history;
        morph.SetFeature("Target", 1f);
        materials.SetScalar("HED_Norm_Blend", 0.75f);
        history.Undo();
        var before = morph.CreateDraft(null, materials.CreateOverrides());

        var callbackFailure = new InvalidOperationException("rollback preview failure");
        EventHandler throwingPreview = (_, _) => throw callbackFailure;
        Exception? observedFailure = null;
        try
        {
            using (history.BeginAggregate())
            {
                morph.SetFeature("Target", 2f);
                materials.SetScalar("HED_Norm_Blend", 0.4f);
                morph.SetRandomisedBones([new BoneTranslation("root", new Vector3(4, 5, 6))],
                    new Dictionary<string, Vector3> { ["root"] = new(1, 2, 3) });
                morph.EvaluationChanged += throwingPreview;
            }
        }
        catch (InvalidOperationException exception) { observedFailure = exception; }
        finally { morph.EvaluationChanged -= throwingPreview; }
        TestAssert.True(ReferenceEquals(callbackFailure, observedFailure) && history.CanRedo && morph.CursedBoneOffsets.Count == 0 &&
                        MorphFaceEditorStateComparer.Equals(before, morph.CreateDraft(null, materials.CreateOverrides())),
            "Cancelling the composite lost the preceding redo or retained partial authored state.");
        using (var empty = history.BeginAggregate()) empty.Commit();
        history.Redo();
        TestAssert.Near(0.75f, materials.GetScalar("HED_Norm_Blend"), 0.000001f);
        TestAssert.Near(1f, morph.GetFeature("Target"), 0.000001f);
    }

    private static void DirtyComparisonTracksAuthoredState()
    {
        var source = new AssetIdentity("fixture.pcc", "Package.Face", 1, "BioMorphFace");
        var hair = new AssetIdentity("fixture.pcc", "Package.Hair", 2, "SkeletalMesh");
        var material = new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("Scalar", 0.5f)],
            [new VectorMaterialOverride("Colour", Vector4.One)],
            []);
        var baseline = new MorphFaceDocument(
            source,
            new PackageFingerprint(1, DateTime.UnixEpoch, "hash"),
            null,
            hair,
            [new MorphFeatureValue("Feature", 0.25f)],
            [new BoneTranslation("root", Vector3.Zero)],
            material,
            [new[] { Vector3.Zero }],
            []);

        TestAssert.True(MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with { BakedLods = [new[] { Vector3.One }] }),
            "Derived baked geometry incorrectly marked the document dirty.");
        TestAssert.True(!MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with { MorphFeatures = [new MorphFeatureValue("Feature", 0.75f)] }),
            "A feature edit was not detected.");
        TestAssert.True(!MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with { OtherMeshReferences = [new AssetIdentity("fixture.pcc", "Package.Hat", 3, "SkeletalMesh")] }),
            "An m_oOtherMeshes edit was not detected.");
        TestAssert.True(!MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with
            {
                MaterialOverrides = material with
                {
                    Scalars = [new ScalarMaterialOverride("Scalar", 0.75f)]
                }
            }),
            "A material edit was not detected.");
        var unresolved = baseline with
        {
            MaterialOverrides = material with
            {
                Textures = [new TextureMaterialOverride("HED_Diff",
                    new AssetIdentity("", "BIOG_Example.Diff.Test", 0, "Texture2D"))]
            }
        };
        TestAssert.True(MorphFaceEditorStateComparer.Equals(unresolved, unresolved with { BakedLods = [new[] { Vector3.One }] }),
            "An unresolved authored texture path broke dirty-state comparison.");
        TestAssert.True(!MorphFaceEditorStateComparer.Equals(baseline, unresolved),
            "An unresolved authored texture edit was not detected.");
    }

}
