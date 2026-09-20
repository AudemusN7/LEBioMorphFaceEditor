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
        new("unified history preserves interleaved editor chronology", UnifiedHistoryIsChronological),
        new("dirty comparison tracks authored state and ignores derived LODs", DirtyComparisonTracksAuthoredState),
        new("oracle mismatch threshold tolerates visually insignificant drift", OracleMismatchThresholdToleratesSmallDrift),
        new("Fix Morph rejects unsafe repair candidates", FixMorphRejectsUnsafeCandidates),
        new("fix morph rebuilds all compatible LODs and re-enables editing", FixMorphRebuildsCompatibleLods),
        new("Fix Morph remains atomic when a notification callback fails", FixMorphSurvivesNotificationFailure),
        new("semantic transfer rebakes destination geometry and preserves bone residuals", SemanticTransferRebakesDestinationProfile),
        new("fixed-bake mode preserves imported geometry and authored morphs while editing bones", FixedBakePreservesDraftAndEditsBones),
        new("fixed-bake bone translation uses semantic undo and redo", FixedBakeBoneTranslationUndoRedo),
        new("relative-bake mode preserves the authored player bake initially", RelativeBakePreservesInitialAuthoredBake),
        new("relative-bake applies one canonical slider delta", RelativeBakeAppliesSliderDelta),
        new("relative-bake recomputes from a stable baseline", RelativeBakeDoesNotAccumulate),
        new("relative-bake preserves an unproven lower LOD", RelativeBakePreservesUnprovenLowerLod),
        new("relative-bake draft persists features bones and derived bake", RelativeBakeDraftPersistsState),
        new("relative-bake slider undo and redo restore derived geometry", RelativeBakeUndoRedo),
        new("relative-bake disables on incompatible authored LOD0 topology", RelativeBakeRejectsIncompatibleTopology),
        new("base-mesh-only and blocked profiles do not expose bone editing", BaseMeshOnlyDoesNotExposeBones)
    ];

    private static void RelativeBakePreservesInitialAuthoredBake()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);

        TestAssert.True(session.GeometryMode == MorphFaceGeometryMode.RelativeBake,
            "The relative-bake session did not retain its explicit geometry mode.");
        TestAssert.True(session.CanEditMorphFeatures, session.EditBlockReason ?? "Relative-bake editing was disabled.");
        TestAssert.True(session.Evaluation.Geometry.Positions.SequenceEqual(document.BakedLods[0]),
            "Initial relative-bake geometry was reconstructed instead of preserving authored vertices.");
        TestAssert.True(session.Evaluation.LodGeometry[1].Positions.SequenceEqual(document.BakedLods[1]),
            "Initial lower-LOD geometry was not preserved exactly.");
    }

    private static void RelativeBakeAppliesSliderDelta()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        session.SetFeature("Target", 1f);

        TestAssert.Near(new Vector3(11, 2, 3), session.Evaluation.Geometry.Positions[1], 0.000001f);
        TestAssert.Near(new Vector3(102, 0, 0), session.Evaluation.LodGeometry[1].Positions[1], 0.000001f);
    }

    private static void RelativeBakeDoesNotAccumulate()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        session.SetFeature("Target", 1f);
        session.SetFeature("Target", 2f);
        session.SetFeature("Target", 1f);

        TestAssert.Near(new Vector3(11, 2, 3), session.Evaluation.Geometry.Positions[1], 0.000001f);
        var draft = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        TestAssert.Near(new Vector3(11, 2, 3), draft.BakedLods[0][1], 0.000001f);
    }

    private static void RelativeBakeDraftPersistsState()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        session.SetFeature("Target", 1f);
        session.SetBoneAxis("root", 0, 3f);

        var draft = session.CreateDraft(null, MorphFaceMaterialOverrides.Empty);
        TestAssert.Near(1f, draft.GetFeatureOffset("Target"), 0.000001f);
        TestAssert.Near(new Vector3(3, 2, 0), draft.FinalSkeleton.Single().Translation, 0.000001f);
        TestAssert.Near(new Vector3(11, 2, 3), draft.BakedLods[0][1], 0.000001f);
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

    private static void RelativeBakeUndoRedo()
    {
        var (mesh, document, target) = CreateRelativeBakeFixture();
        var session = CreateRelativeSession(mesh, document, target);
        session.SetFeature("Target", 1f);
        session.Undo();
        TestAssert.Near(document.BakedLods[0][1], session.Evaluation.Geometry.Positions[1], 0.000001f);
        session.Redo();
        TestAssert.Near(new Vector3(11, 2, 3), session.Evaluation.Geometry.Positions[1], 0.000001f);
    }

    private static void RelativeBakeRejectsIncompatibleTopology()
    {
        var (mesh, document, _) = CreateRelativeBakeFixture();
        var incompatibleTarget = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Target", "MorphTarget"),
            [new MorphTargetLod(0, 4, [new MorphVertexDelta(1, Vector3.UnitX, Vector3.Zero)])],
            []);
        var session = CreateRelativeSession(mesh, document, incompatibleTarget);
        TestAssert.True(!session.CanEditMorphFeatures && !session.CanEdit,
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

        TestAssert.True(!session.CanEditMorphFeatures && !session.CanEdit,
            "Fixed-bake mode exposed morph editing through the compatibility alias.");
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

    private static void FixedBakeBoneTranslationUndoRedo()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var document = CreateFixedBakeDocument(mesh, mesh.Positions, featureOffset: 0.25f, bone: Vector3.Zero);
        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            geometryMode: MorphFaceGeometryMode.FixedBake);

        session.BeginBoneTranslationEdit("root");
        session.SetBoneAxis("root", 0, 2);
        session.SetBoneAxis("root", 1, 3);
        session.EndBoneTranslationEdit("root");
        TestAssert.True(session.CanUndo, "A complete bone gesture did not create history.");
        TestAssert.Near(new Vector3(2, 3, 0), session.FinalSkeleton.Single().Translation, 0.000001f);
        session.Undo();
        TestAssert.Near(Vector3.Zero, session.FinalSkeleton.Single().Translation, 0.000001f);
        session.Redo();
        TestAssert.Near(new Vector3(2, 3, 0), session.FinalSkeleton.Single().Translation, 0.000001f);
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

    private static void OracleMismatchThresholdToleratesSmallDrift()
    {
        var mesh = TestFixtures.CreateMesh();
        MorphFaceEditingSession CreateSession(float drift) => new(
            new MorphFaceDocument(
                TestFixtures.CreateIdentity("Face", "BioMorphFace"),
                new PackageFingerprint(1, DateTime.UnixEpoch, "face"),
                mesh.Source,
                null,
                [],
                [],
                MorphFaceMaterialOverrides.Empty,
                [mesh.Positions.Select((position, index) => index == 1
                    ? position + new Vector3(drift, 0, 0)
                    : position).ToArray()],
                []),
            mesh,
            []);

        var atThreshold = CreateSession(MorphFaceEditingSession.OracleMismatchThreshold);
        TestAssert.True(atThreshold.CanEdit,
            $"A threshold baked drift incorrectly blocked editing: {atThreshold.EditBlockReason}");
        TestAssert.True(!atThreshold.CanFixMorph,
            "A threshold baked drift incorrectly offered morph repair.");

        var aboveThreshold = CreateSession(MorphFaceEditingSession.OracleMismatchThreshold + 0.0001f);
        TestAssert.True(!aboveThreshold.CanEdit,
            "A just-over-threshold baked drift incorrectly allowed editing.");
        TestAssert.True(aboveThreshold.CanFixMorph,
            aboveThreshold.EditBlockReason ?? "A just-over-threshold baked drift did not offer morph repair.");
    }

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

        TestAssert.True(!session.CanEdit, "The intentionally malformed face was editable before repair.");
        TestAssert.True(session.CanFixMorph, session.EditBlockReason ?? "The malformed face was not offered repair.");

        session.FixMorph();
        var draft = session.CreateDraft(hair, [otherMesh], material);

        TestAssert.True(session.CanEdit, session.EditBlockReason ?? "Repair did not re-enable editing.");
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
        TestAssert.True(session.CanEdit, "Undoing the slider edit unexpectedly undid the repair.");
        TestAssert.True(session.HasPendingRepair, "Undoing the slider edit cleared the pending repair state.");
        session.Undo();
        TestAssert.True(!session.CanEdit, "Undo did not restore the oracle-blocked state.");
        TestAssert.True(!session.HasPendingRepair, "Undo did not clear the pending repair state.");
        AssertNonGeometryState(session.CreateDraft(hair, [otherMesh], material));
        session.Redo();
        TestAssert.True(session.CanEdit, "Redo did not restore editability after repair.");
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
        var nonRenderableLowerLod = mesh with { LodPositions = [mesh.Positions, lowerPositions] };
        var unevaluable = new MorphFaceEditingSession(
            CreateDocument([], [
                [Vector3.Zero, new Vector3(2, 2, 3), new Vector3(-1, -2, -3)],
                lowerPositions
            ]),
            nonRenderableLowerLod,
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
        TestAssert.True(session.CanEdit && session.HasPendingRepair && session.CanUndo,
            "A notification failure partially rolled back a recorded repair.");
        session.EvaluationChanged -= throwingCallback;
        session.Undo();
        TestAssert.True(!session.CanEdit && !session.HasPendingRepair,
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
        TestAssert.True(source.CanEdit, source.EditBlockReason ?? "Source session was not editable.");

        var destinationBase = new[]
        {
            new Vector3(10, 0, 0),
            new Vector3(11, 2, 3),
            new Vector3(9, -2, -3)
        };
        var destinationMesh = sourceMesh with
        {
            Source = TestFixtures.CreateIdentity("DestinationBase", "SkeletalMesh"),
            Positions = destinationBase,
            Topology = sourceMesh.Topology with
            {
                ReferenceSkeleton = [new ReferenceBone("root", 0, new Vector3(5, 0, 0), Quaternion.Identity)]
            }
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
        TestAssert.True(destination.CanEdit, destination.EditBlockReason ?? "Destination session was not editable.");

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

    private static void UnifiedHistoryIsChronological()
    {
        var geometry = new FakeSource("geometry");
        var material = new FakeSource("material");
        var hair = new FakeSource("hair");
        using var history = new EditorUndoCoordinator(geometry, material, hair);
        geometry.Commit("feature");
        material.Commit("colour");
        hair.Commit("mesh");

        history.Undo();
        history.Undo();
        history.Undo();
        TestAssert.Equal("undo:mesh,undo:colour,undo:feature", string.Join(',',
            hair.Log.Concat(material.Log).Concat(geometry.Log)));

        history.Redo();
        history.Redo();
        history.Redo();
        TestAssert.True(geometry.Log.Contains("redo:feature"), "Geometry redo was not replayed.");
        TestAssert.True(material.Log.Contains("redo:colour"), "Material redo was not replayed.");
        TestAssert.True(hair.Log.Contains("redo:mesh"), "Hair redo was not replayed.");
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

    private sealed class FakeSource(string name) : IUndoableEditSource
    {
        private readonly Stack<string> _undo = new();
        private readonly Stack<string> _redo = new();
        public event EventHandler? EditCommitted;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public List<string> Log { get; } = [];

        public void Commit(string value)
        {
            _undo.Push(value);
            _redo.Clear();
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }

        public void Undo()
        {
            var value = _undo.Pop();
            _redo.Push(value);
            Log.Add($"undo:{value}");
        }

        public void Redo()
        {
            var value = _redo.Pop();
            _undo.Push(value);
            Log.Add($"redo:{value}");
        }

        public void ClearRedo() => _redo.Clear();
        public override string ToString() => name;
    }
}
