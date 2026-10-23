using MorphFaceEditor.Core.Services;
using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using SharpDX.DXGI;

using static MorphFaceEditor.Tests.RenderTestReadback;

namespace MorphFaceEditor.Tests;

/// <summary>GPU data-contract checks for AO using an actual LE2 human female head.</summary>
public static class AmbientOcclusionRenderingTests
{
    private const string CorpusPath = "tests/Global Morphs/LE2 GlobalMorphs.pcc";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("HMF AO composition changes ambient diffuse only", HmfAoCompositionChangesAmbientDiffuseOnly),
        new("HMF AO surface normals follow normal maps without changing edge depth", HmfAoSurfaceNormalTracksNormalMapAtEdges)
    ];

    private static void HmfAoCompositionChangesAmbientDiffuseOnly()
    {
        var scene = LoadHmfScene();
        var camera = CreateCamera(scene.Bounds);
        using var renderer = new HeadPreviewRenderer(192, 192);
        renderer.SetScene(scene);

        renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: true,
            ShadowsEnabled: true,
            PostProcessingEnabled: true));

        var sceneColour = ReadFloatTexture(renderer, "Resolved", Format.R16G16B16A16_Float);
        var ambient = ReadFloatTexture(renderer, "ResolvedAmbient", Format.R16G16B16A16_Float);
        var visibility = ReadFloatTexture(renderer, "AoVisibility", Format.R16G16_Float);
        var surface = ReadFloatTexture(renderer, "AoSurface", Format.R32G32B32A32_Float);
        var composed = ReadFloatTexture(renderer, "AoComposited", Format.R16G16B16A16_Float);
        RequireSameSize(sceneColour, ambient, visibility, surface, composed);

        var checkedPixels = 0;
        var attenuatedPixels = 0;
        for (var pixel = 0; pixel < surface.Values.Length / 4; pixel++)
        {
            var offset = pixel * 4;
            var depth = surface.Values[offset + 3];
            TestAssert.True(float.IsFinite(depth), "HMF AO surface preparation wrote a non-finite device depth.");
            if (depth >= 1f)
            {
                continue;
            }

            TestAssert.True(IsFinitePixel(sceneColour, ambient, visibility, composed, pixel),
                $"AO inputs or output contained a non-finite value at covered HMF pixel {pixel}.");

            var contact = visibility.Values[pixel * 2];
            var cavity = visibility.Values[pixel * 2 + 1];
            TestAssert.True(contact is >= 0 and <= 1 && cavity is >= 0 and <= 1,
                $"AO visibility was outside [0, 1] at covered HMF pixel {pixel}.");
            var combinedVisibility = MathF.Pow(contact * cavity,
                HeadPreviewAmbientOcclusionSettings.Default.PowerExponent);
            var hasAmbient = false;
            for (var channel = 0; channel < 3; channel++)
            {
                var sceneValue = sceneColour.Values[offset + channel];
                var ambientValue = ambient.Values[offset + channel];
                var composedValue = composed.Values[offset + channel];
                hasAmbient |= Math.Abs(ambientValue) > 0.002f;

                // The AO stage may replace only the ambient diffuse contribution;
                // direct, specular and emissive radiance stay in the scene term.
                var expected = sceneValue - ambientValue + ambientValue * combinedVisibility;
                TestAssert.True(Math.Abs(expected - composedValue) <= Math.Max(0.008f, Math.Abs(expected) * 0.003f),
                    $"AO composition changed a non-ambient HDR contribution at HMF pixel {pixel}, channel {channel} " +
                    $"(scene={sceneValue:F5}, ambient={ambientValue:F5}, visibility={combinedVisibility:F5}, " +
                    $"composed={composedValue:F5}).");
            }

            if (hasAmbient)
            {
                checkedPixels++;
                if (combinedVisibility < 0.995f) attenuatedPixels++;
            }
        }

        TestAssert.True(checkedPixels > 64,
            $"The LE2 HMF fixture exposed too few covered pixels with ambient diffuse ({checkedPixels}).");
        TestAssert.True(attenuatedPixels > 0,
            "The covered HMF pixels had no AO attenuation to apply to their ambient diffuse contribution.");
    }

    private static void HmfAoSurfaceNormalTracksNormalMapAtEdges()
    {
        var scene = LoadHmfScene();
        var camera = CreateCamera(scene.Bounds);
        using var renderer = new HeadPreviewRenderer(192, 192);
        renderer.SetScene(scene);

        var normalMaterials = scene.Meshes
            .SelectMany(mesh => mesh.Sections)
            .Select(section => section.Material)
            .DistinctBy(material => material.Key)
            .Where(material => material.Textures.Values.Any(texture => texture.Role == TextureRole.Normal))
            .ToArray();
        TestAssert.True(normalMaterials.Length > 0,
            "The selected LE2 HMF fixture has no normal-mapped material to exercise the AO surface input.");

        renderer.UpdateMaterials(CreateNormalMapUpdates(normalMaterials, [128, 128, 255, 255], "flat"));
        renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: true, ShadowsEnabled: true));
        var flat = ReadFloatTexture(renderer, "AoSurface", Format.R32G32B32A32_Float);
        var flatPlane = ReadFloatTexture(renderer, "AoReceiverPlane", Format.R16G16B16A16_Float);

        renderer.UpdateMaterials(CreateNormalMapUpdates(normalMaterials, [255, 128, 128, 255], "tilted"));
        renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: true, ShadowsEnabled: true));
        var tilted = ReadFloatTexture(renderer, "AoSurface", Format.R32G32B32A32_Float);
        var tiltedPlane = ReadFloatTexture(renderer, "AoReceiverPlane", Format.R16G16B16A16_Float);
        RequireSameSize(flat, tilted);
        TestAssert.True(flatPlane.Values.SequenceEqual(tiltedPlane.Values),
            "Changing material normal maps changed the actual AO receiver triangle planes.");
        var capturedPlanes = 0;
        for (var pixel = 0; pixel < flat.Values.Length / 4; pixel++)
        {
            var offset = pixel * 4;
            if (flat.Values[offset + 3] >= 1) continue;
            var sampleIndex = flatPlane.Values[offset + 3];
            TestAssert.True(sampleIndex >= 0 && sampleIndex < renderer.MultisampleCount &&
                sampleIndex == MathF.Truncate(sampleIndex),
                "A covered HMF receiver plane has an invalid selected MSAA sample index.");
            var plane = new Vector3(flatPlane.Values[offset] * 2 - 1,
                flatPlane.Values[offset + 1] * 2 - 1, flatPlane.Values[offset + 2] * 2 - 1);
            TestAssert.True(float.IsFinite(plane.LengthSquared()) &&
                Math.Abs(plane.LengthSquared() - 1) < 0.01f,
                "A covered HMF pixel has an invalid receiver plane normal.");
            if (Math.Abs(plane.X) + Math.Abs(plane.Y) > 0.1f) capturedPlanes++;
        }
        TestAssert.True(capturedPlanes > 32,
            "The HMF receiver plane target still contains its clear value instead of triangle data.");

        var edgePixels = 0;
        var changedNormals = 0;
        for (var y = 1; y < flat.Height - 1; y++)
        for (var x = 1; x < flat.Width - 1; x++)
        {
            var pixel = y * flat.Width + x;
            var offset = pixel * 4;
            var flatDepth = flat.Values[offset + 3];
            if (flatDepth >= 1f || !HasUncoveredNeighbour(flat, x, y))
            {
                continue;
            }

            edgePixels++;
            TestAssert.Near(flatDepth, tilted.Values[offset + 3], 1e-6f);
            var normalDelta = Math.Abs(flat.Values[offset] - tilted.Values[offset])
                              + Math.Abs(flat.Values[offset + 1] - tilted.Values[offset + 1])
                              + Math.Abs(flat.Values[offset + 2] - tilted.Values[offset + 2]);
            if (normalDelta > 0.05f) changedNormals++;
        }

        TestAssert.True(edgePixels > 0,
            "The HMF view did not expose any covered pixels at the surface/background edge.");
        TestAssert.True(changedNormals > 0,
            "Changing the HMF normal map did not change AO surface normals at covered edge pixels.");
    }

    private static IReadOnlyDictionary<string, HeadPreviewMaterial> CreateNormalMapUpdates(
        IReadOnlyList<HeadPreviewMaterial> materials,
        byte[] rgba,
        string variant)
    {
        var updates = new Dictionary<string, HeadPreviewMaterial>(StringComparer.Ordinal);
        foreach (var material in materials)
        {
            var textures = material.Textures.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Role == TextureRole.Normal
                    ? entry.Value with
                    {
                        Key = $"{entry.Value.Key}-ao-{variant}",
                        Width = 1,
                        Height = 1,
                        Rgba8 = rgba.ToArray(),
                        Mips = null
                    }
                    : entry.Value,
                StringComparer.OrdinalIgnoreCase);
            updates.Add(material.Key, material with { Textures = textures });
        }
        return updates;
    }

    private static bool HasUncoveredNeighbour(FloatTarget surface, int x, int y)
    {
        var width = surface.Width;
        var left = (y * width + x - 1) * 4 + 3;
        var right = (y * width + x + 1) * 4 + 3;
        var above = ((y - 1) * width + x) * 4 + 3;
        var below = ((y + 1) * width + x) * 4 + 3;
        return surface.Values[left] >= 1f || surface.Values[right] >= 1f ||
               surface.Values[above] >= 1f || surface.Values[below] >= 1f;
    }

    private static bool IsFinitePixel(FloatTarget scene, FloatTarget ambient, FloatTarget visibility, FloatTarget composed, int pixel)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            var offset = pixel * 4 + channel;
            if (!float.IsFinite(scene.Values[offset]) || !float.IsFinite(ambient.Values[offset]) ||
                !float.IsFinite(composed.Values[offset])) return false;
        }
        return float.IsFinite(visibility.Values[pixel * 2]) && float.IsFinite(visibility.Values[pixel * 2 + 1]);
    }

    private static void RequireSameSize(params FloatTarget[] targets)
    {
        var expected = targets[0];
        foreach (var target in targets.Skip(1))
        {
            TestAssert.Equal(expected.Width, target.Width);
            TestAssert.Equal(expected.Height, target.Height);
        }
    }

    private static HeadPreviewScene LoadHmfScene()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var loader = new MorphFacePreviewLoadService(
            new HeadPreviewSceneFactory(),
            new MorphTargetCatalog(),
            MorphFaceProfileRegistry.CreateDefault(),
            new MorphFacePackageReader());
        var catalog = new MorphFaceCatalogService().ReadAsync(CorpusPath).GetAwaiter().GetResult();
        var face = catalog.Faces.FirstOrDefault(candidate =>
            candidate.ProfileKey.Equals("le2-human-female", StringComparison.OrdinalIgnoreCase) &&
            candidate.InstancedPath.Contains("arv_kenson", StringComparison.OrdinalIgnoreCase))
            ?? catalog.Faces.FirstOrDefault(candidate =>
                candidate.ProfileKey.Equals("le2-human-female", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("LE2 GlobalMorphs contains no supported HMF face for AO tests.");
        return loader.LoadAsync(CorpusPath, face.InstancedPath).GetAwaiter().GetResult().Scene;
    }

    private static HeadOrbitCamera CreateCamera(HeadPreviewBounds bounds)
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(bounds);
        camera.Orbit(0.45f, -0.18f);
        return camera;
    }
}
