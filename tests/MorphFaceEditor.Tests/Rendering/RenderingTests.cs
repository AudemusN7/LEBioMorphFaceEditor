using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        new("mapped readback preserves pixels and releases mapping after writer failure", MappedReadbackPreservesPixelsAfterWriterFailure),
        new("scene factory maps non-contiguous LOD IDs to baked ordinals", SceneFactoryMapsNonContiguousLodIds),
        new("scene factory falls back to base geometry for an unmatched baked LOD", SceneFactoryHandlesUnmatchedBakedLod),
        new("custom mesh sections use the mesh material-slot order", CustomMeshUsesAuthoredMaterialOrder),
        new("custom mesh preview ignores BioMorphFace geometry and skeleton", CustomMeshIgnoresMorphFaceGeometry),
        new("detached attachments retain their native bind placement", DetachedAttachmentsRetainNativeBindPlacement),
        new("fixed-bake preview skins preserved imported geometry", FixedBakePreviewSkinsPreservedGeometry),
        new("deferred and live scene updates match a fresh full render", SceneUpdatesMatchFreshRendering),
        new("scene publication presents current pixels without a settling render", ScenePublicationPresentsCurrentPixels),
        new("viewport publishes new sizes during window and pane resizing", ViewportPublishesDuringResize),
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
        var hair = mesh with { Source = TestFixtures.CreateIdentity("Imported.Hair", "SkeletalMesh") };
        var other = mesh with { Source = TestFixtures.CreateIdentity("Imported.Other", "SkeletalMesh") };
        var baked = mesh.Positions.Select(position => position + new Vector3(25, 0, 0)).ToArray();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Imported.PlayerFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            hair.Source,
            [],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [baked],
            []);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            hair,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document))
        {
            OtherMeshes = [other]
        };
        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            geometryMode: MorphFaceGeometryMode.FixedBake);

        session.SetBoneAxis("root", 0, 3);
        var factory = new HeadPreviewSceneFactory();
        var scene = factory.CreateEditable(loaded, session.Evaluation);
        var update = factory.CreateUpdate(loaded, session.Evaluation);

        TestAssert.True(scene.Meshes[0].ApplySkinning,
            "The fixed-bake preview disabled the imported mesh's verified skin weights.");
        TestAssert.Equal(baked[0], session.Evaluation.Geometry.Positions[0]);
        TestAssert.True(scene.SkinningPalette is { Count: > 0 } && scene.SkinningPalette[0] != Matrix4x4.Identity,
            "The edited fixed-bake skeleton did not reach the preview palette.");
        AssertDeformationMatchesScene(scene, update);
        TestAssert.Equal(new Vector3(0, 0, 3), update.AttachmentUpdates[0].Vertices[0].Position);
        TestAssert.Equal(hair.Source.InstancedPath, update.AttachmentUpdates[0].MeshName);
        TestAssert.Equal(other.Source.InstancedPath, update.AttachmentUpdates[1].MeshName);
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

        var session = new MorphFaceEditingSession(document, mesh, [], geometryMode: MorphFaceGeometryMode.FixedBake);
        session.SetBoneAxis("root", 0, 3);
        var factory = new HeadPreviewSceneFactory();
        var editable = factory.CreateEditable(loaded, session.Evaluation);
        var update = factory.CreateUpdate(loaded, session.Evaluation);
        AssertDeformationMatchesScene(editable, update);
        TestAssert.True(scene.Meshes[1].Vertices.SequenceEqual(update.AttachmentUpdates[0].Vertices),
            "An incremental face edit displaced the detached attachment from its native bind geometry.");
    }

    private static void AssertDeformationMatchesScene(HeadPreviewScene scene, HeadPreviewDeformationUpdate update)
    {
        TestAssert.Equal(scene.Meshes[0].Name, update.MeshName);
        TestAssert.True(scene.Meshes[0].Vertices.SequenceEqual(update.Vertices),
            "Incremental head vertices differ from the full editable scene.");
        TestAssert.True(scene.SkinningPalette!.SequenceEqual(update.SkinningPalette),
            "Incremental skinning differs from the full editable scene.");
        TestAssert.Equal(scene.Meshes.Count - 1, update.AttachmentUpdates.Count);
        for (var index = 0; index < update.AttachmentUpdates.Count; index++)
        {
            TestAssert.Equal(scene.Meshes[index + 1].Name, update.AttachmentUpdates[index].MeshName);
            TestAssert.True(scene.Meshes[index + 1].Vertices.SequenceEqual(update.AttachmentUpdates[index].Vertices),
                "Incremental attachment vertices differ from the full editable scene.");
        }
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
        var viewport = RenderTestReadback.GetTexture(renderer, "Resolved");
        var shadow = RenderTestReadback.GetTexture(renderer, "ShadowMap");
        var grading = RenderTestReadback.GetTexture(renderer, "GradingLut");

        renderer.ClearScene();
        renderer.ClearScene();
        TestAssert.Equal(0L, renderer.GpuTextureBytes);
        TestAssert.Equal(0, renderer.Render(camera, new HeadPreviewOptions()).DrawCalls);
        TestAssert.True(!viewport.IsDisposed && ReferenceEquals(viewport, RenderTestReadback.GetTexture(renderer, "Resolved")),
            "Clearing the face discarded the reusable viewport targets.");
        renderer.Resize(80, 72);
        TestAssert.True(viewport.IsDisposed && !shadow.IsDisposed && !grading.IsDisposed &&
                        ReferenceEquals(shadow, RenderTestReadback.GetTexture(renderer, "ShadowMap")) &&
                        ReferenceEquals(grading, RenderTestReadback.GetTexture(renderer, "GradingLut")),
            "Viewport resize retained old targets or discarded the independent shadow map/grading LUT.");
        renderer.SetScene(scene);
        var frame = renderer.Render(camera, new HeadPreviewOptions());
        TestAssert.Equal(1, frame.DrawCalls);
        TestAssert.Equal(80, frame.Width);
        TestAssert.True(renderer.TextureUploadCount > uploads, "The cleared texture was not uploaded again.");
        TestAssert.Equal(1, renderer.DeviceCreationCount);
    }

    private static void MappedReadbackPreservesPixelsAfterWriterFailure()
    {
        const int width = 65, height = 49, rowBytes = width * 4;
        using var renderer = new HeadPreviewRenderer(width, height);
        var scene = CreateTriangleScene();
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        var options = new HeadPreviewOptions(HeadPreviewRenderMode.Unlit, PostProcessingEnabled: false);
        var destination = new byte[width * height * 4];
        var expected = renderer.Render(camera, options, destination, animationTimeSeconds: 0).BgraPixels.ToArray();
        var failure = new InvalidOperationException("Intentional pixel writer failure.");
        var propagated = false;
        try { renderer.RenderInto(camera, options, (_, _, _) => throw failure, animationTimeSeconds: 0); }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, failure)) { propagated = true; }
        TestAssert.True(propagated, "Mapped publication did not propagate the original writer exception.");
        var frame = renderer.Render(camera, options, destination, animationTimeSeconds: 0);
        TestAssert.True(ReferenceEquals(destination, frame.BgraPixels) && expected.SequenceEqual(destination),
            "Packed rendering failed to recover after a writer exception.");
        var actual = new byte[destination.Length];
        var info = renderer.RenderInto(camera, options, (pointer, byteCount, stride) =>
        {
            TestAssert.True(stride >= rowBytes, "Mapped source stride is shorter than a pixel row.");
            TestAssert.Equal(checked((height - 1) * stride + rowBytes), byteCount);
            for (var row = 0; row < height; row++)
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(pointer, row * stride), actual, row * rowBytes, rowBytes);
        }, animationTimeSeconds: 0);
        TestAssert.Equal(width, info.Width);
        TestAssert.Equal(height, info.Height);
        TestAssert.True(expected.SequenceEqual(actual), "Mapped rendering failed to recover after a writer exception.");
    }

    private static void SceneUpdatesMatchFreshRendering()
    {
        var initial = CreateScene(0);
        var camera = new HeadOrbitCamera();
        camera.Fit(initial.Bounds);
        var options = new HeadPreviewOptions(
            AmbientOcclusionEnabled: false, ShadowsEnabled: true, PostProcessingEnabled: false);
        using var fresh = new HeadPreviewRenderer(128, 128);
        fresh.SetScene(initial);
        var initialPixels = fresh.Render(camera, options, animationTimeSeconds: 2.5f).BgraPixels;

        var state = new HeadPreviewSceneState();
        state.SetScene(initial);
        var deferred = CreateScene(1);
        state.UpdateDeformation(Deformation(deferred));
        state.UpdateMaterials(Materials(deferred));
        using var renderer = new HeadPreviewRenderer(128, 128, state);
        var deferredPixels = AssertMatchesFresh(deferred);
        TestAssert.True(!initialPixels.SequenceEqual(deferredPixels),
            "The deferred geometry, palette and material edits did not change the rendered fixture.");

        var live = CreateScene(2);
        renderer.UpdateDeformation(Deformation(live));
        renderer.UpdateMaterials(Materials(live));
        var livePixels = AssertMatchesFresh(live);
        TestAssert.True(!deferredPixels.SequenceEqual(livePixels),
            "The live geometry, palette and material edits did not change the rendered fixture.");

        var rejected = false;
        try
        {
            renderer.UpdateDeformation(Deformation(initial) with
            {
                AttachmentUpdates = [new HeadPreviewMeshVertexUpdate(live.Meshes[1].Name, [])]
            });
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        TestAssert.True(rejected, "A mixed update with invalid attachment geometry was accepted.");
        TestAssert.True(livePixels.SequenceEqual(renderer.Render(camera, options, animationTimeSeconds: 2.5f).BgraPixels),
            "A rejected attachment update partly changed the displayed head or skinning palette.");

        byte[] AssertMatchesFresh(HeadPreviewScene scene)
        {
            fresh.SetScene(scene);
            var expected = fresh.Render(camera, options, animationTimeSeconds: 2.5f);
            var actual = renderer.Render(camera, options, animationTimeSeconds: 2.5f);
            TestAssert.Equal(2, actual.ShadowDrawCalls);
            TestAssert.True(expected.BgraPixels.SequenceEqual(actual.BgraPixels),
                "Incremental scene state rendered differently from independently loaded full geometry and materials.");
            return actual.BgraPixels;
        }

        static HeadPreviewDeformationUpdate Deformation(HeadPreviewScene scene) => new(
            scene.Meshes[0].Name, scene.Meshes[0].Vertices, scene.SkinningPalette!)
        {
            AttachmentUpdates = [new HeadPreviewMeshVertexUpdate(scene.Meshes[1].Name, scene.Meshes[1].Vertices)]
        };

        static Dictionary<string, HeadPreviewMaterial> Materials(HeadPreviewScene scene) =>
            scene.Meshes.SelectMany(mesh => mesh.Sections).Select(section => section.Material)
                .ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase);

        static HeadPreviewScene CreateScene(int stage)
        {
            var head = CreateQuadMesh("updated-head", -1 + stage * 0.05f, -1, 1, 1 - stage * 0.08f, 0,
                Material("updated-head-material", stage, (byte)(180 - stage * 40), 170, 150)) with { ApplySkinning = true };
            var attachment = CreateQuadMesh("updated-attachment", -0.65f + stage * 0.18f, 0.05f,
                -0.15f + stage * 0.18f, 0.55f, 0.35f + stage * 0.05f,
                Material("updated-attachment-material", stage, 140, (byte)(180 - stage * 40), 200)) with { IsAttachment = true };
            return new HeadPreviewScene("Updated synthetic scene", [head, attachment],
                new HeadPreviewBounds(new Vector3(-1.2f, -1.2f, -0.05f), new Vector3(1.2f, 1.2f, 0.5f)),
                [Matrix4x4.CreateTranslation(stage * 0.08f, -stage * 0.05f, 0)]);
        }

        static HeadPreviewMaterial Material(string key, int stage, byte red, byte green, byte blue) =>
            new(key, HeadMaterialFamily.Skin)
            {
                Textures = new Dictionary<string, HeadPreviewTexture>
                {
                    ["HED_Diff"] = new($"{key}-{stage}", "HED_Diff", 1, 1, [red, green, blue, 255],
                        TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false)
                }
            };
    }

    private static void ScenePublicationPresentsCurrentPixels() => WpfTestDispatcher.Invoke(() =>
    {
        var (window, host, image) = CreatePreviewWindow(96, 96);
        using var controller = new HeadPreviewHostController(window, host, image);
        try
        {
            byte[]? firstPublishedPixels = null;
            controller.FramePresented += _ =>
            {
                if (firstPublishedPixels is not null) return;
                var first = (BitmapSource)image.Source;
                firstPublishedPixels = new byte[first.PixelWidth * first.PixelHeight * 4];
                first.CopyPixels(firstPublishedPixels, first.PixelWidth * 4, 0);
            };
            var scene = CreateTriangleScene();
            var material = scene.Meshes[0].Sections[0].Material with
            {
                Textures = new Dictionary<string, HeadPreviewTexture>
                {
                    ["HED_Diff"] = new("deferred-host-diffuse", "HED_Diff", 1, 1, [50, 110, 220, 255],
                        TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false)
                }
            };
            var deferredScene = scene with
            {
                Meshes = [scene.Meshes[0] with
                {
                    Vertices = scene.Meshes[0].Vertices.Select(vertex => vertex with
                    {
                        Position = vertex.Position + new Vector3(0.15f, 0.1f, 0)
                    }).ToArray(),
                    Sections = [new HeadPreviewSection(0, 3, 0, material)]
                }]
            };
            controller.SetScene(scene);
            controller.UpdateDeformation(new HeadPreviewDeformationUpdate(
                deferredScene.Meshes[0].Name, deferredScene.Meshes[0].Vertices, []));
            controller.UpdateMaterials(new HeadPreviewMaterialUpdate(
                new Dictionary<string, HeadPreviewMaterial> { [material.Key] = material }));
            TestAssert.Equal(0, controller.Diagnostics.RendererCreations);
            TestAssert.Equal(0, controller.Diagnostics.FramesRendered);
            window.Show();
            controller.RequestRender(allowInactive: true);
            for (var index = 0; index < 8 && firstPublishedPixels is null; index++) PumpDispatcher();

            // Initial visibility can add a lifecycle frame; replacements below need one.
            TestAssert.True(firstPublishedPixels is not null, "The first completed frame was not published to the WPF image.");
            var bitmap = (BitmapSource)image.Source;
            using (var fresh = new HeadPreviewRenderer(bitmap.PixelWidth, bitmap.PixelHeight))
            {
                fresh.SetScene(deferredScene);
                var expected = fresh.Render(controller.Camera, new HeadPreviewOptions()).BgraPixels;
                TestAssert.True(expected.SequenceEqual(firstPublishedPixels!),
                    "The first published WPF scene lost geometry or material updates made before renderer creation.");
            }
            TestAssert.Near(DxMathUtil.DegreesToRadians(32), controller.Camera.Yaw, 1e-6f);
            TestAssert.Near(DxMathUtil.DegreesToRadians(4), controller.Camera.Pitch, 1e-6f);
            foreach (var resetPosition in new[] { false, true })
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
                controller.SetScene(scene, resetPosition);
                PumpDispatcher();
                TestAssert.Near(yaw, controller.Camera.Yaw, 1e-6f);
                TestAssert.Near(pitch, controller.Camera.Pitch, 1e-6f);
                if (resetPosition)
                {
                    TestAssert.Equal(scene.Bounds.Center, controller.Camera.Target);
                }
                else
                {
                    TestAssert.Equal(target, controller.Camera.Target);
                    TestAssert.Near(distance, controller.Camera.Distance, 1e-6f);
                }
                TestAssert.Equal(frameCount + 1, controller.Diagnostics.FramesRendered);
            }
            // A pending request keeps one queue slot across clear/reload.
            var beforeQueuedReload = controller.Diagnostics.FramesRendered;
            controller.RequestRender(allowInactive: true);
            controller.ClearScene();
            controller.SetScene(scene);
            PumpDispatcher();
            TestAssert.Equal(beforeQueuedReload + 1, controller.Diagnostics.FramesRendered);
            TestAssert.Equal(1, controller.Diagnostics.RendererCreations);
            TestAssert.Equal(0, controller.Diagnostics.RendererDisposals);
            TestAssert.Equal(0, controller.Diagnostics.ResizeOperations);
        }
        finally
        {
            window.Close();
        }
        TestAssert.Equal(1, controller.Diagnostics.RendererDisposals);
    });

    private static void ViewportPublishesDuringResize() => WpfTestDispatcher.Invoke(() =>
    {
        var (window, host, image) = CreatePreviewWindow(128, 112);
        using var controller = new HeadPreviewHostController(window, host, image);
        var lastPresented = (Width: 0, Height: 0);
        string? renderFailure = null;
        controller.FramePresented += frame => lastPresented = (frame.Width, frame.Height);
        controller.PreviewFailed += message => renderFailure = message;
        try
        {
            controller.SetOptions(new HeadPreviewOptions(HeadPreviewRenderMode.Unlit,
                AmbientOcclusionEnabled: false, ShadowsEnabled: false, PostProcessingEnabled: false));
            controller.SetScene(CreateTriangleScene());
            window.Show();
            window.Activate();
            controller.RequestRender(allowInactive: true);
            PumpDispatcher(350);
            TestAssert.True(window.IsActive, "The resize fixture could not activate its offscreen window.");
            TestAssert.True(renderFailure is null, $"The resize fixture could not warm its renderer: {renderFailure}");
            TestAssert.Equal(CurrentPixelSize(), lastPresented);

            var activityChanges = 0;
            void OnActivityChanged(object? sender, EventArgs args) => activityChanges++;
            window.Activated += OnActivityChanged;
            window.Deactivated += OnActivityChanged;
            try
            {
                ExerciseResize("window", step =>
                {
                    window.Width = 128 + step * 3;
                    window.Height = 112 + step * 2;
                });
                host.HorizontalAlignment = HorizontalAlignment.Left;
                ExerciseResize("pane", step => host.Width = 180 - step * 3);
                TestAssert.Equal(0, activityChanges);
            }
            finally
            {
                window.Activated -= OnActivityChanged;
                window.Deactivated -= OnActivityChanged;
            }
        }
        finally
        {
            window.Close();
            PumpDispatcher();
        }

        void ExerciseResize(string phase, Action<int> resize)
        {
            var startingSize = lastPresented;
            var sizesDuringResize = new HashSet<(int Width, int Height)>();
            var clock = Stopwatch.StartNew();
            var sizeChanges = 0;
            var lastSizeChange = 0d;
            var maximumGap = 0d;
            void OnSizeChanged(object sender, SizeChangedEventArgs args)
            {
                var now = clock.Elapsed.TotalMilliseconds;
                if (sizeChanges > 0) maximumGap = Math.Max(maximumGap, now - lastSizeChange);
                lastSizeChange = now;
                sizeChanges++;
            }
            void OnFrame(HeadPreviewFrameInfo frame)
            {
                var size = (frame.Width, frame.Height);
                if (size != startingSize) sizesDuringResize.Add(size);
            }
            host.SizeChanged += OnSizeChanged;
            controller.FramePresented += OnFrame;
            try
            {
                for (var step = 1; step <= 18; step++)
                {
                    resize(step);
                    window.UpdateLayout();
                    PumpDispatcher(20);
                }
            }
            finally
            {
                host.SizeChanged -= OnSizeChanged;
                controller.FramePresented -= OnFrame;
            }
            TestAssert.True(sizeChanges >= 10 && clock.Elapsed.TotalMilliseconds >= 250 && maximumGap < 150,
                $"The {phase} fixture did not sustain size changes faster than the old 150 ms debounce.");
            TestAssert.True(sizesDuringResize.Count >= 2,
                $"The viewport waited for {phase} resizing to stop: {sizesDuringResize.Count} distinct updated sizes.");
            PumpDispatcher(300);
            TestAssert.True(renderFailure is null, $"Rendering failed during {phase} resizing: {renderFailure}");
            TestAssert.Equal(CurrentPixelSize(), lastPresented);
            var settledFrames = controller.Diagnostics.FramesRendered;
            PumpDispatcher(80);
            TestAssert.Equal(settledFrames, controller.Diagnostics.FramesRendered);
        }

        (int Width, int Height) CurrentPixelSize()
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(host);
            return ((int)Math.Round(host.ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(host.ActualHeight * dpi.DpiScaleY));
        }
    });

    private static (Window Window, Grid Host, Image Image) CreatePreviewWindow(int width, int height)
    {
        var image = new Image();
        var host = new Grid { Children = { image } };
        return (new Window
        {
            Width = width,
            Height = height,
            Left = -2000,
            Top = -2000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = new Grid { Children = { host } }
        }, host, image);
    }

    private static void PumpDispatcher(int milliseconds = 0)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
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
