using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Editing;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.GameFilesystem;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

/// <summary>Tests the visible effect of material edits on installed player heads.</summary>
internal static class PlayerMaterialRegressionTests
{
    internal static void Le1FemaleDefaultsRetainSkinTone()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var factory = new HeadPreviewSceneFactory();
        using var loader = new MorphFacePreviewLoadService(factory, new MorphTargetCatalog(),
            MorphFaceProfileRegistry.CreateDefault(), new MorphFacePackageReader());
        var loaded = loader.LoadAsync(StandalonePlayerMorphImportService.ResolveInstalledSeed(MorphFaceGame.LE1),
            "BIOG_MORPH_FACE.Player_Base_Female").GetAwaiter().GetResult();
        var session = loaded.MaterialEditingSession;
        session.ResetToDefaults();
        TestAssert.Near(0, session.GetScalar("HED_Lips_Tint_Scalar"), 1e-6f);
        TestAssert.True(session.CreateOverrides().Scalars.Count == 0, "Reset authored a synthetic lipstick override.");
        // Only draw the face: scalp/neck colour changes cannot mask a broken face branch.
        var scene = loaded.Scene with
        {
            Meshes = loaded.Scene.Meshes.Select(mesh => mesh with
            {
                Sections = mesh.Sections.Where(section => section.Material.Family == HeadMaterialFamily.Skin).ToArray()
            }).ToArray()
        };
        using var renderer = new HeadPreviewRenderer(192, 192);
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        camera.ResetFront();
        camera.Zoom(240);
        byte[] Render(Vector4 color, HeadPreviewRenderMode mode = HeadPreviewRenderMode.Unlit)
        {
            session.SetVector("SkinTone", color);
            renderer.UpdateMaterials(factory.CreateMaterialUpdate(loaded.Loaded, session.Materials).Materials);
            return renderer.Render(camera, new HeadPreviewOptions(mode)).BgraPixels.ToArray();
        }
        var red = Render(new Vector4(0.8f, 0.05f, 0.05f, 1));
        var green = Render(new Vector4(0.05f, 0.8f, 0.05f, 1));
        var changed = red.Zip(green).Count(pair => Math.Abs(pair.First - pair.Second) > 5);
        Console.WriteLine($"SkinTone changed {changed} channel values after defaults.");
        TestAssert.True(changed > 300, "LE1 female face stopped responding to SkinTone after defaults.");

        session.SetScalar("HED_Lips_Tint_Scalar", 0.7f);
        TestAssert.Near(0.7f, session.CreateOverrides().Scalars.Single(value => value.Name == "HED_Lips_Tint_Scalar").Value, 1e-6f);
        session.Undo();
        TestAssert.Near(0, session.GetScalar("HED_Lips_Tint_Scalar"), 1e-6f);

        // Exercise actual packed scar channels after the reset, rather than
        // inferring visual support merely from a texture binding or UI control.
        var scarPackagePath = MELoadedFiles.GetFilesLoadedInGame(MEGame.LE1, forceUseCached: true)["BIOG_HMF_HED_PROMorph_R.pcc"];
        using var scarPackage = MEPackageHandler.OpenMEPackage(scarPackagePath, forceLoadFromDisk: true);
        var scarExport = scarPackage.Exports.First(value => value.ClassName == "Texture2D" &&
            value.ObjectName.Instanced.Contains("PROCustom_Scr", StringComparison.OrdinalIgnoreCase));
        using var reader = new MorphFacePackageReader();
        var scar = reader.LoadTexture(scarPackagePath, scarExport.InstancedFullPath,
            HumanMaterialProfiles.Describe("HED_Scar", MaterialParameterKind.Texture, HeadMaterialFamily.Skin));
        session.SetTextureReference("HED_Scar", scar);
        var baseColor = new Vector4(0.35f, 0.2f, 0.15f, 1);
        var noScar = Render(baseColor);
        session.SetVector("HED_Scar_Vector", new Vector4(0.9f, 0.01f, 0.01f, 1));
        session.SetScalar("HED_Scar_Diffuse_Scalar", 1);
        var coloredScar = Render(baseColor);
        TestAssert.True(!noScar.SequenceEqual(coloredScar), "The installed scar diffuse channel had no visible effect.");
        session.SetScalar("HED_Scar_Diffuse_Scalar", 0);
        var flatScar = Render(baseColor, HeadPreviewRenderMode.Shaded);
        session.SetScalar("HED_Custom_Scar_Scalar", 1);
        var normalScar = Render(baseColor, HeadPreviewRenderMode.Shaded);
        TestAssert.True(!flatScar.SequenceEqual(normalScar), "The installed scar normal channel had no visible effect.");
    }

    internal static void PlayerEyeDefaultsCoverBothSexes()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var reader = new MorphFacePackageReader();
        foreach (var game in new[] { MorphFaceGame.LE2, MorphFaceGame.LE3 })
        foreach (var sex in new[] { "Male", "Female" })
        {
            var face = reader.Load(StandalonePlayerMorphImportService.ResolveInstalledSeed(game),
                $"BIOG_MORPH_FACE.CharacterCreation_Base_{sex}");
            var session = new MaterialEditingSession(face.Document.MaterialOverrides, face.Materials);
            var eye = session.Materials.Materials.Values.Single(value => value.Family == HeadMaterialFamily.Eyes);
            TestAssert.Near(0, eye.Scalars["Emis_Scalar"], 1e-6f);
            // Publishing another eye texture is the randomisation trigger that
            // previously activated the inherited alignment colour.
            session.SetTextureReference("EYE_Diff", eye.Textures["EYE_Diff"].Texture);
            TestAssert.Near(0, session.Materials.Materials[eye.Key].Scalars["Emis_Scalar"], 1e-6f);
            session.SetScalar("Emis_Scalar", 1.5f);
            TestAssert.Near(1.5f, session.Materials.Materials[eye.Key].Scalars["Emis_Scalar"], 1e-6f);
            session.ResetToDefaults();
            TestAssert.Near(0, session.Materials.Materials[eye.Key].Scalars["Emis_Scalar"], 1e-6f);
            Console.WriteLine($"{game} {sex} player eyes: safe default, texture update, explicit emission and reset passed.");
        }
    }
}
