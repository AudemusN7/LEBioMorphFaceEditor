using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static async Task RejectedCareer(QaContext c, string input)
    {
        c.PreparedPlayer = null;
        c.TrackInput(input);
        try
        {
            PcsavHeadMorphWorkspace.Open(input);
        }
        catch (Exception error) when (error is InvalidDataException || error is PcsavException { Reason: PcsavError.InvalidFile or PcsavError.UnsupportedFormat })
        {
            var copy = c.Artifact("rejected-input.pcsav");
            File.Copy(input, copy);
            var fingerprint = PackageFingerprint.Capture(copy);
            using var reader = new MorphFacePackageReader();
            using var vm = FileWorkflow(c, new QaDialogs(), reader);
            await vm.OpenDroppedFileAsync(copy);
            QaContext.Require(!string.IsNullOrWhiteSpace(vm.ErrorMessage) && !vm.HasWorkspace &&
                fingerprint == PackageFingerprint.Capture(copy), "Invalid career input was not safely refused.");
            c.Current.Evidence["inputClassification"] = new { Rejected = true, Reason = error.Message, vm.ErrorMessage,
                Scope = "Controlled refusal of an uncurated input; this does not establish whether the external save is valid." };
            return;
        }
        throw new InvalidDataException("The selected incomplete-LOD refusal fixture unexpectedly parsed successfully; reassess its coverage classification.");
    }

    private static async Task PcsavJourney(QaContext c, string fixtureName)
    {
        var input = Path.Combine(c.Root, "fixtures", "PCSAV_Test", fixtureName);
        c.TrackInput(input);
        var copy = c.Artifact("career.pcsav");
        File.Copy(input, copy);
        var original = PcsavHeadMorphWorkspace.Open(copy);
        c.Face = new(original.Game, original.Sex == StandalonePlayerSex.Male ? "HMM" : "HMF", null, original.Sex);
        c.Current.Evidence["inputClassification"] = new { original.Game, original.Sex, original.HasHeadMorph };
        await c.WarmGame(original.Game);
        var ron = c.Artifact("career-input.ron");
        var head = original.HasHeadMorph ? original.Head :
            new MorphFacePackageContextService().CapturePlayerDefaultHead(original.Game, original.Sex);
        var registry = await c.Textures.ReadAsync(original.Game);
        if (!registry.IsAvailable) throw new QaPrerequisiteException("PCSAV journey requires a Ready asset database.");
        var assets = StandalonePlayerAssetCatalog.ForHead(original.Game, head, registry.Candidates, attachmentMeshes: registry.AttachmentMeshes);
        // The editing projection may omit missing attachments; native bytes stay in the career workspace.
        if (original.HasHeadMorph) head = original.PrepareEditingHead(assets, out _);
        TseHeadMorphRon.Write(ron, head);
        using var imported = await c.Measure("PCSAV head to Player workspace", () => Task.Run(() =>
            new StandalonePlayerMorphImportService().ImportPlayerHead(original.Game, head, "QaCareer", assets)), QaStageKind.FileTransfer);
        var preview = await c.Load(imported.Workspace.WorkingPath, imported.ImportedFacePath, MorphFaceGeometryMode.RelativeBake);
        using var scope = await c.Editor(preview, imported.Workspace.WorkingPath, player: true);
        original.SetEditingBaseline(scope.Editor.CreateDraft());
        var before = scope.Editor.CreateDraft();
        await c.Randomise(scope.Editor, purpose: "PCSAV randomise/save");
        var draft = scope.Editor.CreateDraft();
        QaContext.Require(!MorphFaceEditorStateComparer.Equals(before, draft), "PCSAV randomisation changed nothing.");
        var saved = await c.Save(draft, imported.Workspace.WorkingPath, "career-edited.pcc");
        var editedRon = c.Artifact("career-edited.ron");
        var edited = new MorphFacePackageContextService().CaptureHead(saved.PackagePath, saved.FaceInstancedPath);
        TseHeadMorphRon.Write(editedRon, edited);
        await c.Measure("Save edited PCSAV", () => Task.Run(() => original.SaveEditedHead(edited)), QaStageKind.FileTransfer);
        var reopened = PcsavHeadMorphWorkspace.Open(copy);
        QaContext.Require(reopened.HasHeadMorph && original.UnrelatedPrefix.SequenceEqual(reopened.UnrelatedPrefix) &&
            original.UnrelatedSuffix.SequenceEqual(reopened.UnrelatedSuffix), "PCSAV save changed unrelated career data.");
        var differences = QaHeadPayload.GeometryDifferences(edited.MorphData, reopened.Head.MorphData, compareNameOrder: false);
        QaContext.Require(differences.Count == 0, "PCSAV edited head differs: " + string.Join("; ", differences));
        if (original.HasHeadMorph)
        {
            QaContext.Require(QaHeadPayload.RetainsOrder(original.Head.MorphData.MorphFeatures.Select(value => value.Name),
                    reopened.Head.MorphData.MorphFeatures.Select(value => value.Name)) &&
                QaHeadPayload.RetainsOrder(original.Head.MorphData.FinalSkeleton.Select(value => value.BoneName),
                    reopened.Head.MorphData.FinalSkeleton.Select(value => value.BoneName)),
                "PCSAV save changed native feature/bone ordering.");
        }
        foreach (var value in edited.MaterialData.Scalars)
            QaContext.Require(reopened.Head.MaterialData.Scalars.Contains(value), "PCSAV lost edited scalar " + value.Name);
        foreach (var value in edited.MaterialData.Vectors)
            QaContext.Require(reopened.Head.MaterialData.Vectors.Contains(value), "PCSAV lost edited vector " + value.Name);
        foreach (var value in edited.MaterialData.Textures)
            QaContext.Require(reopened.Head.MaterialData.Textures.Any(actual => actual.Name == value.Name &&
                string.Equals(actual.TextureReference?.InstancedPath, value.TextureReference?.InstancedPath, StringComparison.OrdinalIgnoreCase)),
                "PCSAV lost edited texture " + value.Name);
        VerifyPlayerOutput(c, reopened.Head, original.HasHeadMorph ? original.Head : null);
        var outputRon = c.Artifact("career-reopened.ron");
        TseHeadMorphRon.Write(outputRon, reopened.Head);
        RequireHead(reopened.Head, TseHeadMorphRon.Read(outputRon), "PCSAV RON export");
        var canReplaceFullHead = true;
        try { new PlayerRuntimeAssetValidator().Verify(original.Game, reopened.Head); }
        catch (InvalidDataException error) when (UserFacingMessage.FromException(error)?.LocalizationKey == "loc_PlayerAsset_SeekFreeSourceRequired")
        {
            canReplaceFullHead = false;
            c.Current.Evidence["unavailableNativeReferences"] = error.Message;
        }
        // Exercise full-head RON replacement back into the career through the editor command.
        using var reader = new MorphFacePackageReader();
        var dialogs = new QaDialogs { ImportPath = outputRon, RonExportPath = c.Artifact("career-ui-export.ron") };
        using var vm = FileWorkflow(c, dialogs, reader);
        // Opening a career constructs the same staged Player workspace as a full RON import.
        await c.Measure("Reopen PCSAV in editor", () => vm.OpenDroppedFileAsync(copy), QaStageKind.FileTransfer);
        CheckVm(vm);
        var beforeReplacement = PackageFingerprint.Capture(copy);
        await c.Measure("Import full RON into PCSAV", () => Execute(vm.ImportMorphFaceRonCommand), QaStageKind.FileTransfer);
        if (!canReplaceFullHead)
        {
            if (string.IsNullOrWhiteSpace(vm.ErrorMessage))
                await c.Measure("Refuse unsafe full-head replacement", () => Execute(vm.SaveCommand), QaStageKind.FileTransfer);
            QaContext.Require(!string.IsNullOrWhiteSpace(vm.ErrorMessage) && beforeReplacement == PackageFingerprint.Capture(copy),
                "Unsafe full-head replacement was published or silently accepted.");
            // Reopen in an independent shell so an unsaved rejected replacement cannot affect export.
            using var exportReader = new MorphFacePackageReader();
            using var exportVm = FileWorkflow(c, dialogs, exportReader);
            await exportVm.OpenDroppedFileAsync(copy);
            CheckVm(exportVm);
            await Execute(exportVm.ExportMorphRonCommand);
            if (File.Exists(dialogs.RonExportPath))
            {
                CheckVm(exportVm);
                VerifyPlayerOutput(c, TseHeadMorphRon.Read(dialogs.RonExportPath));
            }
            else QaContext.Require(!string.IsNullOrWhiteSpace(exportVm.ErrorMessage), "Full RON export neither produced a safe file nor reported refusal.");
            QaContext.Require(beforeReplacement == PackageFingerprint.Capture(copy), "Full RON export changed its career source.");
            c.Current.Evidence["fullHeadRuntimeBoundary"] = "Unrelated PCSAV edits preserved unavailable native references; unsafe full replacement was refused. Full RON export must produce a safe editing projection or report refusal.";
            return;
        }
        CheckVm(vm);
        await c.Measure("Save PCSAV RON replacement", () => Execute(vm.SaveCommand), QaStageKind.FileTransfer);
        CheckVm(vm);
        RequireHead(reopened.Head, PcsavHeadMorphWorkspace.Open(copy).Head, "PCSAV full replacement");
        await c.Measure("Export full RON from PCSAV", () => Execute(vm.ExportMorphRonCommand), QaStageKind.FileTransfer);
        CheckVm(vm);
        VerifyPlayerOutput(c, TseHeadMorphRon.Read(dialogs.RonExportPath));
        c.Current.Evidence["pcsav"] = new { Fixture = input, original.Game, original.Sex, CreatedCustomHead = !original.HasHeadMorph,
            UnrelatedBytesPreserved = true, FullRonReplacement = true };
    }

    private static HeadMorphPayload ToHead(MorphFaceDocument draft) => new(draft.HairMeshReference?.InstancedPath ?? "None",
        draft.OtherMeshReferences.OfType<AssetIdentity>().Select(value => value.InstancedPath).ToArray(),
        new(draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods),
        new(draft.MaterialOverrides.Scalars, draft.MaterialOverrides.Vectors, draft.MaterialOverrides.Textures
            .Where(value => !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name)).ToArray()));

    private static void RequireHead(HeadMorphPayload expected, HeadMorphPayload actual, string purpose)
    {
        var differences = QaHeadPayload.GeometryDifferences(expected.MorphData, actual.MorphData).ToList();
        if (!string.Equals(expected.HairMesh, actual.HairMesh, StringComparison.OrdinalIgnoreCase)) differences.Add("hair identity differs");
        if (!expected.AccessoryMeshes.SequenceEqual(actual.AccessoryMeshes, StringComparer.OrdinalIgnoreCase)) differences.Add("accessory identities differ");
        if (MaterialSignature(expected.MaterialData) != MaterialSignature(actual.MaterialData))
            differences.Add("material payload differs: " + string.Join("; ", MaterialDifferences(expected.MaterialData, actual.MaterialData)));
        QaContext.Require(differences.Count == 0, purpose + ": " + string.Join("; ", differences));
    }

    private static async Task TseRonJourney(QaContext c, string fixtureName)
    {
        var input = Path.Combine(c.Root, "fixtures", "RON_Test", fixtureName + ".ron");
        c.TrackInput(input);
        QaContext.Require(TseHeadMorphRon.ReadProvenance(input) is null, "TSE fixture unexpectedly has MFE provenance.");
        var original = TseHeadMorphRon.Read(input);
        var seed = StandalonePlayerMorphImportService.ResolveInstalledSeed(c.Face.Game);
        c.TrackInput(seed);
        var registry = await c.Textures.ReadAsync(c.Face.Game);
        if (!registry.IsAvailable) throw new QaPrerequisiteException("TSE RON journey requires a Ready asset database.");
        var assets = await c.Measure("Resolve external TSE source donors", () => Task.Run(() =>
            StandalonePlayerAssetCatalog.ForRon(c.Face.Game, input, registry.Candidates,
                referencePackagePath: seed, attachmentMeshes: registry.AttachmentMeshes)));
        foreach (var donor in assets.Textures.Concat(assets.SkeletalMeshes)
                     .Select(value => value.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase))
            c.TrackInput(donor);
        c.Current.Evidence["tseFixture"] = input;
        c.Current.Evidence["tseSourceDonors"] = new { Seed = seed, assets.Textures, assets.SkeletalMeshes };
        using var reader = new MorphFacePackageReader();
        var dialogs = new QaDialogs { ImportGame = c.Face.Game, RonExportPath = c.Artifact("tse-readback.ron") };
        using var vm = FileWorkflow(c, dialogs, reader);
        await c.Measure("Open external TSE RON", () => vm.OpenDroppedFileAsync(input), QaStageKind.FileTransfer);
        c.Current.Evidence["importMessages"] = dialogs.Messages;
        CheckVm(vm);
        QaContext.Require(vm.Editor is not null, "TSE RON did not create an editor.");
        QaContext.Require(!vm.CanCommitFace, "TSE RON exposed a commit-capable installed package workspace.");
        var imported = vm.Editor!.CreateDraft();
        var raw = ReadPackageHead(imported.Source.PackagePath, vm.SelectedFace!.InstancedPath);
        var differences = QaHeadPayload.GeometryDifferences(original.MorphData, raw.MorphData);
        QaContext.Require(differences.Count == 0, "TSE authored payload changed: " + string.Join("; ", differences));
        QaContext.Require(string.Equals(string.IsNullOrWhiteSpace(original.HairMesh) ? "None" : original.HairMesh,
                raw.HairMesh, StringComparison.OrdinalIgnoreCase) &&
            original.AccessoryMeshes.SequenceEqual(raw.AccessoryMeshes, StringComparer.OrdinalIgnoreCase),
            "TSE import changed the original hair or accessory identities.");
        QaContext.Require(vm.Editor.CanEditMorphFeatures && vm.Editor.CanEditBones,
            "TSE canonical Player fixture lost its safe morph or bone controls.");
        QaContext.Require(vm.Editor.Evaluation.Geometry.Positions.SequenceEqual(original.MorphData.BakedLods[0]),
            "Opening the TSE fixture changed its authored LOD0 bake.");
        foreach (var (authoredName, targetName) in new[]
                 { ("Jaw_Width", "jaw_wide"), ("mouthShape_thick", "mouthShape_fatLips") }
                 .Where(alias => original.MorphData.MorphFeatures.Any(feature =>
                     feature.Name.Equals(alias.Item1, StringComparison.OrdinalIgnoreCase))))
        {
            var resolved = vm.Editor.Evaluation.Resolution.Features.Single(feature =>
                feature.Feature.Name.Equals(authoredName, StringComparison.OrdinalIgnoreCase));
            QaContext.Require(resolved.Kind == MorphFeatureResolutionKind.AliasTarget &&
                string.Equals(resolved.Target?.Source.InstancedPath.Split('.').Last(), targetName,
                    StringComparison.OrdinalIgnoreCase),
                $"TSE creator feature '{authoredName}' did not resolve to canonical target '{targetName}'.");
        }
        var materials = new MorphFacePackageContextService().CaptureMaterialData(
            imported.Source.PackagePath, vm.SelectedFace!.InstancedPath);
        const string malformedScalp = "BIOG_HMM_HIR_PRO_R.PROCustomFade02.HMM_HIR_PROCustomFade01_Mask";
        if (fixtureName.Equals("LE1-2_Customizable_Sheploo", StringComparison.OrdinalIgnoreCase))
        {
            QaContext.Require(original.MaterialData.Textures.Any(value =>
                    string.Equals(value.TextureReference?.InstancedPath, malformedScalp, StringComparison.OrdinalIgnoreCase)),
                "The Sheploo fixture no longer contains its known malformed scalp reference.");
            QaContext.Require(materials.Textures.All(value =>
                    !string.Equals(value.TextureReference?.InstancedPath, malformedScalp, StringComparison.OrdinalIgnoreCase)) &&
                dialogs.Messages.Any(message => message.Contains(malformedScalp, StringComparison.OrdinalIgnoreCase)),
                "TSE Sheploo import did not omit and name its malformed scalp reference.");
        }
        var expectedMaterials = original.MaterialData with { Textures = original.MaterialData.Textures.Where(value =>
            !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name) &&
            !string.Equals(value.TextureReference?.InstancedPath, malformedScalp, StringComparison.OrdinalIgnoreCase)).ToArray() };
        var importedMaterials = materials with { Textures = materials.Textures.Where(value =>
            !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name)).ToArray() };
        QaContext.Require(MaterialSignature(expectedMaterials) == MaterialSignature(importedMaterials),
            "TSE import changed the original BMO material payload: " +
            string.Join("; ", MaterialDifferences(expectedMaterials, importedMaterials)));
        if (fixtureName.Contains("ashleyhair", StringComparison.OrdinalIgnoreCase))
        {
            QaContext.Require(original.MaterialData.Textures.Any(value =>
                    value.Name.Equals("HAIR_Mask", StringComparison.OrdinalIgnoreCase) && value.TextureReference is null) &&
                dialogs.Messages.All(message => !message.Contains("HAIR_Mask", StringComparison.OrdinalIgnoreCase)),
                "TSE Ashley hair import treated its empty HAIR_Mask sentinel as a malformed texture.");
            if (c.Face.Game == MorphFaceGame.LE2)
            {
                using var package = MEPackageHandler.OpenMEPackage(imported.Source.PackagePath, forceLoadFromDisk: true);
                var hair = package.FindExport(CanonicalAttachmentMaterialPolicy.AshleyMeshPath, "SkeletalMesh")
                    ?? throw new InvalidDataException("TSE Ashley import lost its canonical hair mesh.");
                var slots = hair.GetBinaryData<SkeletalMesh>().Materials;
                QaContext.Require(slots.Length == 1 && package.GetEntry(slots[0]) is { ClassName: "MaterialInstanceConstant" } mic &&
                    mic.InstancedFullPath.Equals(CanonicalAttachmentMaterialPolicy.AshleyMaterialPath, StringComparison.OrdinalIgnoreCase),
                    "TSE LE2 Ashley hair did not retain its single canonical material instance.");
            }
        }
        await c.Measure("Export imported TSE head", () => Execute(vm.ExportMorphRonCommand), QaStageKind.FileTransfer);
        CheckVm(vm);
        var exported = TseHeadMorphRon.Read(dialogs.RonExportPath);
        VerifyPlayerOutput(c, exported);
        RequireHead(raw, exported, "TSE import/export");
        var saved = await c.Save(imported, vm.Editor!.CreateDraft().Source.PackagePath, "tse-roundtrip.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath, MorphFaceGeometryMode.RelativeBake);
        AssertGeometry(imported, reopened.Loaded.Document);
        AssertMaterials(imported, reopened.Loaded.Document);
        await c.Render(reopened.Scene, "tse-reopened.png");
    }

    private static async Task MissingRonAssets(QaContext c, string kind)
    {
        var source = c.Source();
        var ron = c.Artifact("missing-assets.ron");
        new MorphFacePackageContextService().ExportRon(source.PackagePath, source.FacePath, ron, Provenance(c.Face));
        var head = TseHeadMorphRon.Read(ron);
        const string missing = "BIOG_QA_MISSING.Asset_That_Does_Not_Exist";
        var texture = head.MaterialData.Textures.First();
        head = kind switch
        {
            "hair" => head with { HairMesh = missing },
            "accessory" => head with { AccessoryMeshes = [missing] },
            "texture" => head with { MaterialData = head.MaterialData with
                { Textures = head.MaterialData.Textures.Select(value => value.Name == texture.Name
                    ? value with { TextureReference = new("missing.pcc", missing, 0, "Texture2D") } : value).ToArray() } },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        TseHeadMorphRon.Write(ron, head, Provenance(c.Face));
        using var reader = new MorphFacePackageReader();
        var dialogs = new QaDialogs { ImportGame = c.Face.Game,
            RonDestination = new(RonImportDestination.NpcFace, c.Face.Family) };
        using var vm = FileWorkflow(c, dialogs, reader);
        await c.Measure("Import NPC RON with missing optional assets", () => vm.OpenDroppedFileAsync(ron), QaStageKind.FileTransfer);
        CheckVm(vm);
        QaContext.Require(vm.Editor is not null, "Missing optional RON assets prevented opening.");
        var draft = vm.Editor!.CreateDraft();
        QaContext.Require(draft.HairMeshReference?.InstancedPath != missing &&
            draft.OtherMeshReferences.All(value => value?.InstancedPath != missing) &&
            draft.MaterialOverrides.Textures.All(value => value.TextureReference?.InstancedPath != missing),
            "Missing RON assets survived as invalid references.");
        QaContext.Require(dialogs.Messages.Count > 0, "Skipped missing RON assets were not reported.");
        var saved = await c.Save(draft, vm.Editor!.CreateDraft().Source.PackagePath, "missing-assets-skipped.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath, MorphFaceGeometryMode.RelativeBake);
        AssertGeometry(draft, reopened.Loaded.Document);
        AssertMaterials(draft, reopened.Loaded.Document);
        c.Current.Evidence["skippedAssetMessages"] = dialogs.Messages;
    }

    private static async Task DonorSweep(QaContext c)
    {
        var package = c.Face.Package(c);
        var eligible = MorphFaceReferenceInspector.Inspect(package).Where(value =>
            c.Profiles.Find(value.Game, value.FacePath, value.BaseHeadPath)?.Key ==
                $"{c.Face.Game.ToString().ToLowerInvariant()}-{c.Face.ProfileSuffix}" &&
            !QaCoverageSelection.KnownUnusedCorpusFacePaths.Contains(value.FacePath)).OrderBy(value => value.FacePath).ToArray();
        QaContext.Require(eligible.Length > 0, "Exhaustive donor sweep found no eligible donors.");
        var outcomes = new List<object>();
        var failures = new List<string>();
        c.Current.Evidence["donorSweep"] = outcomes;
        foreach (var donor in eligible)
        {
            try
            {
                var preview = await c.Load(package, donor.FacePath);
                var saved = await c.Save(preview.Loaded.Document, package, "donor.pcc");
                var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath);
                AssertGeometry(preview.Loaded.Document, reopened.Loaded.Document);
                AssertMaterials(preview.Loaded.Document, reopened.Loaded.Document);
                outcomes.Add(new { donor.FacePath, Status = "passed", Output = saved.PackagePath });
            }
            catch (Exception error)
            {
                failures.Add(donor.FacePath + ": " + error);
                outcomes.Add(new { donor.FacePath, Status = "failed", Error = error.ToString() });
            }
        }
        c.Current.Evidence["donorCoverage"] = new { Eligible = eligible.Length, Attempted = outcomes.Count, Failed = failures.Count,
            Scope = "Every eligible donor: fresh load, unchanged export, fresh reopen, geometry/material readback. No claim of exhaustive random combinations." };
        QaContext.Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
