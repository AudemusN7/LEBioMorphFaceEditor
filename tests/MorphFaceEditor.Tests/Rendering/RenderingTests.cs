using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using DxMathUtil = SharpDX.MathUtil;

namespace MorphFaceEditor.Tests;

// Preview geometry, material output and D3D resource lifecycle contracts.
public static class RenderingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("orbit camera retains framing when bounds change", OrbitCameraRetainsFramingWhenBoundsChange),
        new("LE tangent basis uses TangentZ handedness", TangentBasisUsesTangentZHandedness),
        new("unlit mode bypasses lighting and ambient occlusion", UnlitModeBypassesLightingAndAmbientOcclusion),
        new("renderer releases face resources and draws again after clear", RendererReleasesFaceResourcesAfterClear),
        new("scene factory maps non-contiguous LOD IDs to baked ordinals", SceneFactoryMapsNonContiguousLodIds),
        new("scene factory falls back to base geometry for an unmatched baked LOD", SceneFactoryHandlesUnmatchedBakedLod),
        new("custom mesh sections use the mesh material-slot order", CustomMeshUsesAuthoredMaterialOrder),
        new("custom mesh preview ignores BioMorphFace geometry and skeleton", CustomMeshIgnoresMorphFaceGeometry),
        new("detached attachments retain their native bind placement", DetachedAttachmentsRetainNativeBindPlacement),
        new("fixed-bake preview skins preserved imported geometry", FixedBakePreviewSkinsPreservedGeometry),
        new("scene publication settles with a second viewport frame", ScenePublicationSettlesWithSecondFrame),
        new("renderer rejects work after deterministic disposal", RendererRejectsWorkAfterDisposal),
        .. ShadowRenderingTests.All,
        .. HdrRenderingTests.All
    ];

    private static void SceneFactoryMapsNonContiguousLodIds()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh(lowerLodIndex: 2);
        var bakedLod2 = mesh.Lods[1].Positions
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
            [mesh.Lods[0].Positions, bakedLod2],
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
            .Take(1)
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

        TestAssert.True(!loaded.TopologyDiagnostics.IsValid,
            "The custom-mesh regression did not reproduce incompatible BioMorphFace bake topology.");

        var previewMesh = new HeadPreviewSceneFactory().Create(loaded).Meshes[0];

        TestAssert.Equal(new Vector3(0, 0, 1), previewMesh.Vertices[1].Position);
        TestAssert.True(!previewMesh.ApplySkinning,
            "The custom base mesh was still being transformed by the BioMorphFace skeleton.");
    }

    private static void FixedBakePreviewSkinsPreservedGeometry()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var baked = mesh.Positions.Select(position => position + new Vector3(25, 0, 0)).ToArray();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Imported.PlayerFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [baked],
            []);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document));
        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            geometryMode: MorphFaceGeometryMode.FixedBake);

        session.SetBoneAxis("root", 0, 3);
        var scene = new HeadPreviewSceneFactory().CreateEditable(loaded, session.Evaluation);

        TestAssert.True(scene.Meshes[0].ApplySkinning,
            "The fixed-bake preview disabled the imported mesh's verified skin weights.");
        TestAssert.Equal(baked[0], session.Evaluation.Geometry.Positions[0]);
        TestAssert.True(scene.SkinningPalette is { Count: > 0 } && scene.SkinningPalette[0] != Matrix4x4.Identity,
            "The edited fixed-bake skeleton did not reach the preview palette.");
    }

    private static void DetachedAttachmentsRetainNativeBindPlacement()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("DetachedMesh.CustomHead", "ImportedMesh"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            mesh.Source,
            [],
            [new BoneTranslation("root", new Vector3(-4.5f, 0, -35))],
            MorphFaceMaterialOverrides.Empty,
            mesh.Lods.Select(lod => lod.Positions).ToArray(),
            []);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            mesh,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document))
        {
            UsesNativeAttachmentBindPose = true
        };

        var scene = new HeadPreviewSceneFactory().Create(loaded);

        TestAssert.True(
            scene.Meshes[0].Vertices.Select(vertex => vertex.Position)
                .SequenceEqual(scene.Meshes[1].Vertices.Select(vertex => vertex.Position)),
            "The detached face pose displaced an attachment from its native bind geometry.");
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

    private static void RendererReleasesFaceResourcesAfterClear()
    {
        using var renderer = new HeadPreviewRenderer(64, 64);
        var scene = CreateTriangleScene();
        var material = scene.Meshes[0].Sections[0].Material with
        {
            Textures = new Dictionary<string, HeadPreviewTexture>
            {
                ["HED_Diff"] = new("face-texture", "HED_Diff", 1, 1, [255, 0, 0, 255],
                    TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false)
            }
        };
        scene = scene with
        {
            Meshes = [scene.Meshes[0] with { Sections = [new HeadPreviewSection(0, 3, 0, material)] }]
        };
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        renderer.SetScene(scene);
        renderer.Render(camera, new HeadPreviewOptions());
        TestAssert.True(renderer.GpuTextureBytes > 0, "The fixture did not upload a face texture.");
        var uploads = renderer.TextureUploadCount;

        renderer.ClearScene();
        renderer.ClearScene();
        TestAssert.Equal(0L, renderer.GpuTextureBytes);
        TestAssert.Equal(0, renderer.Render(camera, new HeadPreviewOptions()).DrawCalls);
        renderer.Resize(80, 72);
        renderer.SetScene(scene);
        var frame = renderer.Render(camera, new HeadPreviewOptions());
        TestAssert.Equal(1, frame.DrawCalls);
        TestAssert.Equal(80, frame.Width);
        TestAssert.True(renderer.TextureUploadCount > uploads, "The cleared texture was not uploaded again.");
        TestAssert.Equal(1, renderer.DeviceCreationCount);
    }

    private static void ScenePublicationSettlesWithSecondFrame()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var image = new System.Windows.Controls.Image();
                var host = new System.Windows.Controls.Grid();
                host.Children.Add(image);
                var window = new System.Windows.Window
                {
                    Width = 96,
                    Height = 96,
                    Left = -2000,
                    Top = -2000,
                    ShowInTaskbar = false,
                    WindowStyle = System.Windows.WindowStyle.None,
                    Content = host
                };
                window.Show();
                PumpDispatcher();

                using var controller = new HeadPreviewHostController(window, host, image);
                controller.SetScene(CreateTriangleScene());
                for (var index = 0; index < 8 && controller.Diagnostics.FramesRendered < 2; index++)
                {
                    PumpDispatcher();
                }

                TestAssert.Equal(2L, controller.Diagnostics.FramesRendered);
                TestAssert.True(image.Source is System.Windows.Media.Imaging.WriteableBitmap,
                    "The settled frame was not presented to the WPF image.");
                TestAssert.Near(DxMathUtil.DegreesToRadians(32), controller.Camera.Yaw, 1e-6f);
                TestAssert.Near(DxMathUtil.DegreesToRadians(4), controller.Camera.Pitch, 1e-6f);
                for (var load = 0; load < 3; load++)
                {
                    controller.Camera.Orbit(0.3f, 0.2f);
                    controller.Camera.Pan(0.1f, 0.1f);
                    controller.Camera.Zoom(120);
                    var yaw = controller.Camera.Yaw;
                    var pitch = controller.Camera.Pitch;
                    var target = controller.Camera.Target;
                    var distance = controller.Camera.Distance;
                    controller.ClearScene();
                    TestAssert.True(image.Source is null, "Clearing left the previous face visible.");
                    var frameCount = controller.Diagnostics.FramesRendered;
                    controller.RequestRender(allowInactive: true);
                    PumpDispatcher();
                    TestAssert.Equal(frameCount, controller.Diagnostics.FramesRendered);
                    // A species change requests a framing reset, but even that
                    // must retain the user's chosen rotation.
                    var resetPosition = load == 1;
                    controller.SetScene(CreateTriangleScene(), resetPosition);
                    PumpDispatcher();
                    TestAssert.Near(yaw, controller.Camera.Yaw, 1e-6f);
                    TestAssert.Near(pitch, controller.Camera.Pitch, 1e-6f);
                    if (resetPosition)
                    {
                        TestAssert.Equal(CreateTriangleScene().Bounds.Center, controller.Camera.Target);
                    }
                    else
                    {
                        TestAssert.Equal(target, controller.Camera.Target);
                        TestAssert.Near(distance, controller.Camera.Distance, 1e-6f);
                    }
                    TestAssert.Equal(frameCount + 2, controller.Diagnostics.FramesRendered);
                    TestAssert.Equal(1, controller.Diagnostics.RendererCreations);
                    TestAssert.Equal(0, controller.Diagnostics.RendererDisposals);
                    TestAssert.Equal(0, controller.Diagnostics.ResizeOperations);
                }
                window.Close();
                TestAssert.Equal(1, controller.Diagnostics.RendererDisposals);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The WPF scene-publication regression failed.", failure);
        }

        static void PumpDispatcher()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
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
