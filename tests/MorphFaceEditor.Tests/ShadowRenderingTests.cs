using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

// Pipeline/coverage guards only. Face-shadow quality is accepted through real-head QA.
public static class ShadowRenderingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("key shadows darken a receiver without blacking out ambient light", KeyShadowDarkensReceiver),
        new("transparent hair texels leave holes in key shadows", HairAlphaHolesDoNotCastSolidShadows),
        new("unlit and diagnostic modes ignore key shadows", NonShadedModesIgnoreShadows),
        new("hidden attachments do not cast key shadows", HiddenAttachmentsDoNotCastShadows),
        new("preset shadow softness respects an explicit override", PresetShadowSoftnessRespectsOverride)
    ];

    private static void KeyShadowDarkensReceiver()
    {
        using var renderer = new HeadPreviewRenderer(192, 192);
        var scene = CreateShadowScene(includeOccluder: true);
        renderer.SetScene(scene);
        var camera = CreateShadowCamera(scene.Bounds);

        var withoutShadows = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false)).BgraPixels.ToArray();
        var withShadows = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: true)).BgraPixels;
        var changed = CountDarkenedPixels(withoutShadows, withShadows);
        var shadowedAverage = AverageDarkenedRgb(withoutShadows, withShadows);

        TestAssert.True(changed > 20,
            $"The raised opaque card cast no visible key shadow onto its receiver ({changed} pixels changed).");
        TestAssert.True(changed < withoutShadows.Length / 12,
            $"Key-shadow toggle changed too much of the frame ({changed} pixels); light or background was altered.");
        TestAssert.True(shadowedAverage > 8,
            $"Shadowed receiver lost fill/ambient illumination (mean RGB {shadowedAverage:F1}).");
    }

    private static void PresetShadowSoftnessRespectsOverride()
    {
        using var renderer = new HeadPreviewRenderer(192, 192);
        var scene = CreateShadowScene(includeOccluder: true);
        renderer.SetScene(scene);
        var camera = CreateShadowCamera(scene.Bounds);
        foreach (var preset in Enum.GetValues<HeadPreviewLightingPreset>())
        {
            var options = new HeadPreviewOptions(LightingPreset: preset, AmbientOcclusionEnabled: false);
            var automatic = renderer.Render(camera, options).BgraPixels.ToArray();
            var explicitSoftness = HeadPreviewLightingPresets.Get(preset).ShadowSoftnessWorld;
            var explicitFrame = renderer.Render(camera, options with { ShadowSoftnessWorld = explicitSoftness });
            TestAssert.True(automatic.SequenceEqual(explicitFrame.BgraPixels),
                $"{preset} did not use its shadow softness by default.");
        }
        var studioOptions = new HeadPreviewOptions(AmbientOcclusionEnabled: false);
        var soft = renderer.Render(camera, studioOptions).BgraPixels.ToArray();
        var hard = renderer.Render(camera, studioOptions with { ShadowSoftnessWorld = 0 });
        TestAssert.True(!soft.SequenceEqual(hard.BgraPixels), "An explicit hard-shadow override was ignored.");
    }

    private static void HairAlphaHolesDoNotCastSolidShadows()
    {
        using var renderer = new HeadPreviewRenderer(192, 192);
        var transparent = CreateHairMaterial(0, 0);
        var scene = CreateShadowScene(includeOccluder: true, occluderMaterial: transparent);
        renderer.SetScene(scene);
        var camera = CreateShadowCamera(scene.Bounds);
        var transparentShadow = RenderShadowDifference(renderer, camera);

        var partial = CreateHairMaterial(255, 0);
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [partial.Key] = partial });
        var partialShadow = RenderShadowDifference(renderer, camera);

        var opaque = CreateHairMaterial(255, 255);
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [opaque.Key] = opaque });
        var opaqueShadow = RenderShadowDifference(renderer, camera);

        TestAssert.True(transparentShadow.Count == 0,
            $"Fully transparent hair texels cast shadows onto the receiver ({transparentShadow.Count} darkened pixels).");
        // A soft kernel can spread the partial shadow over the same pixel set
        // as the opaque card. Compare total attenuation to verify the holes.
        TestAssert.True(partialShadow.Count > 20 && partialShadow.Darkening < opaqueShadow.Darkening,
            $"Half-transparent hair did not cast a cutout shadow " +
            $"(partial={partialShadow}, opaque={opaqueShadow}).");
        TestAssert.True(opaqueShadow.Count > 20,
            $"Opaque hair texels cast no key shadow onto the receiver ({opaqueShadow.Count} darkened pixels).");
    }

    private static (int Count, long Darkening) RenderShadowDifference(HeadPreviewRenderer renderer, HeadOrbitCamera camera)
    {
        var withoutShadows = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false)).BgraPixels.ToArray();
        var withShadows = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: true)).BgraPixels;
        long darkening = 0;
        for (var offset = 0; offset < withoutShadows.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                darkening += Math.Max(0, withoutShadows[offset + channel] - withShadows[offset + channel]);
            }
        }
        return (CountDarkenedPixels(withoutShadows, withShadows), darkening);
    }

    private static void NonShadedModesIgnoreShadows()
    {
        using var renderer = new HeadPreviewRenderer(128, 128);
        var scene = CreateShadowScene(includeOccluder: true);
        renderer.SetScene(scene);
        var camera = CreateShadowCamera(scene.Bounds);

        foreach (var mode in new[] { HeadPreviewRenderMode.Unlit, HeadPreviewRenderMode.Diagnostic })
        {
            var withoutShadows = renderer.Render(camera, new HeadPreviewOptions(
                RenderMode: mode,
                AmbientOcclusionEnabled: false,
                ShadowsEnabled: false)).BgraPixels.ToArray();
            var withShadows = renderer.Render(camera, new HeadPreviewOptions(
                RenderMode: mode,
                AmbientOcclusionEnabled: false,
                ShadowsEnabled: true)).BgraPixels;

            TestAssert.True(withoutShadows.SequenceEqual(withShadows),
                $"{mode} output changed when key shadows were toggled.");
        }
    }

    private static void HiddenAttachmentsDoNotCastShadows()
    {
        using var renderer = new HeadPreviewRenderer(160, 160);
        var attachedCaster = CreateShadowScene(includeOccluder: true, attachmentOccluder: true);
        renderer.SetScene(attachedCaster);
        var camera = CreateShadowCamera(attachedCaster.Bounds);
        var hiddenAttachment = renderer.Render(camera, new HeadPreviewOptions(
            ShowAttachment: false,
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: true)).BgraPixels.ToArray();

        var receiverOnly = CreateShadowScene(includeOccluder: false);
        renderer.SetScene(receiverOnly);
        var receiverOnlyPixels = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: true)).BgraPixels;

        TestAssert.True(hiddenAttachment.SequenceEqual(receiverOnlyPixels),
            "A hidden attachment remained in the shadow caster list.");
    }

    private static HeadPreviewScene CreateShadowScene(
        bool includeOccluder,
        HeadPreviewMaterial? occluderMaterial = null,
        bool attachmentOccluder = false)
    {
        var receiverMaterial = CreateNeutralOpaqueMaterial("shadow-receiver");
        var meshes = new List<HeadPreviewMesh>
        {
            CreateQuad("receiver", -1, -1, 1, 1, 0, receiverMaterial)
        };
        if (includeOccluder)
        {
            meshes.Add(CreateQuad(
                "raised-caster", -0.65f, 0.05f, -0.15f, 0.55f, 0.35f,
                occluderMaterial ?? CreateNeutralOpaqueMaterial("shadow-caster"),
                isAttachment: attachmentOccluder));
        }

        return new HeadPreviewScene(
            "Synthetic shadow scene",
            meshes,
            new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.4f)));
    }

    private static HeadPreviewMaterial CreateNeutralOpaqueMaterial(string key) =>
        new(key, key, HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One },
            new Dictionary<string, HeadPreviewTexture>());

    private static HeadPreviewMaterial CreateHairMaterial(byte leftAlpha, byte rightAlpha)
    {
        var rgba = new byte[] { 180, 180, 180, leftAlpha, 180, 180, 180, rightAlpha };
        var diffuse = new HeadPreviewTexture(
            $"hair-alpha-{leftAlpha}-{rightAlpha}", "HAIR_Diff", 2, 1, rgba,
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Mask, true);
        return new HeadPreviewMaterial(
            "alpha-hair", "alpha-hair", HeadMaterialFamily.Hair,
            HeadMaterialBlendMode.Translucent, true,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4> { ["HED_Hair_Colour_Vector"] = Vector4.One },
            new Dictionary<string, HeadPreviewTexture> { ["HAIR_Diff"] = diffuse });
    }

    private static HeadPreviewMesh CreateQuad(
        string name,
        float minX,
        float minY,
        float maxX,
        float maxY,
        float z,
        HeadPreviewMaterial material,
        bool isAttachment = false)
    {
        return new HeadPreviewMesh(
            name, CreateQuadVertices(minX, minY, maxX, maxY, z), [0u, 1u, 2u, 0u, 2u, 3u],
            [new HeadPreviewSection(0, 6, 0, material)],
            IsAttachment: isAttachment);
    }

    private static HeadPreviewVertex[] CreateQuadVertices(
        float minX,
        float minY,
        float maxX,
        float maxY,
        float z) =>
    [
        CreateVertex(new Vector3(minX, minY, z), new Vector2(0, 1)),
        CreateVertex(new Vector3(maxX, minY, z), new Vector2(1, 1)),
        CreateVertex(new Vector3(maxX, maxY, z), new Vector2(1, 0)),
        CreateVertex(new Vector3(minX, maxY, z), new Vector2(0, 0))
    ];

    private static HeadPreviewVertex CreateVertex(Vector3 position, Vector2 uv) => new(
        position, Vector3.UnitZ, new Vector4(1, 0, 0, 1), uv,
        0, 0, 0, 0, new Vector4(1, 0, 0, 0));

    private static HeadOrbitCamera CreateShadowCamera(HeadPreviewBounds bounds)
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(bounds);
        return camera;
    }

    private static int CountDarkenedPixels(byte[] withoutShadows, byte[] withShadows)
    {
        var count = 0;
        for (var offset = 0; offset < withoutShadows.Length; offset += 4)
        {
            var darkened = false;
            for (var channel = 0; channel < 3; channel++)
            {
                darkened |= withShadows[offset + channel] + 2 < withoutShadows[offset + channel];
            }
            if (darkened)
            {
                count++;
            }
        }
        return count;
    }

    private static double AverageDarkenedRgb(byte[] withoutShadows, byte[] withShadows)
    {
        long total = 0;
        var count = 0;
        for (var offset = 0; offset < withoutShadows.Length; offset += 4)
        {
            var darkened = false;
            for (var channel = 0; channel < 3; channel++)
            {
                darkened |= withShadows[offset + channel] + 2 < withoutShadows[offset + channel];
            }
            if (!darkened)
            {
                continue;
            }
            total += withShadows[offset] + withShadows[offset + 1] + withShadows[offset + 2];
            count++;
        }
        return count == 0 ? 0 : total / (count * 3.0);
    }
}
