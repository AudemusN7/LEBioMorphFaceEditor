using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using DxMathUtil = SharpDX.MathUtil;

namespace MorphFaceEditor.Tests;

// Fast renderer contract tests: camera math, material classification and D3D resource lifecycle.
public static class RenderingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("orbit camera fits and clamps", OrbitCameraFitsAndClamps),
        new("orbit camera retains framing when bounds change", OrbitCameraRetainsFramingWhenBoundsChange),
        new("orbit camera resets framing without losing rotation", OrbitCameraResetsFramingWithoutLosingRotation),
        new("preview camera groups humanoids and isolates divergent species", PreviewCameraGroupsSpecies),
        new("orbit camera pans in its view plane", OrbitCameraPansInViewPlane),
        new("camera-parented directions follow orbit", CameraParentedDirectionsFollowOrbit),
        new("LE tangent basis uses TangentZ handedness", TangentBasisUsesTangentZHandedness),
        new("head materials classify by family", MaterialsClassifyByFamily),
        new("BioMorphFace search matches names and paths", BioMorphFaceSearchMatchesNamesAndPaths),
        new("three-point lighting presets are valid", ThreePointLightingPresetsAreValid),
        new("lighting presets change shaded output", LightingPresetsChangeShadedOutput),
        new("unlit mode bypasses lighting and ambient occlusion", UnlitModeBypassesLightingAndAmbientOcclusion),
        new("view-space AO does not reveal coplanar triangle topology", AmbientOcclusionRejectsCoplanarTopology),
        new("view-space AO produces visible contact shadowing", AmbientOcclusionProducesContactShadowing),
        new("lit-translucent hair is excluded from AO", AmbientOcclusionExcludesHair),
        new("renderer clears to the selected background colour", RendererUsesSelectedBackgroundColor),
        new("renderer draws, reads back, and retains its device on resize", RendererDrawsAndRetainsDevice),
        new("renderer can hide attachment meshes", RendererCanHideAttachments),
        new("scene factory builds the selected mesh LOD", SceneFactoryBuildsSelectedLod),
        new("scene factory maps non-contiguous LOD IDs to baked ordinals", SceneFactoryMapsNonContiguousLodIds),
        new("scene factory falls back to base geometry for an unmatched baked LOD", SceneFactoryHandlesUnmatchedBakedLod),
        new("lower LOD sections use their authored material remap", LowerLodSectionsUseMaterialRemap),
        new("custom mesh sections use the mesh material-slot order", CustomMeshUsesAuthoredMaterialOrder),
        new("custom mesh preview ignores BioMorphFace geometry and skeleton", CustomMeshIgnoresMorphFaceGeometry),
        new("preview lifecycle blocks inactive window states", PreviewLifecycleBlocksInactiveStates),
        new("renderer rejects work after deterministic disposal", RendererRejectsWorkAfterDisposal)
    ];

    private static void OrbitCameraFitsAndClamps()
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(new HeadPreviewBounds(new Vector3(-1, -2, -1), new Vector3(1, 2, 1)));
        TestAssert.True(camera.Distance > camera.MinimumDistance, "Fit did not place the camera outside the mesh.");
        camera.Orbit(40, 40);
        TestAssert.True(Math.Abs(camera.Pitch) <= DxMathUtil.DegreesToRadians(82.01f), "Pitch was not clamped.");
        camera.Zoom(float.MaxValue);
        TestAssert.Near(camera.MinimumDistance, camera.Distance, 1e-6f);
        TestAssert.True(float.IsFinite(camera.Position.X), "Camera position became non-finite.");
    }

    private static void SceneFactoryBuildsSelectedLod()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [],
            MorphFaceMaterialOverrides.Empty,
            mesh.AvailableLodPositions,
            []);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document));

        var scene = new HeadPreviewSceneFactory().Create(loaded, 1);

        TestAssert.Equal(new Vector3(0, 0, 10), scene.Meshes[0].Vertices[0].Position);
        TestAssert.Equal(3, scene.Meshes[0].Indices.Count);
    }

    private static void SceneFactoryMapsNonContiguousLodIds()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh(lowerLodIndex: 2);
        var bakedLod2 = mesh.AvailableLodPositions[1]
            .Select(position => position + new Vector3(5, 0, 0))
            .ToArray();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [],
            MorphFaceMaterialOverrides.Empty,
            [mesh.AvailableLodPositions[0], bakedLod2],
            []);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document));

        var scene = new HeadPreviewSceneFactory().Create(loaded, 2);

        TestAssert.Equal(new Vector3(0, 0, 15), scene.Meshes[0].Vertices[0].Position);
    }

    private static void LowerLodSectionsUseMaterialRemap()
    {
        var map = new[] { 2, 0, 1 };
        TestAssert.Equal(2, LodMaterialMap.Resolve(0, map, 3, "fixture LOD1"));
        TestAssert.Equal(0, LodMaterialMap.Resolve(1, map, 3, "fixture LOD1"));
        TestAssert.Equal(1, LodMaterialMap.Resolve(2, map, 3, "fixture LOD1"));
    }

    private static void CustomMeshUsesAuthoredMaterialOrder()
    {
        var skinIdentity = TestFixtures.CreateIdentity("Custom.SkinMaterial", "MaterialInstanceConstant");
        var eyeIdentity = TestFixtures.CreateIdentity("Custom.EyeMaterial", "MaterialInstanceConstant") with { UIndex = 2 };
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.One };
        var skeleton = new[] { new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity) };
        var topology = new SkeletalMeshTopology(
            0,
            positions.Length,
            6,
            2,
            [new MeshSectionTopology(1, 0, 0, 1), new MeshSectionTopology(0, 0, 3, 1)],
            [new MeshChunkTopology(0, 0, positions.Length, 1, [0])],
            skeleton,
            [0],
            [0],
            new string('M', 64));
        var renderData = new SkeletalMeshRenderData(
            Enumerable.Repeat(new Vector4(1, 0, 0, 1), positions.Length).ToArray(),
            Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            Enumerable.Repeat(new BoneIndex4(0, 0, 0, 0), positions.Length).ToArray(),
            Enumerable.Repeat(new Vector4(1, 0, 0, 0), positions.Length).ToArray(),
            [0, 1, 2, 1, 3, 2],
            [skinIdentity, eyeIdentity]);
        var mesh = new SkeletalMeshAsset(
            TestFixtures.CreateIdentity("Custom.AhernHead", "SkeletalMesh"),
            positions,
            Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            topology,
            renderData,
            [positions],
            [new SkeletalMeshLod(0, positions, Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(), topology, renderData)]);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Custom.AhernFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [],
            MorphFaceMaterialOverrides.Empty,
            [],
            []);
        var skin = CreateMaterial(skinIdentity, "Skin Master", HeadMaterialFamily.Skin);
        var eyes = CreateMaterial(eyeIdentity, "Eye Master", HeadMaterialFamily.Eyes);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [skin.Key] = skin,
                [eyes.Key] = eyes
            }),
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document));

        var sections = new HeadPreviewSceneFactory().Create(loaded).Meshes[0].Sections;

        TestAssert.Equal(eyes.Key, sections[0].Material.Key);
        TestAssert.Equal(skin.Key, sections[1].Material.Key);

        static ResolvedHeadMaterial CreateMaterial(
            AssetIdentity identity,
            string masterName,
            HeadMaterialFamily family) => new(
                MaterialIdentityKey.Create(identity),
                identity,
                masterName,
                family,
                HeadMaterialBlendMode.Opaque,
                false,
                new Dictionary<string, float>(),
                new Dictionary<string, Vector4>(),
                new Dictionary<string, MaterialTextureBinding>());
    }

    private static void CustomMeshIgnoresMorphFaceGeometry()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var exploded = mesh.Positions
            .Select(position => position + new Vector3(1000, 2000, 3000))
            .ToArray();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Custom.ExplodedFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [new BoneTranslation("root", new Vector3(500, 500, 500))],
            MorphFaceMaterialOverrides.Empty,
            [exploded],
            []);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document))
        {
            UsesCustomBaseMesh = true
        };

        var previewMesh = new HeadPreviewSceneFactory().Create(loaded).Meshes[0];

        TestAssert.Equal(new Vector3(0, 0, 1), previewMesh.Vertices[1].Position);
        TestAssert.True(!previewMesh.ApplySkinning,
            "The custom base mesh was still being transformed by the BioMorphFace skeleton.");
    }

    private static void SceneFactoryHandlesUnmatchedBakedLod()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions, [Vector3.Zero]],
            []);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document));

        var scene = new HeadPreviewSceneFactory().Create(loaded, 1);

        TestAssert.Equal(new Vector3(0, 0, 10), scene.Meshes[0].Vertices[0].Position);
    }

    private static void OrbitCameraRetainsFramingWhenBoundsChange()
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(new HeadPreviewBounds(new Vector3(-1), new Vector3(1)));
        camera.ResetThreeQuarter();
        camera.Zoom(240);
        camera.Pan(0.2f, -0.1f);
        var target = camera.Target;
        var yaw = camera.Yaw;
        var pitch = camera.Pitch;
        var distance = camera.Distance;

        camera.UpdateBounds(new HeadPreviewBounds(new Vector3(-1.1f), new Vector3(1.1f)));

        TestAssert.Equal(target, camera.Target);
        TestAssert.Equal(yaw, camera.Yaw);
        TestAssert.Equal(pitch, camera.Pitch);
        TestAssert.Equal(distance, camera.Distance);
    }

    private static void OrbitCameraResetsFramingWithoutLosingRotation()
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(new HeadPreviewBounds(new Vector3(-1), new Vector3(1)));
        camera.Orbit(0.7f, -0.2f);
        camera.Zoom(600);
        camera.Pan(0.4f, -0.25f);
        var yaw = camera.Yaw;
        var pitch = camera.Pitch;

        var replacementBounds = new HeadPreviewBounds(new Vector3(-5, -3, -2), new Vector3(7, 9, 4));
        camera.UpdateBounds(replacementBounds);
        camera.ResetPosition();

        TestAssert.Equal(yaw, camera.Yaw);
        TestAssert.Equal(pitch, camera.Pitch);
        TestAssert.Equal(replacementBounds.Center, camera.Target);
        TestAssert.True(camera.Distance > camera.MinimumDistance, "Reset framing did not fit the replacement bounds.");
    }

    private static void PreviewCameraGroupsSpecies()
    {
        var humanoid = PreviewCameraGrouping.ForProfile("le3-human-male");
        TestAssert.Equal(humanoid, PreviewCameraGrouping.ForProfile("le3-human-female"));
        TestAssert.Equal(humanoid, PreviewCameraGrouping.ForProfile("le3-asari"));
        TestAssert.Equal(humanoid, PreviewCameraGrouping.ForProfile("le3-batarian"));
        TestAssert.Equal(
            PreviewCameraGrouping.SpeciesForProfile("le1-turian"),
            PreviewCameraGrouping.SpeciesForProfile("le3-turian"));
        TestAssert.True(humanoid != PreviewCameraGrouping.ForProfile("le3-krogan"),
            "Krogan shared humanoid camera framing.");
        TestAssert.True(PreviewCameraGrouping.ForProfile("le3-krogan") !=
                        PreviewCameraGrouping.ForProfile("le3-salarian"),
            "Krogan and Salarian shared divergent-species camera framing.");
        TestAssert.True(PreviewCameraGrouping.ForProfile("le3-salarian") !=
                        PreviewCameraGrouping.ForProfile("le3-turian"),
            "Salarian and Turian shared divergent-species camera framing.");
    }

    private static void OrbitCameraPansInViewPlane()
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(new HeadPreviewBounds(new Vector3(-1), new Vector3(1)));
        var positionBefore = camera.Position;
        var targetBefore = camera.Target;

        camera.Pan(0.25f, -0.1f);

        var targetDelta = camera.Target - targetBefore;
        var positionDelta = camera.Position - positionBefore;
        TestAssert.Near(0.25f, targetDelta.X, 1e-6f);
        TestAssert.Near(-0.1f, targetDelta.Y, 1e-6f);
        TestAssert.Near(0, targetDelta.Z, 1e-6f);
        TestAssert.True(Vector3.Distance(targetDelta, positionDelta) < 1e-6f, "Pan changed the orbit offset.");
    }

    private static void CameraParentedDirectionsFollowOrbit()
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(new HeadPreviewBounds(new Vector3(-1), new Vector3(1)));
        var cameraDirection = Vector3.Normalize(new Vector3(-0.4f, 0.3f, 0.8f));
        var frontDirection = camera.TransformCameraDirectionToWorld(cameraDirection);
        camera.Orbit(MathF.PI * 0.5f, 0);
        var sideDirection = camera.TransformCameraDirectionToWorld(cameraDirection);

        TestAssert.True(
            Vector3.Distance(frontDirection, sideDirection) > 0.5f,
            "Orbiting the camera did not rotate its parented light direction.");
        TestAssert.Near(1, sideDirection.Length(), 1e-6f);
    }

    private static void TangentBasisUsesTangentZHandedness()
    {
        var tangentX = new Vector4(1, 0, 0, 1);
        var mirrored = SkeletalMeshTangentBasis.ToRenderTangent(tangentX, new Vector4(0, 0, 1, -1));
        var regular = SkeletalMeshTangentBasis.ToRenderTangent(tangentX with { W = -1 }, new Vector4(0, 0, 1, 1));

        TestAssert.Equal(-1f, mirrored.W);
        TestAssert.Equal(1f, regular.W);

        var orthogonal = SkeletalMeshTangentBasis.Orthogonalize(
            new Vector4(1, 0, 1, -1),
            Vector3.UnitZ);
        TestAssert.Near(0, Vector3.Dot(new Vector3(orthogonal.X, orthogonal.Y, orthogonal.Z), Vector3.UnitZ), 1e-6f);
        TestAssert.Near(1, new Vector3(orthogonal.X, orthogonal.Y, orthogonal.Z).Length(), 1e-6f);
        TestAssert.Equal(-1f, orthogonal.W);
    }

    private static void MaterialsClassifyByFamily()
    {
        TestAssert.Equal(HeadMaterialFamily.Skin, HeadMaterialClassifier.Classify("HMM_HED_PROBase_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Eyes, HeadMaterialClassifier.Classify("HMM_EYE_IRIS_MAT"));
        TestAssert.Equal(HeadMaterialFamily.SalarianSkin, HeadMaterialClassifier.Classify("SAL_HED_PRO_MAT"));
        TestAssert.Equal(HeadMaterialFamily.SalarianEyes, HeadMaterialClassifier.Classify("SAL_HED_EYE_MAT"));
        TestAssert.Equal(HeadMaterialFamily.BatarianSkin, HeadMaterialClassifier.Classify("BAT_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Hair, HeadMaterialClassifier.Classify("HMM_HIR_SHORT_MAT", attachment: true));
        TestAssert.Equal(HeadMaterialFamily.MaskedHair, HeadMaterialClassifier.Classify("HMM_HIR_PROShort01_MAT_1b", attachment: true));
        TestAssert.Equal(HeadMaterialFamily.Hair, HeadMaterialClassifier.Classify("HMM_HIR_PROShort02_MAT", attachment: true));
        TestAssert.Equal(HeadMaterialFamily.Accessory, HeadMaterialClassifier.Classify("Visor_MAT", attachment: true));
        TestAssert.Equal(HeadMaterialFamily.Skin,
            HeadMaterialClassifier.ClassifyChain(["TUR_HED_PRO_MAT", "Generic_Head_Master"]));
        TestAssert.Equal(HeadMaterialFamily.Eyes,
            HeadMaterialClassifier.ClassifyChain(["HMM_HED_PRO_MAT", "HMM_EYE_MASTER_MAT"]));
        TestAssert.Equal("HMM_EYE_MASTER_MAT",
            HeadMaterialClassifier.EffectiveRootIdentity(["ASA_EYE_MIC", "HMF_EYE_PARENT", "HMM_EYE_MASTER_MAT"]));
    }

    private static void BioMorphFaceSearchMatchesNamesAndPaths()
    {
        var face = new BioMorphFaceListItem(
            42,
            "BIOA_STA_FAC.HMM.Plot.sta60_friendly_security",
            "sta60_friendly_security",
            "[HMM]");

        TestAssert.True(BioMorphFaceSearch.Matches(face, "FRIENDLY"), "Object-name search was case-sensitive.");
        TestAssert.True(BioMorphFaceSearch.Matches(face, "hmm.plot"), "Instanced-path search did not match.");
        TestAssert.True(BioMorphFaceSearch.Matches(face, "  "), "Blank search did not include the face.");
        TestAssert.True(!BioMorphFaceSearch.Matches(face, "conrad"), "Unrelated search included the face.");
        TestAssert.True(BioMorphFaceSearch.Matches(face, "HMM"), "Bare profile tag did not match.");
        TestAssert.True(BioMorphFaceSearch.Matches(face, "[HM]"), "Bracketed profile-tag prefix did not match.");
        TestAssert.True(!BioMorphFaceSearch.Matches(face, "BAT"), "A different bare profile tag matched name text.");

        var logical = ExplorerNaturalStringComparer.Instance;
        TestAssert.True(logical.Compare("kro001_guard2", "kro001_guard10") < 0,
            "Numeric suffixes were not sorted naturally.");
        TestAssert.True(logical.Compare("cit_fetch", "Cit002") < 0,
            "Punctuation/number ordering did not match Windows Explorer.");
    }

    private static void ThreePointLightingPresetsAreValid()
    {
        foreach (var preset in Enum.GetValues<HeadPreviewLightingPreset>())
        {
            var rig = HeadPreviewLightingPresets.Get(preset);
            var lights = new[] { rig.Key, rig.Fill, rig.Rim };
            TestAssert.Equal(3, lights.Length);
            foreach (var light in lights)
            {
                TestAssert.Near(1, light.Direction.Length(), 1e-5f);
                TestAssert.True(light.Intensity >= 0, $"{preset} contains a negative light intensity.");
                TestAssert.True(
                    light.Color.X >= 0 && light.Color.Y >= 0 && light.Color.Z >= 0,
                    $"{preset} contains a negative light colour.");
            }
            TestAssert.True(rig.AmbientIntensity >= 0, $"{preset} contains a negative ambient intensity.");
            TestAssert.True(rig.Exposure > 0, $"{preset} contains a non-positive exposure.");
        }
    }

    private static void LightingPresetsChangeShadedOutput()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var scene = CreateTriangleScene();
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var studio = renderer.Render(
            camera,
            new HeadPreviewOptions(LightingPreset: HeadPreviewLightingPreset.Studio)).BgraPixels.ToArray();
        var warm = renderer.Render(
            camera,
            new HeadPreviewOptions(LightingPreset: HeadPreviewLightingPreset.WarmInterior)).BgraPixels;

        TestAssert.True(!studio.SequenceEqual(warm), "Changing the lighting preset did not change shaded output.");

        var night = renderer.Render(
            camera,
            new HeadPreviewOptions(LightingPreset: HeadPreviewLightingPreset.CoolNight)).BgraPixels;
        TestAssert.True(
            AverageRgb(studio) > AverageRgb(night) * 1.35,
            "Cool Night was not materially darker than Studio.");
    }

    private static void UnlitModeBypassesLightingAndAmbientOcclusion()
    {
        using var renderer = new HeadPreviewRenderer(160, 160);
        var material = new HeadPreviewMaterial("Unlit_Contact", HeadMaterialFamily.Skin);
        var backing = CreateQuadMesh("Backing", -1, -1, 1, 1, 0, material);
        var raised = CreateQuadMesh("Raised", -0.28f, -0.28f, 0.28f, 0.28f, 0.12f, material);
        var scene = new HeadPreviewScene(
            "Unlit contact",
            [backing, raised],
            new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.17f)));
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var studio = renderer.Render(camera, new HeadPreviewOptions(
            HeadPreviewRenderMode.Unlit,
            LightingPreset: HeadPreviewLightingPreset.Studio,
            AmbientOcclusionEnabled: true)).BgraPixels.ToArray();
        var withoutAo = renderer.Render(camera, new HeadPreviewOptions(
            HeadPreviewRenderMode.Unlit,
            LightingPreset: HeadPreviewLightingPreset.Studio,
            AmbientOcclusionEnabled: false)).BgraPixels;

        TestAssert.True(studio.SequenceEqual(withoutAo),
            "Unlit mode changed when ambient occlusion was enabled.");
    }

    private static double AverageRgb(byte[] pixels) => pixels
        .Where((_, index) => index % 4 != 3)
        .Average(value => (double)value);

    private static void AmbientOcclusionRejectsCoplanarTopology()
    {
        var material = new HeadPreviewMaterial("AO_Plane", HeadMaterialFamily.Skin);
        var mesh = CreateQuadMesh("Plane", -0.9f, -0.9f, 0.9f, 0.9f, 0, material);
        var scene = new HeadPreviewScene(
            "AO plane",
            [mesh],
            new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.05f)));
        using var renderer = new HeadPreviewRenderer(128, 128);
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var withoutAo = renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: false)).BgraPixels.ToArray();
        var withAo = renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: true)).BgraPixels;

        TestAssert.True(withoutAo.SequenceEqual(withAo),
            "Coplanar triangles self-occluded and exposed their shared topology.");
    }

    private static void AmbientOcclusionProducesContactShadowing()
    {
        var material = new HeadPreviewMaterial("AO_Contact", HeadMaterialFamily.Skin);
        var backing = CreateQuadMesh("Backing", -1, -1, 1, 1, 0, material);
        var raised = CreateQuadMesh("Raised", -0.28f, -0.28f, 0.28f, 0.28f, 0.12f, material);
        var scene = new HeadPreviewScene(
            "AO contact",
            [backing, raised],
            new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.17f)));
        using var renderer = new HeadPreviewRenderer(160, 160);
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var withoutAo = renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: false)).BgraPixels.ToArray();
        var withAo = renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: true)).BgraPixels;
        var changedColorBytes = withAo.Where((value, index) => index % 4 != 3 && value < withoutAo[index]).Count();

        TestAssert.True(changedColorBytes > 100,
            "Raised geometry produced no visible ambient contact shadow on its backing surface.");
    }

    private static void AmbientOcclusionExcludesHair()
    {
        var material = new HeadPreviewMaterial("AO_Hair", HeadMaterialFamily.Hair);
        var hair = CreateQuadMesh("Hair", -0.8f, -0.8f, 0.8f, 0.8f, 0, material);
        var scene = new HeadPreviewScene(
            "AO hair exclusion",
            [hair],
            new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.05f)));
        using var renderer = new HeadPreviewRenderer(128, 128);
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var withoutAo = renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: false)).BgraPixels.ToArray();
        var withAo = renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: true)).BgraPixels;

        TestAssert.True(withoutAo.SequenceEqual(withAo),
            "Lit-translucent hair entered the opaque AO depth/normal snapshot.");
    }

    private static void RendererUsesSelectedBackgroundColor()
    {
        using var renderer = new HeadPreviewRenderer(48, 48);
        var scene = CreateTriangleScene();
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var frame = renderer.Render(camera, new HeadPreviewOptions(
            BackgroundColor: new Vector3(0.2f, 0.4f, 0.6f)));

        TestAssert.Equal((byte)153, frame.BgraPixels[0]);
        TestAssert.Equal((byte)102, frame.BgraPixels[1]);
        TestAssert.Equal((byte)51, frame.BgraPixels[2]);
        TestAssert.Equal(byte.MaxValue, frame.BgraPixels[3]);
    }

    private static void RendererDrawsAndRetainsDevice()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        renderer.SetScene(CreateTriangleScene());
        var camera = new HeadOrbitCamera();
        camera.Fit(new HeadPreviewBounds(new Vector3(-1, -1, -0.1f), new Vector3(1, 1, 0.1f)));
        var destination = new byte[96 * 96 * 4];
        var frame = renderer.Render(camera, new HeadPreviewOptions(HeadPreviewRenderMode.Diagnostic), destination);
        TestAssert.True(ReferenceEquals(destination, frame.BgraPixels), "Renderer did not reuse the caller-owned readback buffer.");
        TestAssert.Equal(1, frame.DrawCalls);
        TestAssert.True(frame.BgraPixels.Where((value, index) => index % 4 != 3).Any(value => value > 40), "No rendered geometry was found in the readback.");

        renderer.Resize(80, 72);
        var resizedFrame = renderer.Render(camera, new HeadPreviewOptions(), new byte[80 * 72 * 4]);
        TestAssert.Equal(80, resizedFrame.Width);
        TestAssert.Equal(72, resizedFrame.Height);
        TestAssert.Equal(1, renderer.DeviceCreationCount);
    }

    private static void PreviewLifecycleBlocksInactiveStates()
    {
        var active = new PreviewActivityState(true, true, false, false, 800, 600);
        TestAssert.True(PreviewRenderPolicy.CanRender(active), "Active visible preview should render.");
        TestAssert.True(!PreviewRenderPolicy.CanRender(active with { IsVisible = false }), "Hidden preview rendered.");
        TestAssert.True(!PreviewRenderPolicy.CanRender(active with { IsActive = false }), "Inactive preview rendered.");
        TestAssert.True(!PreviewRenderPolicy.CanRender(active with { IsMinimized = true }), "Minimised preview rendered.");
        TestAssert.True(!PreviewRenderPolicy.CanRender(active with { IsClosed = true }), "Closed preview rendered.");
        TestAssert.True(!PreviewRenderPolicy.CanRender(active with { HostWidth = 0 }), "Zero-sized preview rendered.");
    }

    private static void RendererCanHideAttachments()
    {
        using var renderer = new HeadPreviewRenderer(64, 64);
        var scene = CreateTriangleScene();
        var attachment = scene.Meshes[0] with { Name = "Attachment", IsAttachment = true };
        renderer.SetScene(scene with { Meshes = [scene.Meshes[0], attachment] });
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var visible = renderer.Render(camera, new HeadPreviewOptions(ShowAttachment: true));
        var hidden = renderer.Render(camera, new HeadPreviewOptions(ShowAttachment: false));

        TestAssert.Equal(2, visible.DrawCalls);
        TestAssert.Equal(1, hidden.DrawCalls);
    }

    private static void RendererRejectsWorkAfterDisposal()
    {
        var renderer = new HeadPreviewRenderer(32, 32);
        renderer.Dispose();
        try
        {
            renderer.Render(new HeadOrbitCamera(), new HeadPreviewOptions());
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        throw new Exception("Disposed renderer accepted a frame request.");
    }

    private static HeadPreviewScene CreateTriangleScene()
    {
        var vertices = new[]
        {
            CreateVertex(new Vector3(-0.8f, -0.7f, 0)),
            CreateVertex(new Vector3(0.8f, -0.7f, 0)),
            CreateVertex(new Vector3(0, 0.8f, 0))
        };
        var material = new HeadPreviewMaterial("Synthetic_Face", HeadMaterialFamily.Skin);
        var mesh = new HeadPreviewMesh("Triangle", vertices, [0u, 1u, 2u], [new HeadPreviewSection(0, 3, 0, material)]);
        return new HeadPreviewScene(
            "Synthetic head",
            [mesh],
            new HeadPreviewBounds(new Vector3(-1, -1, -0.1f), new Vector3(1, 1, 0.1f)));
    }

    private static HeadPreviewMesh CreateQuadMesh(
        string name,
        float minimumX,
        float minimumY,
        float maximumX,
        float maximumY,
        float z,
        HeadPreviewMaterial material)
    {
        var vertices = new[]
        {
            CreateVertex(new Vector3(minimumX, minimumY, z)),
            CreateVertex(new Vector3(maximumX, minimumY, z)),
            CreateVertex(new Vector3(maximumX, maximumY, z)),
            CreateVertex(new Vector3(minimumX, maximumY, z))
        };
        return new HeadPreviewMesh(
            name,
            vertices,
            [0u, 1u, 2u, 0u, 2u, 3u],
            [new HeadPreviewSection(0, 6, 0, material)]);
    }

    private static HeadPreviewVertex CreateVertex(Vector3 position) => new(
        position,
        Vector3.UnitZ,
        new Vector4(1, 0, 0, 1),
        Vector2.Zero,
        0,
        0,
        0,
        0,
        new Vector4(1, 0, 0, 0));
}
