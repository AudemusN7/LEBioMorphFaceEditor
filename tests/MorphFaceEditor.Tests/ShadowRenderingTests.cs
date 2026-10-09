using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

// GPU-only synthetic tests for directional shadows. These fixtures avoid game assets.
public static class ShadowRenderingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("key shadows darken a receiver without blacking out ambient light", KeyShadowDarkensReceiver),
        new("transparent hair texels leave holes in key shadows", HairAlphaHolesDoNotCastSolidShadows),
        new("unlit and diagnostic modes ignore key shadows", NonShadedModesIgnoreShadows),
        new("hidden attachments do not cast key shadows", HiddenAttachmentsDoNotCastShadows),
        new("deforming a caster moves and restores its key shadow", DeformingCasterMovesItsShadow),
        new("skinned caster pose and resize keep shadows in sync", SkinnedCasterPoseAndResizeKeepShadowsInSync)
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

    private static void HairAlphaHolesDoNotCastSolidShadows()
    {
        using var renderer = new HeadPreviewRenderer(192, 192);
        var transparent = CreateHairMaterial(0, 0);
        var scene = CreateShadowScene(includeOccluder: true, occluderMaterial: transparent);
        renderer.SetScene(scene);
        var camera = CreateShadowCamera(scene.Bounds);
        var transparentCount = RenderShadowDifferenceCount(renderer, camera);

        var partial = CreateHairMaterial(255, 0);
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [partial.Key] = partial });
        var partialCount = RenderShadowDifferenceCount(renderer, camera);

        var opaque = CreateHairMaterial(255, 255);
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [opaque.Key] = opaque });
        var opaqueCount = RenderShadowDifferenceCount(renderer, camera);

        TestAssert.True(transparentCount == 0,
            $"Fully transparent hair texels cast shadows onto the receiver ({transparentCount} darkened pixels).");
        TestAssert.True(partialCount > 20 && partialCount < opaqueCount,
            $"Half-transparent hair did not cast a cutout shadow " +
            $"(transparent={transparentCount}, partial={partialCount}, opaque={opaqueCount}).");
        TestAssert.True(opaqueCount > 20,
            $"Opaque hair texels cast no key shadow onto the receiver ({opaqueCount} darkened pixels).");
    }

    private static int RenderShadowDifferenceCount(HeadPreviewRenderer renderer, HeadOrbitCamera camera)
    {
        var withoutShadows = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false)).BgraPixels.ToArray();
        var withShadows = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: true)).BgraPixels;
        return CountDarkenedPixels(withoutShadows, withShadows);
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

    private static void DeformingCasterMovesItsShadow()
    {
        using var renderer = new HeadPreviewRenderer(192, 192);
        var scene = CreateShadowScene(includeOccluder: true);
        renderer.SetScene(scene);
        var camera = CreateShadowCamera(scene.Bounds);
        var firstOff = RenderPixels(renderer, camera, shadowsEnabled: false);
        var firstOn = RenderPixels(renderer, camera, shadowsEnabled: true);
        var firstMask = DarkenedPixelMask(firstOff, firstOn);

        renderer.UpdateMeshVertices("raised-caster", CreateQuadVertices(0.25f, -0.5f, 0.75f, 0, 0.35f));
        var movedOff = RenderPixels(renderer, camera, shadowsEnabled: false);
        var movedOn = RenderPixels(renderer, camera, shadowsEnabled: true);
        var movedMask = DarkenedPixelMask(movedOff, movedOn);

        var firstCount = firstMask.Count(value => value);
        var movedCount = movedMask.Count(value => value);
        TestAssert.True(firstCount > 20 && movedCount > 20,
            "The caster did not produce a measurable shadow before and after deformation.");
        var overlap = firstMask.Zip(movedMask).Count(pair => pair.First && pair.Second);
        var smallerShadow = Math.Min(firstCount, movedCount);
        TestAssert.True(overlap < smallerShadow * 0.8,
            $"The shadow mask did not follow the moved caster (overlap={overlap}, smaller={smallerShadow}).");

        renderer.UpdateMeshVertices("raised-caster", CreateQuadVertices(-0.65f, 0.05f, -0.15f, 0.55f, 0.35f));
        var restoredOn = RenderPixels(renderer, camera, shadowsEnabled: true);
        TestAssert.True(firstOn.SequenceEqual(restoredOn),
            "Restoring the caster vertices did not restore the original shadow frame.");
    }

    private static void SkinnedCasterPoseAndResizeKeepShadowsInSync()
    {
        using var renderer = new HeadPreviewRenderer(192, 192);
        var scene = CreateShadowScene(includeOccluder: true, skinnedOccluder: true);
        renderer.SetScene(scene);
        var camera = CreateShadowCamera(scene.Bounds);
        var firstOff = RenderPixels(renderer, camera, shadowsEnabled: false);
        var firstOn = RenderPixels(renderer, camera, shadowsEnabled: true);
        var firstMask = DarkenedPixelMask(firstOff, firstOn);

        renderer.SetSkinningPalette([Matrix4x4.CreateTranslation(0.8f, -0.55f, 0)]);
        var movedOff = RenderPixels(renderer, camera, shadowsEnabled: false);
        var movedOn = RenderPixels(renderer, camera, shadowsEnabled: true);
        var movedMask = DarkenedPixelMask(movedOff, movedOn);
        var overlap = firstMask.Zip(movedMask).Count(pair => pair.First && pair.Second);
        var smallerShadow = Math.Min(firstMask.Count(value => value), movedMask.Count(value => value));
        TestAssert.True(smallerShadow > 20 && overlap < smallerShadow * 0.8,
            $"Skinning-palette translation did not move the caster's shadow " +
            $"(overlap={overlap}, smaller={smallerShadow}).");

        renderer.SetSkinningPalette([Matrix4x4.Identity]);
        var restoredOff = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false, ShadowsEnabled: false));
        var restoredOn = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false, ShadowsEnabled: true));
        var restoredMask = DarkenedPixelMask(restoredOff.BgraPixels, restoredOn.BgraPixels);
        var restoreOverlap = firstMask.Zip(restoredMask).Count(pair => pair.First && pair.Second);
        TestAssert.True(restoreOverlap > firstMask.Count(value => value) * 0.65,
            $"Restoring the skinning palette did not restore the original shadow footprint " +
            $"(overlap={restoreOverlap}, original={firstMask.Count(value => value)}).");

        renderer.Resize(512, 512);
        _ = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false, ShadowsEnabled: false)); // Warm new viewport targets.
        var largeOn = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false, ShadowsEnabled: true));
        var largeOff = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false, ShadowsEnabled: false));
        TestAssert.True(CountDarkenedPixels(restoredOff.BgraPixels, restoredOn.BgraPixels) > 20,
            "The 192px shadow frame had no visible caster shadow.");
        TestAssert.True(CountDarkenedPixels(largeOff.BgraPixels, largeOn.BgraPixels) > 20,
            "The resized 512px viewport lost its caster shadow.");
        TestAssert.True(renderer.DeviceCreationCount == 1,
            "Resizing the viewport recreated the D3D device instead of retaining it.");
        TestAssert.True(largeOn.BgraPixels.Length == 512 * 512 * 4 && largeOff.BgraPixels.Length == 512 * 512 * 4,
            "The resized shadow renders did not return the requested 512px frame.");
        Console.WriteLine(
            $"Shadow viewport probe: 192px={restoredOn.Width}x{restoredOn.Height}, " +
            $"renderMs192Off={restoredOff.RenderTime.TotalMilliseconds:F1}, " +
            $"renderMs192On={restoredOn.RenderTime.TotalMilliseconds:F1}, " +
            $"shadowDraws192={restoredOn.ShadowDrawCalls}, " +
            $"renderMs512Off={largeOff.RenderTime.TotalMilliseconds:F1}, " +
            $"renderMs512On={largeOn.RenderTime.TotalMilliseconds:F1}, " +
            $"shadowDraws={largeOn.ShadowDrawCalls}, " +
            $"deviceCreations={renderer.DeviceCreationCount}.");
    }

    private static HeadPreviewScene CreateShadowScene(
        bool includeOccluder,
        HeadPreviewMaterial? occluderMaterial = null,
        bool attachmentOccluder = false,
        bool skinnedOccluder = false)
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
                isAttachment: attachmentOccluder,
                applySkinning: skinnedOccluder));
        }

        return new HeadPreviewScene(
            "Synthetic shadow scene",
            meshes,
            new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.4f)),
            skinnedOccluder ? [Matrix4x4.Identity] : null);
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
        bool isAttachment = false,
        bool applySkinning = false)
    {
        return new HeadPreviewMesh(
            name, CreateQuadVertices(minX, minY, maxX, maxY, z), [0u, 1u, 2u, 0u, 2u, 3u],
            [new HeadPreviewSection(0, 6, 0, material)],
            IsAttachment: isAttachment,
            ApplySkinning: applySkinning);
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

    private static bool[] DarkenedPixelMask(byte[] withoutShadows, byte[] withShadows)
    {
        var mask = new bool[withoutShadows.Length / 4];
        var pixel = 0;
        for (var offset = 0; offset < withoutShadows.Length; offset += 4, pixel++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                if (withShadows[offset + channel] + 2 < withoutShadows[offset + channel])
                {
                    mask[pixel] = true;
                    break;
                }
            }
        }
        return mask;
    }

    private static byte[] RenderPixels(
        HeadPreviewRenderer renderer,
        HeadOrbitCamera camera,
        bool shadowsEnabled) => renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: shadowsEnabled)).BgraPixels.ToArray();

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
