using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    /// <summary>Runs the real manual ShortAfro texture-switch workflow for one NPC sex.</summary>
    internal static async Task NativeCcScalpSwitch(QaContext c)
    {
        QaContext.Require(c.Face.Game == MorphFaceGame.LE3 &&
                          (c.Face.Family is "HMM" or "HMF") && !c.Face.IsPlayer,
            "Native CC scalp switch requires an LE3 HMM or HMF NPC fixture.");
        var game = MorphFaceGame.LE3;
        var packagePath = c.CorpusPackage(game);
        var references = c.MeasureSync("Inspect LE3 human NPC fixtures", () =>
            MorphFaceReferenceInspector.Inspect(packagePath), QaStageKind.Load);
        var family = c.Face.Family;
        var female = family == "HMF";
        var attemptedFaces = new List<string>();
        MorphFacePreviewLoadResult? preview = null;
        var facePath = string.Empty;
        ResolvedHeadMaterial? scalpTarget = null;
        foreach (var candidateFace in references.Select(value => value.FacePath)
                     .Where(value => value.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            attemptedFaces.Add(candidateFace);
            var candidate = await c.Load(packagePath, candidateFace);
            var candidateSlots = candidate.Loaded.BaseHead.RenderData?.MaterialSlots ?? [];
            var candidateScalp = candidate.Loaded.Materials.Materials.Values.SingleOrDefault(material =>
                material.Family == HeadMaterialFamily.Scalp && candidateSlots.Any(slot =>
                    slot?.InstancedPath.Equals(material.Source.InstancedPath,
                        StringComparison.OrdinalIgnoreCase) == true));
            if (candidateScalp is null || candidateScalp.PreviewModel == HumanMaterialPreviewModel.CcScalp)
                continue;
            preview = candidate;
            facePath = candidateFace;
            scalpTarget = candidateScalp;
            break;
        }
        if (preview is null || scalpTarget is null)
            throw new QaPrerequisiteException($"No {family} LE3 NPC fixture with a non-CC scalp graph is available.");
        c.Current.Evidence[$"ccScalp/{family}/fixtureSearch"] = attemptedFaces;
        using var scope = await c.Editor(preview, packagePath, player: false);
        var editor = scope.Editor;
        var original = preview.Loaded;
        var originalHead = original.BaseHead;
        var originalHeadPath = original.Document.BaseHeadReference?.InstancedPath
            ?? throw new InvalidDataException($"{facePath} has no base-head identity.");
        var originalHeadBytes = ReadExactMeshBytes(original.BaseHead.Source);
        var originalSlots = originalHead.RenderData?.MaterialSlots
            ?? throw new InvalidDataException($"{facePath} has no base-head material slots.");
        var scalpSlot = originalSlots.Select((slot, index) => (slot, index))
            .Single(value => value.slot?.InstancedPath.Equals(scalpTarget.Source.InstancedPath,
                StringComparison.OrdinalIgnoreCase) == true).index;

        var registry = await c.Textures.ReadAsync(game);
        if (!registry.IsAvailable)
            throw new QaPrerequisiteException("The LE3 asset database must be Ready to resolve the ShortAfro mesh and textures.");
        var sex = female ? PlayerHairSex.Female : PlayerHairSex.Male;
        var styleId = female ? "hmf-short-afro" : "hmm-short-afro";
        var style = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE3, sex,
                registry.Candidates, registry.AttachmentMeshes, preferNativeNpcMeshVariants: true)
            .SingleOrDefault(value => value.Id == styleId)
            ?? throw new QaPrerequisiteException($"LE3 {styleId} is not available in the official asset catalogue.");
        var mesh = style.MeshVariants.OrderBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new QaPrerequisiteException($"LE3 {styleId} has no resolved ShortAfro mesh.");
        var meshOption = editor.HairMesh.Options.FirstOrDefault(value =>
            value.Identity is { } identity && identity.InstancedPath.Equals(mesh.InstancedPath,
                StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(identity.PackagePath).Equals(Path.GetFullPath(mesh.PackagePath),
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The selected {family} ShortAfro mesh is absent from the editor picker.");

            // Selecting the mesh is its own user action. The subsequent texture confirmation
            // must record the scalp graph and paired maps as one undoable edit.
            editor.HairMesh.Selected = meshOption;
            var beforeSwitch = editor.CreateDraft();
            var prepared = await editor.PrepareManualHairScalpPairAsync(mesh)
                ?? throw new InvalidDataException($"The editor did not prepare the {family} ShortAfro scalp pair.");
            QaContext.Require(prepared.RequiresCcScalpMaterial && prepared.FemaleScalp == female,
                $"The {family} ShortAfro workflow did not request its sex-specific CC scalp donor.");
            using var donorReader = new MorphFacePackageReader();
            var referencesService = new PackageReferenceService(donorReader, c.Textures);
            prepared = prepared with
            {
                ScalpMaterial = await referencesService.LoadLe3CcScalpMaterialAsync(female)
            };
            var expectedDonor = Le3CcScalpMaterialPolicy.CanonicalDonorPath(female);
            QaContext.Require(prepared.ScalpMaterial.Source.InstancedPath.Equals(expectedDonor,
                    StringComparison.OrdinalIgnoreCase),
                $"The {family} ShortAfro workflow selected the wrong CC scalp donor.");

            editor.ApplyManualHairScalpPair(prepared);
            var selected = editor.CreateDraft();
            QaContext.Require(selected.ScalpMaterialSelection is { } selection &&
                              selection.TargetMaterial.InstancedPath.Equals(scalpTarget.Source.InstancedPath,
                                  StringComparison.OrdinalIgnoreCase) &&
                              selection.DonorMaterial.InstancedPath.Equals(expectedDonor,
                                  StringComparison.OrdinalIgnoreCase),
                $"The {family} scalp graph change was not recorded against the selected face material.");
            QaContext.Require(selected.HairMeshReference?.InstancedPath.Equals(mesh.InstancedPath,
                    StringComparison.OrdinalIgnoreCase) == true,
                $"The {family} ShortAfro mesh selection was not retained.");
            var scalpMaps = selected.MaterialOverrides.Textures.Where(value =>
                    value.Name.Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase) ||
                    value.Name.Equals("HED_Scalp_Norm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            QaContext.Require(scalpMaps.Length == 2,
                $"The {family} ShortAfro action did not apply both required scalp maps.");

            editor.UndoCommand.Execute(null);
            QaContext.Require(MorphFaceEditorStateComparer.Equals(beforeSwitch, editor.CreateDraft()),
                $"One undo did not restore the {family} pre-switch graph and texture state.");
            editor.RedoCommand.Execute(null);
            QaContext.Require(MorphFaceEditorStateComparer.Equals(selected, editor.CreateDraft()),
                $"One redo did not restore the {family} selected graph and texture state.");

            var existingCopy = c.Artifact($"{family.ToLowerInvariant()}-cc-scalp-existing.pcc");
            File.Copy(packagePath, existingCopy);
            var baseline = c.MeasureSync("Capture copied PCC integrity baseline", () =>
            {
                using var package = MEPackageHandler.OpenMEPackage(existingCopy, forceLoadFromDisk: true);
                return PackageIntegrity.CaptureIssues(package);
            }, QaStageKind.Diagnostic);
            var existingKey = Path.GetFullPath(existingCopy);
            c.ExistingPackageBaselines[existingKey] = baseline;
            c.Current.Evidence["existingPackagePath"] = existingKey;
            c.Current.Evidence["existingPackageSource"] = packagePath;
            c.Current.Evidence["existingPackageBaseline"] = baseline;
            c.ExistingPackageSources[existingKey] = packagePath;
            c.Current.Evidence[$"ccScalp/{family}/existingPackageBaseline"] = baseline;
            var existingDraft = editor.CreateDraft() with
            {
                Source = editor.CreateDraft().Source with { PackagePath = existingCopy },
                SourceFingerprint = PackageFingerprint.Capture(existingCopy)
            };
            var existingSave = await c.Measure("Save existing PCC scalp graph", () => Task.Run(() =>
                new MorphFacePackageWriter().SaveExisting(existingDraft)), QaStageKind.FileTransfer);
            VerifyIntegrity(existingCopy, baseline);
            var existingReopened = await c.Load(existingCopy, existingSave.FaceInstancedPath);
            VerifySavedCcScalp(c, existingCopy, existingSave.FaceInstancedPath, originalHeadPath,
                originalHead, originalHeadBytes, selected, scalpSlot, expectedDonor, mesh.InstancedPath,
                existingReopened, preserveUnusedOldHead: true, baseline);
            var savedGraphBaseline = c.MeasureSync("Capture saved graph integrity baseline", () =>
            {
                using var package = MEPackageHandler.OpenMEPackage(existingCopy, forceLoadFromDisk: true);
                return PackageIntegrity.CaptureIssues(package);
            }, QaStageKind.Diagnostic);
            _ = await c.Measure("No-op resave after reopening CC graph", () => Task.Run(() =>
                new MorphFacePackageWriter().SaveExisting(existingReopened.Loaded.Document)),
                QaStageKind.FileTransfer);
            existingReopened = await c.Load(existingCopy, existingSave.FaceInstancedPath);
            VerifySavedCcScalp(c, existingCopy, existingSave.FaceInstancedPath, originalHeadPath,
                originalHead, originalHeadBytes, selected, scalpSlot, expectedDonor, mesh.InstancedPath,
                existingReopened, preserveUnusedOldHead: true, savedGraphBaseline);

            var exportSave = await c.Save(editor.CreateDraft(), packagePath,
                $"{family.ToLowerInvariant()}-cc-scalp-export.pcc");
            var exportReopened = await c.Load(exportSave.PackagePath, exportSave.FaceInstancedPath);
            VerifySavedCcScalp(c, exportSave.PackagePath, exportSave.FaceInstancedPath, originalHeadPath,
                originalHead, originalHeadBytes, selected, scalpSlot, expectedDonor, mesh.InstancedPath,
                exportReopened, preserveUnusedOldHead: false, integrityBaseline: null);

        c.Current.Evidence[$"ccScalp/{family}"] = new
        {
            Face = facePath,
            Mesh = mesh,
            Donor = expectedDonor,
            ScalpSlot = scalpSlot,
            RequiredMaps = scalpMaps.Select(value => value.Name).OrderBy(value => value).ToArray(),
            ExistingSave = existingCopy,
            ExistingReopenHead = existingReopened.Loaded.BaseHead.Source,
            FreshExport = exportSave.PackagePath,
            ExportReopenHead = exportReopened.Loaded.BaseHead.Source
        };
    }

    /// <summary>Checks an official LE3 Player seed, its prepared copy, and a visible Buzzcut edit.</summary>
    internal static async Task PlayerCcScalpDefault(QaContext c)
    {
        QaContext.Require(c.Face.Game == MorphFaceGame.LE3 && c.Face.IsPlayer,
            "Player CC scalp default case requires an LE3 Player fixture.");
        var female = c.Face.PlayerSex == StandalonePlayerSex.Female;
        var expectedDonor = Le3CcScalpMaterialPolicy.CanonicalDonorPath(female);
        var seedPath = StandalonePlayerMorphImportService.ResolveInstalledSeed(MorphFaceGame.LE3);
        c.TrackInput(seedPath);
        var templateFacePath = c.Face.Selector;
        var official = await c.Load(seedPath, templateFacePath, MorphFaceGeometryMode.RelativeBake);
        var sourceScalp = FindCcScalp(official, expectedDonor, $"Official {c.Face.PlayerSex}");
        var nativeUnusedAlpha = sourceScalp.Scalars.TryGetValue("HAIR_Mask_Alpha_Scalar", out var alpha)
            ? alpha
            : throw new InvalidDataException("The official CC scalp MIC has no authored Hair Mask Alpha value.");
        using var editorScope = await c.Editor(official, seedPath, player: true);
        var editor = editorScope.Editor;
        var buzzcut = editor.Material.Scalars.SingleOrDefault(value =>
            value.Name.Equals("HED_Scalp_Buzzcut_Alpha_Scalar", StringComparison.OrdinalIgnoreCase));
        QaContext.Require(buzzcut is not null,
            "The official CC scalp template did not expose its active Buzzcut Alpha control.");
        QaContext.Require(editor.Material.Scalars.All(value =>
                !value.Name.Equals("HAIR_Mask_Alpha_Scalar", StringComparison.OrdinalIgnoreCase)),
            "The unused Hair Mask Alpha scalar appeared as an editable control.");
        var oldBuzzcut = buzzcut!.Value;
        var newBuzzcut = oldBuzzcut < buzzcut.Maximum ? oldBuzzcut + Math.Min(0.125f, buzzcut.Maximum - oldBuzzcut)
            : oldBuzzcut - Math.Min(0.125f, oldBuzzcut - buzzcut.Minimum);
        QaContext.Require(newBuzzcut != oldBuzzcut, "The active Buzzcut Alpha control has no editable range.");
        buzzcut.Value = newBuzzcut;
        var edited = editor.CreateDraft();
        QaContext.Require(edited.MaterialOverrides.Scalars.Any(value =>
                value.Name.Equals("HED_Scalp_Buzzcut_Alpha_Scalar", StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(value.Value - newBuzzcut) < 0.00001f),
            "Editing Buzzcut Alpha did not create a saved face override.");
        QaContext.Require(!edited.MaterialOverrides.Scalars.Any(value =>
                value.Name.Equals("HAIR_Mask_Alpha_Scalar", StringComparison.OrdinalIgnoreCase)),
            "The hidden Hair Mask Alpha value was turned into an authored face override.");

        var editedSave = await c.Save(edited, seedPath,
            $"{(female ? "hmf" : "hmm")}-cc-scalp-buzzcut.pcc");
        var editedReopened = await c.Load(editedSave.PackagePath, editedSave.FaceInstancedPath,
            MorphFaceGeometryMode.RelativeBake);
        var savedBuzzcut = editedReopened.Loaded.Document.MaterialOverrides.Scalars.SingleOrDefault(value =>
            value.Name.Equals("HED_Scalp_Buzzcut_Alpha_Scalar", StringComparison.OrdinalIgnoreCase));
        QaContext.Require(savedBuzzcut is not null && Math.Abs(savedBuzzcut.Value - newBuzzcut) < 0.00001f,
            "The Buzzcut Alpha edit did not survive PCC save and reopen.");
        var reopenedScalp = FindCcScalp(editedReopened, expectedDonor, "Reopened Player edit");
        QaContext.Require(reopenedScalp.Scalars.TryGetValue("HAIR_Mask_Alpha_Scalar", out var savedUnusedAlpha) &&
                          Math.Abs(savedUnusedAlpha - nativeUnusedAlpha) < 0.00001f,
            "Saving Buzzcut Alpha changed the hidden authored Hair Mask Alpha value.");

        var preparedSource = c.Source();
        var prepared = await c.Load(preparedSource.PackagePath, preparedSource.FacePath,
            MorphFaceGeometryMode.RelativeBake);
        _ = FindCcScalp(prepared, expectedDonor, "Prepared Player fixture");
        c.Current.Evidence["playerCcScalpDefault"] = new
        {
            c.Face.PlayerSex,
            OfficialSeedFace = templateFacePath,
            PreparedFace = preparedSource.FacePath,
            Donor = expectedDonor,
            BuzzcutBefore = oldBuzzcut,
            BuzzcutAfter = newBuzzcut,
            HairMaskAlphaPreserved = nativeUnusedAlpha
        };
    }

    private static ResolvedHeadMaterial FindCcScalp(
        MorphFacePreviewLoadResult preview,
        string expectedDonor,
        string description)
    {
        var scalp = preview.Loaded.Materials.Materials.Values.SingleOrDefault(material =>
            material.Family == HeadMaterialFamily.Scalp && material.Source.InstancedPath.Equals(
                expectedDonor, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{description} does not resolve exact CC donor '{expectedDonor}'.");
        QaContext.Require(scalp.PreviewModel == HumanMaterialPreviewModel.CcScalp &&
                          scalp.MasterMaterialName.Equals("HMM_HED_PRO_MASTER_SCALP_MAT_CC",
                              StringComparison.OrdinalIgnoreCase),
            $"{description} did not resolve through the CC scalp master graph.");
        return scalp;
    }

    /// <summary>Loads the official Kaidan hair attachment and checks its native preview-only boundary.</summary>
    internal static async Task KaidanPreviewBoundary(QaContext c)
    {
        QaContext.Require(c.Face.Game == MorphFaceGame.LE3 && !c.Face.IsPlayer &&
                          c.Face.Family == "HMM",
            "Kaidan preview boundary case requires an LE3 HMM NPC fixture.");
        var source = c.Source();
        var fixture = await c.Load(source.PackagePath, source.FacePath);
        const string packageName = "BIOG_HMM_HIR_PRO_R.pcc";
        const string meshPath = "BIOG_HMM_HIR_PRO_R.Hair_Kaiden.HMM_HIR_Kai_MDL";
        var packagePath = InstalledGameAssetResolver.ResolvePackage(MorphFaceGame.LE3, packageName);
        c.TrackInput(packagePath);
        using var reader = new MorphFacePackageReader();
        var attachment = c.MeasureSync("Load official Kaidan hair attachment", () =>
            reader.LoadDetachedAttachment(packagePath, meshPath), QaStageKind.Load);
        QaContext.Require(attachment.Materials.Materials.Count > 0,
            "The official Kaidan hair mesh has no material evidence.");
        foreach (var material in attachment.Materials.Materials.Values)
        {
            QaContext.Require(material.PreviewModel == HumanMaterialPreviewModel.Kaiden,
                $"Kaidan material '{material.Source.InstancedPath}' lost its native preview model.");
            QaContext.Require(!material.ContributesFaceControls,
                $"Kaidan material '{material.Source.InstancedPath}' leaked into editable face controls.");
        }

        var catalog = new CustomMaterialTemplateCatalogService(reader, c.Store).Load(MorphFaceGame.LE3);
        QaContext.Require(catalog.Options.All(option =>
                !option.Template.MasterMaterialName.Contains("Kaiden", StringComparison.OrdinalIgnoreCase) &&
                !option.Id.Contains("kaiden", StringComparison.OrdinalIgnoreCase)),
            "The preview-only Kaidan scalp graph appeared as an authoring material choice.");
        var loaded = fixture.Loaded with
        {
            Document = fixture.Loaded.Document with
            {
                BaseHeadReference = attachment.Source,
                HairMeshReference = null
            },
            BaseHead = attachment.Mesh,
            HairMesh = null,
            Materials = attachment.Materials,
            UsesCustomBaseMesh = true
        };
        await c.Render(c.Scenes.Create(loaded), "kaidan-native-hair-preview.png");
        c.Current.Evidence["kaidanPreviewBoundary"] = new
        {
            Mesh = attachment.Source,
            Materials = attachment.Materials.Materials.Values.Select(material => new
            {
                material.Source,
                material.MasterMaterialName,
                material.PreviewModel,
                material.ContributesFaceControls,
                material.IsPreviewOnlyAttachment,
                material.SupportedScalars,
                material.SupportedVectors
            }),
            AuthoringChoiceCount = catalog.Options.Count
        };
    }

    private static void VerifySavedCcScalp(
        QaContext c,
        string packagePath,
        string facePath,
        string originalHeadPath,
        SkeletalMeshAsset originalHead,
        byte[] originalHeadBytes,
        MorphFaceDocument expectedDocument,
        int scalpSlot,
        string expectedDonor,
        string expectedHairMesh,
        MorphFacePreviewLoadResult reopened,
        bool preserveUnusedOldHead,
        IReadOnlySet<string>? integrityBaseline)
    {
        QaContext.Require(reopened.Loaded.Document.HairMeshReference?.InstancedPath.Equals(
                expectedHairMesh, StringComparison.OrdinalIgnoreCase) == true,
            "The saved face did not retain the selected ShortAfro mesh identity.");
        QaContext.Require(!reopened.Loaded.BaseHead.Source.InstancedPath.Equals(originalHeadPath,
                StringComparison.OrdinalIgnoreCase),
            "The saved face did not receive a face-owned head clone for its scalp graph.");
        var slots = reopened.Loaded.BaseHead.RenderData?.MaterialSlots
            ?? throw new InvalidDataException("The saved face-owned head has no material slots.");
        QaContext.Require((uint)scalpSlot < (uint)slots.Count &&
                          slots[scalpSlot]?.InstancedPath.Equals(expectedDonor,
                              StringComparison.OrdinalIgnoreCase) == true,
            $"Saved scalp slot {scalpSlot} does not reference exact donor '{expectedDonor}'.");
        var ccMaterial = reopened.Loaded.Materials.Materials.Values.SingleOrDefault(material =>
            material.Source.InstancedPath.Equals(expectedDonor, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Saved face did not reopen through CC donor '{expectedDonor}'.");
        QaContext.Require(ccMaterial.PreviewModel == HumanMaterialPreviewModel.CcScalp &&
                          ccMaterial.MasterMaterialName.Equals("HMM_HED_PRO_MASTER_SCALP_MAT_CC",
                              StringComparison.OrdinalIgnoreCase),
            "The saved scalp MIC lost its CC master ancestry after reopening.");
        VerifyFaceOverrides(expectedDocument.MaterialOverrides, reopened.Loaded.Document.MaterialOverrides);

        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        if (integrityBaseline is null) PackageIntegrity.Verify(package);
        else PackageIntegrity.Verify(package, integrityBaseline);
        var face = package.FindExport(facePath, "BioMorphFace")
            ?? throw new InvalidDataException("The saved face export is missing.");
        var head = face.GetProperty<ObjectProperty>("m_oBaseHead")
            ?.ResolveToEntry(package) as ExportEntry
            ?? throw new InvalidDataException("The saved face lost its cloned base head.");
        var savedHeadParent = head.Parent
            ?? throw new InvalidDataException("The face-owned head clone lost its native package ancestry.");
        using (var sourcePackage = MEPackageHandler.OpenMEPackage(originalHead.Source.PackagePath,
                   forceLoadFromDisk: true))
        {
            var sourceHead = PackageIntegrity.FindExactEntry(sourcePackage, originalHeadPath, "SkeletalMesh")
                ?? throw new InvalidDataException("The original base-head identity is missing from its source package.");
            QaContext.Require(sourceHead.Parent is not null &&
                              sourceHead.Parent.InstancedFullPath.Equals(savedHeadParent.InstancedFullPath,
                                  StringComparison.OrdinalIgnoreCase),
                "The face-owned head clone changed its native package/group ancestry.");
        }
        var mesh = head.GetBinaryData<SkeletalMesh>();
        var originalSlots = originalHead.RenderData?.MaterialSlots
            ?? throw new InvalidDataException("The original head has no material slots.");
        QaContext.Require(mesh.Materials.Length == originalSlots.Count && mesh.Materials.Length > scalpSlot,
            "The saved head changed its material slot count.");
        var donorSlotCount = mesh.Materials.Select(package.GetEntry)
            .Count(entry => entry is not null && EntryPath(entry).Equals(expectedDonor,
                StringComparison.OrdinalIgnoreCase));
        QaContext.Require(donorSlotCount == 1,
            "The saved head does not contain exactly one reference to the selected CC donor.");

        var sourceHeadEntry = PackageIntegrity.FindExactEntry(package, originalHeadPath, "SkeletalMesh");
        if (preserveUnusedOldHead)
            QaContext.Require(sourceHeadEntry is not null,
                "Existing PCC save removed the original shared base-head identity.");
        else
            QaContext.Require(sourceHeadEntry is null,
                "Fresh export retained the replaced original base-head as an unused asset.");
        if (sourceHeadEntry is ExportEntry oldExport)
            QaContext.Require(oldExport.Data.ToArray().SequenceEqual(originalHeadBytes),
                "Existing PCC save modified the shared source head while replacing one scalp slot.");

        var otherSlots = mesh.Materials
            .Select((materialIndex, slotIndex) => (MaterialIndex: materialIndex, SlotIndex: slotIndex))
            .Where(value => value.SlotIndex != scalpSlot).ToArray();
        foreach (var (materialIndex, slotIndex) in otherSlots)
        {
            var reopenedPath = package.GetEntry(materialIndex) is { } entry ? EntryPath(entry) : null;
            var expectedPath = originalSlots[slotIndex]?.InstancedPath;
            QaContext.Require(string.Equals(reopenedPath, expectedPath, StringComparison.OrdinalIgnoreCase),
                $"Unrelated head material slot {slotIndex} changed from '{expectedPath}' to '{reopenedPath}'.");
        }

        VerifyHeadGeometry(originalHead, reopened.Loaded.BaseHead);
        c.Current.Evidence[$"saveVerification/{Path.GetFileName(packagePath)}"] = new
        {
            Face = facePath,
            ClonedHead = head.InstancedFullPath,
            Donor = expectedDonor,
            HairMesh = reopened.Loaded.Document.HairMeshReference,
            preserveUnusedOldHead,
            Integrity = "Passed"
        };
    }

    private static void VerifyIntegrity(string path, IReadOnlySet<string> baseline)
    {
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        PackageIntegrity.Verify(package, baseline);
    }

    private static byte[] ReadExactMeshBytes(AssetIdentity identity)
    {
        using var package = MEPackageHandler.OpenMEPackage(identity.PackagePath, forceLoadFromDisk: true);
        var export = identity.UIndex > 0 && package.IsUExport(identity.UIndex)
            ? package.GetUExport(identity.UIndex)
            : package.FindExport(identity.InstancedPath, "SkeletalMesh");
        if (export is null || !PccAssetPathPolicy.FromExport(export).Equals(identity.InstancedPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Could not read exact source head '{identity.InstancedPath}'.");
        return export.Data.ToArray();
    }

    private static void VerifyHeadGeometry(SkeletalMeshAsset original, SkeletalMeshAsset reopened)
    {
        var sourceLods = original.AvailableLods.OrderBy(lod => lod.LodIndex).ToArray();
        var savedLods = reopened.AvailableLods.OrderBy(lod => lod.LodIndex).ToArray();
        QaContext.Require(sourceLods.Length == savedLods.Length, "The saved head changed its LOD count.");
        for (var index = 0; index < sourceLods.Length; index++)
        {
            var source = sourceLods[index];
            var saved = savedLods[index];
            QaContext.Require(source.Positions.SequenceEqual(saved.Positions) &&
                              source.Normals.SequenceEqual(saved.Normals),
                $"Saved head LOD {source.LodIndex} changed authored vertices or normals.");
            QaContext.Require(source.RenderData.Indices.SequenceEqual(saved.RenderData.Indices) &&
                              source.RenderData.Tangents.SequenceEqual(saved.RenderData.Tangents) &&
                              source.RenderData.TextureCoordinates.SequenceEqual(saved.RenderData.TextureCoordinates) &&
                              source.RenderData.BoneIndices.SequenceEqual(saved.RenderData.BoneIndices) &&
                              source.RenderData.BoneWeights.SequenceEqual(saved.RenderData.BoneWeights),
                $"Saved head LOD {source.LodIndex} changed render buffers.");
            QaContext.Require(source.Topology.Sections.SequenceEqual(saved.Topology.Sections) &&
                              source.Topology.Chunks.Count == saved.Topology.Chunks.Count &&
                              source.Topology.Chunks.Zip(saved.Topology.Chunks).All(pair =>
                                  pair.First.BaseVertexIndex == pair.Second.BaseVertexIndex &&
                                  pair.First.RigidVertexCount == pair.Second.RigidVertexCount &&
                                  pair.First.SoftVertexCount == pair.Second.SoftVertexCount &&
                                  pair.First.MaxBoneInfluences == pair.Second.MaxBoneInfluences &&
                                  pair.First.BoneMap.SequenceEqual(pair.Second.BoneMap)) &&
                              source.Topology.ReferenceSkeleton.SequenceEqual(saved.Topology.ReferenceSkeleton) &&
                              source.Topology.ActiveBones.SequenceEqual(saved.Topology.ActiveBones) &&
                              source.Topology.RequiredBones.SequenceEqual(saved.Topology.RequiredBones),
                $"Saved head LOD {source.LodIndex} changed topology or rig data.");
        }
    }

    private static void VerifyFaceOverrides(
        MorphFaceMaterialOverrides expected,
        MorphFaceMaterialOverrides actual)
    {
        static string TextureSignature(IEnumerable<TextureMaterialOverride> values) => string.Join("\n",
            values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).Select(value =>
                $"{value.Name}|{value.TextureReference?.ClassName}|{value.TextureReference?.InstancedPath}"));
        QaContext.Require(expected.Scalars.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                              .SequenceEqual(actual.Scalars.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)) &&
                          expected.Vectors.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                              .SequenceEqual(actual.Vectors.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)),
            "The scalp graph switch changed unrelated face scalar or vector overrides.");
        QaContext.Require(TextureSignature(expected.Textures) == TextureSignature(actual.Textures),
            "Saved face texture overrides differ from the selected ShortAfro scalp pair.");
    }

    private static string EntryPath(IEntry entry) => entry is ExportEntry export
        ? PccAssetPathPolicy.FromExport(export)
        : entry.InstancedFullPath;
}
