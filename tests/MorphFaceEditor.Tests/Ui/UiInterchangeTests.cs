using System.Numerics;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Packages;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiInterchangeTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("full RON import preserves existing history and lower-LOD sculpt data", MatchingFullRonKeepsHistory),
        new("RON capture protects against source replacement during destination choice", RonDropUsesCapturedFile),
        new("nonfinite RON export preserves its destination and source", RonExportRejectsNonFiniteFields),
        new("Custom material RON preserves slot assignments and ignores unsupported values", CustomMeshMaterialRonIgnoresSlots)
    ];

    private static void MatchingFullRonKeepsHistory() => RunWithDispatcher(async () =>
    {
        static MorphFaceDocument Canonical(MorphFaceDocument draft)
        {
            static AssetIdentity? Reference(AssetIdentity? asset) => asset is null ? null : asset with
            { InstancedPath = TextureCatalogPicker.CanonicalPath(asset.InstancedPath, asset.PackagePath), PackagePath = "", UIndex = 0 };
            return draft with
            {
                HairMeshReference = Reference(draft.HairMeshReference),
                OtherMeshReferences = draft.OtherMeshReferences.Select(Reference).ToArray(),
                MaterialOverrides = draft.MaterialOverrides with
                { Textures = draft.MaterialOverrides.Textures.Select(value => value with { TextureReference = Reference(value.TextureReference) }).ToArray() }
            };
        }
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-RonUndo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Career.pcsav");
            File.Copy(PcsavHeadMorphTests.Fixtures().Single(path => Path.GetFileName(path) == "LE3-Male-Custom.pcsav"), source);
            var originalBytes = File.ReadAllBytes(source);
            var ron = Path.Combine(directory, "Head.ron");
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs { MorphImportPath = ron };
            using var shell = CreateMainWindowViewModel(reader, dialogs);
            await shell.OpenDroppedFileAsync(source);
            TestAssert.True(!shell.HasError && shell.Editor is not null, shell.ErrorMessage ?? "Career setup failed.");
            var editor = shell.Editor!;
            var initial = editor.CreateDraft();
            var feature = editor.Features.First(value => value.IsEditable);
            var scalar = editor.Material.Scalars.First();
            var bone = editor.Bones.First(value => value.IsAvailable);
            feature.Value += 0.03f;
            scalar.Value += 0.125f;
            bone.Value += 0.01f;
            var desired = editor.CreateDraft();
            var head = new HeadMorphPayload(desired.HairMeshReference?.InstancedPath ?? "None",
                desired.OtherMeshReferences.OfType<AssetIdentity>().Select(value => value.InstancedPath).ToArray(),
                new(desired.MorphFeatures, desired.FinalSkeleton, desired.BakedLods),
                new(desired.MaterialOverrides.Scalars, desired.MaterialOverrides.Vectors, desired.MaterialOverrides.Textures));
            TseHeadMorphRon.Write(ron, head, new(RonExportProducer.MFE, MorphFaceGame.LE3, "HMM", true));
            editor.UndoCommand.Execute(null);
            editor.UndoCommand.Execute(null);
            editor.UndoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(initial, editor.CreateDraft()), "Setup did not undo cleanly.");
            scalar.Value += 0.0625f;
            var beforeImport = editor.CreateDraft();
            var clears = 0;
            shell.PreviewCleared += (_, _) => clears++;
            await ((AsyncRelayCommand)shell.ImportMorphFaceRonCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError, shell.ErrorMessage ?? "Full import failed.");
            TestAssert.True(ReferenceEquals(editor, shell.Editor) && clears == 0,
                "A reproducible full RON import reloaded the editor and discarded history.");
            TestAssert.True(MorphFaceEditorStateComparer.Equals(Canonical(desired), Canonical(editor.CreateDraft())), "Import did not restore the complete head.");
            editor.UndoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(beforeImport, editor.CreateDraft()), "Full import was not one Undo step.");
            editor.UndoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(initial, editor.CreateDraft()), "Import lost the preceding edit history.");
            editor.RedoCommand.Execute(null);
            editor.RedoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(Canonical(desired), Canonical(editor.CreateDraft())), "Redo lost imported head data.");
            TestAssert.True(originalBytes.SequenceEqual(File.ReadAllBytes(source)), "Import wrote to the native save.");
            var sculpted = head.MorphData.BakedLods.Select(lod => lod.ToArray()).ToArray();
            if (sculpted.Length < 2) sculpted = [.. sculpted, [new Vector3(1, 2, 3)]];
            else sculpted[1][0] += new Vector3(1, 0, 0);
            TseHeadMorphRon.Write(ron, head with { MorphData = head.MorphData with { BakedLods = sculpted } },
                new(RonExportProducer.MFE, MorphFaceGame.LE3, "HMM", true));
            var beforeFallback = shell.Editor;
            await ((AsyncRelayCommand)shell.ImportMorphFaceRonCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && !ReferenceEquals(beforeFallback, shell.Editor), shell.ErrorMessage ?? "A lower-LOD sculpt bypassed full-data import.");
            TestAssert.True(shell.Editor!.CreateDraft().BakedLods[1].SequenceEqual(sculpted[1]), "Fallback discarded the sculpted lower LOD.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static void RonDropUsesCapturedFile() => RunWithDispatcher(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-CapturedRon-{Guid.NewGuid():N}.ron");
        const string changed = "(lod0_vertices: [],)";
        try
        {
            var head = new MorphFacePackageContextService().CapturePlayerDefaultHead(MorphFaceGame.LE2, StandalonePlayerSex.Male);
            TestFixtures.WriteLexRon(path, head, MorphFaceGame.LE2, "HMM", true);
            var legacySyntax = File.ReadAllText(path).Replace(",\r\n", "\r\n").Replace(",\n", "\n");
            File.WriteAllText(path, legacySyntax, Encoding.Unicode);
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs
            {
                StandaloneGameChoiceResult = MorphFaceGame.LE2,
                StandaloneNameChoiceResult = "MFE_CapturedRon",
                RonImportDestinationChoiceResult = new(RonImportDestination.PlayerWorkspace, null),
                StandaloneGameChoiceAction = () => File.WriteAllText(path, changed)
            };
            using var shell = CreateMainWindowViewModel(reader, dialogs);
            await shell.OpenDroppedFileAsync(path);
            TestAssert.True(!shell.HasError && shell.Editor is not null, shell.ErrorMessage ?? "Captured head did not open.");
            TestAssert.Equal(1, dialogs.StandaloneGameChoiceCount);
            TestAssert.Equal(1, dialogs.RonImportDestinationChoiceCount);
            TestAssert.True(shell.Editor!.CreateDraft().BakedLods[0].SequenceEqual(head.MorphData.BakedLods[0]),
                "Changing the source during destination selection changed the captured import.");
            shell.Editor.Material.Scalars.First().Value += 0.125f;
            await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
            TestAssert.True(shell.HasError && shell.IsDirty && File.ReadAllText(path) == changed,
                "Saving the captured import overwrote a subsequently changed source.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    });

    private static void RonExportRejectsNonFiniteFields() => RunWithDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-RonNonFinite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Source.pcc");
            File.Copy(Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE1 GlobalMorphs.pcc")), source);
            var facePath = MorphFaceReferenceInspector.Inspect(source).First(face =>
                face.BaseHeadPath?.Contains("HMM", StringComparison.OrdinalIgnoreCase) == true).FacePath;
            using (var package = MEPackageHandler.OpenMEPackage(source, forceLoadFromDisk: true))
            {
                var face = package.FindExport(facePath, "BioMorphFace")!;
                var material = (ExportEntry)face.GetProperty<ObjectProperty>("m_oMaterialOverrides")!.ResolveToEntry(package);
                var scalars = material.GetProperty<ArrayProperty<StructProperty>>("m_aScalarOverrides")!;
                scalars.Add(new StructProperty("ScalarParameter", false,
                    new NameProperty("MFE_NativeNaN", "nName"), new FloatProperty(float.NaN, "sValue")));
                material.WriteProperty(scalars);
                package.Save(source);
            }
            var before = PackageFingerprint.Capture(source);
            var destination = Path.Combine(root, "Head.ron");
            File.WriteAllText(destination, "previous export");
            using var reader = new MorphFacePackageReader();
            using var shell = CreateMainWindowViewModel(reader, new StubEditorDialogs { RonExportPath = destination });
            await shell.OpenDroppedFileAsync(source);
            shell.SelectedFace = shell.Faces.Single(face => face.InstancedPath == facePath);
            TestAssert.True(shell.ExportMorphRonCommand.CanExecute(null), "Fixture did not reach the RON export command.");
            await ((AsyncRelayCommand)shell.ExportMorphRonCommand).ExecuteAsync();
            TestAssert.True(shell.HasError && File.ReadAllText(destination) == "previous export",
                "RON export published unreadable nonfinite values over the previous export.");
            TestAssert.Equal(before, PackageFingerprint.Capture(source));
        }
        finally { Directory.Delete(root, recursive: true); }
    });

    private static void CustomMeshMaterialRonIgnoresSlots() => RunWithDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-CustomMaterialDrop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var meshPath = Path.Combine(directory, "Custom.psk");
            var ronPath = Path.Combine(directory, "Parameters.ron");
            new PSK
            {
                Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
                Wedges = [new() { PointIndex = 0 }, new() { PointIndex = 1, U = 1 }, new() { PointIndex = 2, V = 1 }],
                Faces = [new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2, MatIndex = 1 },
                    new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2, MatIndex = 3 }],
                Materials = [new() { Name = "Unused" }, new() { Name = "Head A" }, new() { Name = "Unused" }, new() { Name = "Head B" }],
                Bones = [], Weights = [], VertexNormals = []
            }.ToFile(meshPath);
            var sourceBytes = File.ReadAllBytes(meshPath);
            var template = MaterialTestFixtures.CreateSession().Materials.Materials.Values.First();
            var option = new CustomMaterialAssignmentOption("human-test", "Human", "human", "head", HeadMaterialFamily.Skin, template)
                { ParameterScopeKey = "human", ParameterScopeLabel = "Human" };
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs { StandaloneGameChoiceResult = MorphFaceGame.LE1, StandaloneNameChoiceResult = "Custom" };
            using var shell = CreateMainWindowViewModel(reader, dialogs, customMaterials: new MaterialDropCatalogue(option));
            await shell.OpenDroppedFileAsync(meshPath);
            var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Custom mesh did not open.");
            foreach (var slot in editor.CustomMaterialSlots) slot.Selected = slot.Options.Single(value => value.Assignment == option);
            var workspace = editor.CustomMaterials!;
            var assignments = workspace.Assignments.ToArray();
            var original = editor.CreateDraft();
            var before = MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(workspace, MorphFaceGame.LE1, "Before",
                editor.Material.CaptureInterchangeData()));
            var values = new MorphFaceMaterialData([new("HED_Norm_Blend", 0.875f), new("Unsupported", 9)],
                [new("SkinTone", new(0.2f, 0.3f, 0.4f, 1)), new("HED_Norm_Blend", Vector4.Zero)],
                [new("HED_Diff", null), new("UnsupportedTexture", new("missing.pcc", "Missing.Texture", 1, "Texture2D"))]);
            var faceFile = FaceMaterialFileService.Capture(MorphFaceGame.LE1, "Face", values);
            var scopedFile = faceFile with { Parameters = new Dictionary<string, MorphFaceMaterialData> { ["human"] = values } };
            var document = scopedFile with
            {
                Slots = [new(0, "Other mesh", "not-installed", "Unused assignment", "human", HeadMaterialFamily.Skin)]
            };
            File.WriteAllText(ronPath, MaterialRonCodec.WriteMfe(document));
            await shell.OpenDroppedFileAsync(ronPath);
            TestAssert.True(!shell.HasError && ReferenceEquals(editor, shell.Editor), shell.ErrorMessage ?? "Material drop replaced the editor.");
            TestAssert.True(assignments.SequenceEqual(workspace.Assignments), "Parameter import changed material assignments.");
            foreach (var material in editor.Material.Materials.Materials.Values)
            {
                TestAssert.Near(0.875f, material.Scalars["HED_Norm_Blend"], 0);
                TestAssert.Equal(new Vector4(0.2f, 0.3f, 0.4f, 1), material.Vectors["SkinTone"]);
                TestAssert.True(!material.Scalars.ContainsKey("Unsupported") && !material.Vectors.ContainsKey("HED_Norm_Blend"),
                    "Import added an unsupported parameter or changed its kind.");
            }
            TestAssert.True(editor.IsDirty && dialogs.LastInformation is null, "Unsupported values produced warnings or import lost dirty state.");
            TestAssert.True(editor.Material.CaptureInterchangeData().Scalars.All(value => !value.Name.Contains("Unsupported")),
                "Ignored parameter was retained for export.");
            editor.UndoCommand.Execute(null);
            TestAssert.Equal(before, MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(workspace, MorphFaceGame.LE1,
                "Before", editor.Material.CaptureInterchangeData())));
            TestAssert.True(assignments.SequenceEqual(workspace.Assignments), "Undo changed slot assignments.");
            editor.RedoCommand.Execute(null);
            TestAssert.Near(0.875f, editor.Material.Scalars.Single(value => value.Name == MaterialParameterControlKey.Create("human", "HED_Norm_Blend")).Value, 0);
            editor.UndoCommand.Execute(null);
            TestAssert.True(original.BakedLods[0].SequenceEqual(editor.CreateDraft().BakedLods[0]) && sourceBytes.SequenceEqual(File.ReadAllBytes(meshPath)),
                "Material import changed geometry or the source mesh.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private sealed class MaterialDropCatalogue(CustomMaterialAssignmentOption option) : ICustomMaterialTemplateCatalog
    {
        public CustomMaterialTemplateCatalogResult Load(MorphFaceGame game) => new([option], [], []);
    }
}
