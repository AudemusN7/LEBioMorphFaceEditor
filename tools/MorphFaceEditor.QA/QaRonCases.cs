using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static MainWindowViewModel FileWorkflow(QaContext c, QaDialogs dialogs, MorphFacePackageReader reader)
    {
        var context = new MorphFacePackageContextService();
        return new(dialogs, new MorphFaceCatalogService(c.Profiles),
            new MorphFacePreviewLoadService(c.Scenes, c.Targets, c.Profiles, reader), c.Scenes,
            new QaColorDialog(), new PackageReferenceService(reader, c.Textures), new MorphFacePackageWriter(), context,
            new MorphFaceConversionService(c.Profiles, c.Targets, context, c.Textures),
            new MorphFaceInterchangeService(), new QaClipboard(), MorphRandomisationCatalog.LoadEmbedded(),
            recentFiles: new RecentFileService(),
            startupPreferences: new StartupPreferencesService(c.Artifact("file-preferences.json")));
    }

    // All mutations are on copies; retain their original structural baseline for the aggregate audit.
    private static string CopyWorkflowPackage(QaContext c, string input, string name)
    {
        var copy = c.Artifact(name);
        File.Copy(input, copy);
        using var package = MEPackageHandler.OpenMEPackage(copy, forceLoadFromDisk: true);
        c.ExistingPackageSources[copy] = input;
        c.ExistingPackageBaselines[copy] = PackageIntegrity.CaptureIssues(package);
        return copy;
    }

    private static async Task SelectFileFace(QaContext c, MainWindowViewModel vm, string package, string face)
    {
        await c.Measure("Open copied RON source", () => vm.OpenDroppedFileAsync(package), QaStageKind.Load);
        CheckVm(vm);
        vm.SelectedFace = vm.Faces.Single(value => value.InstancedPath == face);
        await c.Measure("Load RON source face", () => Execute(vm.LoadSelectedFaceCommand), QaStageKind.Load);
        CheckVm(vm);
        QaContext.Require(vm.Editor is not null, "RON source did not load an editor.");
    }

    private static async Task RonRoundTrip(QaContext c, bool morphOnly)
    {
        var source = c.Source();
        var copy = CopyWorkflowPackage(c, source.PackagePath, "ron-workspace.pcc");
        using var reader = new MorphFacePackageReader();
        var dialogs = new QaDialogs();
        using var vm = FileWorkflow(c, dialogs, reader);
        await SelectFileFace(c, vm, copy, source.FacePath);
        var export = morphOnly ? vm.ExportMorphFeaturesRonCommand : vm.ExportMorphRonCommand;
        var profile = c.Profiles.Find(source.Game, source.FacePath, source.BaseHeadPath);
        var expectedBlocked = c.Face.Family is "TUF" or "BAF" || morphOnly && c.Face.Family == "ALN" ||
            !RonNpcDonorResolver.SupportsMorphRonExport(profile, source.BaseHeadPath);
        if (expectedBlocked)
        {
            QaContext.Require(!export.CanExecute(null), "Unsupported RON export was enabled.");
            c.Current.Evidence["expectedCapabilityBoundary"] = "This archetype deliberately disables this RON export; material RON is tested separately.";
            return;
        }
        dialogs.RonExportPath = c.Artifact(morphOnly ? "morphs.ron" : "full.ron");
        if (morphOnly && vm.Editor!.CanEditBones && vm.Editor.Bones.FirstOrDefault(value => value.IsAvailable) is { } bone)
        {
            var before = bone.Value;
            bone.BeginEdit(); bone.Value += 0.01f; bone.EndEdit();
            c.Current.Evidence["independentBoneEdit"] = new { bone.BoneName, bone.Axis, Before = before, After = bone.Value };
        }
        var initial = vm.Editor!.CreateDraft();
        await c.Measure("Export RON command", () => Execute(export), QaStageKind.FileTransfer);
        CheckVm(vm);
        var head = TseHeadMorphRon.Read(dialogs.RonExportPath);
        QaContext.Require(TseHeadMorphRon.IsMorphFeaturesOnly(dialogs.RonExportPath) == morphOnly,
            "Exported the wrong RON payload mode.");
        if (c.Face.IsPlayer && !morphOnly) VerifyPlayerOutput(c, head);
        if (morphOnly)
        {
            QaContext.Require(head.MaterialData.Scalars.Count == 0 && head.MaterialData.Vectors.Count == 0 &&
                head.MaterialData.Textures.Count == 0 && head.MorphData.BakedLods.Count == 0 &&
                head.MorphData.FinalSkeleton.Count == 0, "Morph-only RON contains unrelated head data.");
            if (!vm.Editor.CanEditMorphFeatures)
            {
                QaContext.Require(!vm.ImportMorphFeaturesRonCommand.CanExecute(null), "Read-only source enabled morph import.");
                c.Current.Evidence["expectedCapabilityBoundary"] = "Morph data can be exported, but this source has no proven editable morph surface; importing into it must remain disabled.";
                return;
            }
        }
        var feature = vm.Editor.Features.FirstOrDefault(value => value.IsEditable &&
            head.MorphData.MorphFeatures.Any(exported => exported.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)));
        if (morphOnly && feature is null)
        {
            feature = vm.Editor.Features.First(value => value.IsEditable);
            var explicitValue = new MorphFeatureValue(feature.Name, feature.Value);
            head = head with { MorphData = head.MorphData with { MorphFeatures = head.MorphData.MorphFeatures.Append(explicitValue).ToArray() } };
            TseHeadMorphRon.Write(dialogs.RonExportPath, head, Provenance(c.Face), morphFeaturesOnly: true);
            c.Current.Evidence["explicitFeatureImportCoverage"] = new { explicitValue.Name, explicitValue.Offset,
                Reason = "The source has no authored editable feature entry; test importing an explicit value for an available control instead of changing donors." };
        }
        if (feature is not null)
        {
            feature.BeginEdit(); feature.Value += 0.02f; feature.EndEdit();
            QaContext.Require(!initial.MorphFeatures.SequenceEqual(vm.Editor.CreateDraft().MorphFeatures),
                "RON restoration setup did not perturb any morph value.");
        }
        else QaContext.Require(!morphOnly, "Morph-only RON route has no editable morph feature.");
        await EditMaterial(c, vm.Editor, 0.02f);
        var beforeImport = vm.Editor.CreateDraft();
        QaContext.Require(MaterialSignature(ToHead(initial).MaterialData) != MaterialSignature(ToHead(beforeImport).MaterialData),
            "RON restoration setup did not perturb any material value.");
        dialogs.ImportPath = dialogs.RonExportPath;
        await c.Measure("Import RON command", () => Execute(morphOnly ? vm.ImportMorphFeaturesRonCommand : vm.ImportMorphFaceRonCommand), QaStageKind.FileTransfer);
        CheckVm(vm);
        var imported = vm.Editor!.CreateDraft();
        if (morphOnly)
        {
            foreach (var exported in head.MorphData.MorphFeatures)
                QaContext.Require(imported.MorphFeatures.Any(value => value.Name.Equals(exported.Name, StringComparison.OrdinalIgnoreCase) &&
                    value.Offset == exported.Offset), "Morph-only RON did not restore feature " + exported.Name);
            QaContext.Require(initial.FinalSkeleton.SequenceEqual(imported.FinalSkeleton),
                "Restoring exported feature values did not restore their evaluated bone positions.");
            AssertMaterials(beforeImport, imported);
        }
        else RequireHead(head, ReadPackageHead(imported.Source.PackagePath, vm.SelectedFace!.InstancedPath), "Full RON import");
        AssertAttachments(initial, imported);
        var expectedSaved = morphOnly ? ToHead(imported) : ReadPackageHead(imported.Source.PackagePath, vm.SelectedFace!.InstancedPath);
        // A full import marks the workspace dirty even when values match its original file.
        await c.Measure("Save imported RON", () => Execute(vm.SaveCommand), QaStageKind.FileTransfer);
        CheckVm(vm);
        var reopened = await c.Load(copy, source.FacePath, MorphFaceGeometryMode.RelativeBake);
        RequireHead(expectedSaved, ReadPackageHead(copy, source.FacePath), "Saved RON import");
        await c.Render(reopened.Scene, "ron-reopened.png");
        c.Current.Evidence["ronMode"] = morphOnly ? "Morph Only" : "Full";
    }

    private static TseHeadMorph ReadPackageHead(string packagePath, string facePath)
    {
        var context = new MorphFacePackageContextService();
        var references = MorphFaceReferenceInspector.Inspect(packagePath).Single(value => value.FacePath == facePath);
        return new(references.HairMeshPath ?? "None", references.OtherMeshPaths,
            context.CaptureMorphData(packagePath, facePath), context.CaptureMaterialData(packagePath, facePath));
    }

    private static RonExportProvenance Provenance(QaFaceSpec face) =>
        new(RonExportProducer.MFE, "QA", face.Game, face.Family, face.IsPlayer);

    private static void AssertAttachments(MorphFaceDocument expected, MorphFaceDocument actual)
    {
        var before = ToHead(expected);
        var after = ToHead(actual);
        QaContext.Require(string.Equals(before.HairMesh, after.HairMesh, StringComparison.OrdinalIgnoreCase) &&
            before.AccessoryMeshes.SequenceEqual(after.AccessoryMeshes, StringComparer.OrdinalIgnoreCase),
            "RON workflow changed hair or accessory identities.");
    }

    private static void VerifyPlayerOutput(QaContext c, TseHeadMorph head, TseHeadMorph? preserved = null)
    {
        new PlayerRuntimeAssetValidator().Verify(c.Face.Game, head, preserved);
        c.Current.Evidence["playerRuntimeSources"] = "Every newly written mesh/texture identity passed the production seekfree/startup/face-factory source validator; preserved native career references are exempt only when unchanged.";
    }

    private static async Task CrossGameRon(QaContext c, MorphFaceGame target)
    {
        var source = c.Source();
        var ron = c.Artifact("cross-game.ron");
        await c.Measure("Export cross-game full RON", () => Task.Run(() =>
            new MorphFacePackageContextService().ExportRon(source.PackagePath, source.FacePath, ron, Provenance(c.Face))), QaStageKind.FileTransfer);
        using var reader = new MorphFacePackageReader();
        var dialogs = new QaDialogs { ImportPath = ron, ImportGame = target, RonDestination = new(c.Face.IsPlayer
            ? RonImportDestination.PlayerWorkspace : RonImportDestination.NpcFace, c.Face.Family) };
        using var vm = FileWorkflow(c, dialogs, reader);
        await c.Measure("Explicit Import RON into other game", () => Execute(vm.ImportMorphCommand), QaStageKind.FileTransfer);
        CheckVm(vm);
        QaContext.Require(vm.Editor is not null && vm.SelectedFace is not null, "Cross-game RON did not open an editor.");
        var imported = vm.Editor!.CreateDraft();
        var original = TseHeadMorphRon.Read(ron);
        var raw = ReadPackageHead(imported.Source.PackagePath, vm.SelectedFace!.InstancedPath);
        var differences = QaHeadPayload.GeometryDifferences(original.MorphData, raw.MorphData);
        QaContext.Require(differences.Count == 0,
            "Cross-game RON lost authored morph data: " + string.Join("; ", differences));
        var saved = await c.Save(imported, vm.Editor!.CreateDraft().Source.PackagePath, "cross-game.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath, MorphFaceGeometryMode.RelativeBake);
        QaContext.Require(reopened.Loaded.Game == target, "Cross-game RON opened in the source game.");
        AssertGeometry(imported, reopened.Loaded.Document);
        AssertMaterials(imported, reopened.Loaded.Document);
        AssertAttachments(imported, reopened.Loaded.Document);
        c.Current.Evidence["destinationGame"] = target;
        c.Current.Evidence["importMessages"] = dialogs.Messages;
        await c.Render(reopened.Scene, "cross-game-reopened.png");
    }
}
