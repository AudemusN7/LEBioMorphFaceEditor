using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    internal static (MorphRandomisationDonor Donor, IReadOnlySet<string> Scope, MorphRandomisationProposal Proposal)
        SelectPlayerConversionDonor(MorphRandomisationCatalog catalog, string profileKey, int seed,
            IReadOnlyDictionary<string, float> current, ISet<string> usedDonors)
    {
        var pool = MorphRandomisationPoolRouter.Resolve(profileKey);
        var remaining = (catalog.Corpus.Pools.GetValueOrDefault(pool) ?? [])
            .Where(d => d.AvailableFeatures.Count > 0).ToHashSet();
        // Keep the original seeded draw when it works. Each rejected donor gets one try.
        while (remaining.Count > 0)
        {
            var hasUnused = remaining.Any(d => !usedDonors.Contains(d.Id));
            var donor = MorphRandomiser.SelectDonor(catalog.Corpus, pool, seed,
                d => remaining.Contains(d) && (!hasUnused || !usedDonors.Contains(d.Id)));
            remaining.Remove(donor);
            var scope = donor.NonZeroValues.Keys.Where(current.ContainsKey)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(8).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (scope.Count < 2) continue;
            var values = scope.ToDictionary(n => n, n => current[n], StringComparer.OrdinalIgnoreCase);
            var bounds = scope.Select(n => new MorphRandomisationFeatureBounds(n, -1f, 1f)).ToArray();
            var proposal = MorphRandomiser.CreateProposal(donor, values, bounds, scope, 45, seed);
            if (scope.Count(n => Math.Abs(proposal.Values[n] - values[n]) > 0.000001f) < 2) continue;
            usedDonors.Add(donor.Id);
            return (donor, scope, proposal);
        }
        throw new InvalidDataException("No random donor produces at least two shared editable slider changes.");
    }

    private static async Task PlayerConversionJourney(QaContext c, MorphFaceGame targetGame, bool known)
    {
        var sourceGame = c.Face.Game;
        var sex = c.Face.PlayerSex!.Value;
        var sourceSeedPath = StandalonePlayerMorphImportService.ResolveInstalledSeed(sourceGame);
        var targetSeedPath = StandalonePlayerMorphImportService.ResolveInstalledSeed(targetGame);
        c.TrackInput(sourceSeedPath); c.TrackInput(targetSeedPath);
        var sourceRegistry = await c.Textures.ReadAsync(sourceGame);
        var targetRegistry = await c.Textures.ReadAsync(targetGame);
        if (!targetRegistry.IsAvailable || !sourceRegistry.IsAvailable)
            throw new QaPrerequisiteException("Player conversion requires ready source and target asset databases.");
        using var reader = new MorphFacePackageReader();
        var source = reader.Load(sourceSeedPath, StandalonePlayerMorphImportService.ResolvePlayerTemplate(sourceGame, sex), decodeTextures: false);
        var target = reader.Load(targetSeedPath, StandalonePlayerMorphImportService.ResolvePlayerTemplate(targetGame, sex), decodeTextures: false);
        var sourceProfile = c.Profiles.Require(sourceGame, source.Document.Source.InstancedPath, source.Document.BaseHeadReference?.InstancedPath);
        var targetProfile = c.Profiles.Require(targetGame, target.Document.Source.InstancedPath, target.Document.BaseHeadReference?.InstancedPath);
        var head = MorphFacePackageContextService.CreateDefaultHead(source);
        var sourceBase = head.MorphData;
        var targetBase = MorphFacePackageContextService.CreateDefaultHead(target).MorphData;
        using var generated = new QaSyntheticPlayerBaselines(known ? [sourceGame, targetGame] : [], known ? [sex] : []);
        var baselines = generated.Catalog;
        string baselineId = "native";
        if (known)
        {
            var baseline = baselines.ForProfile(sourceProfile).SingleOrDefault()
                ?? throw new InvalidDataException($"Generated Player baseline was not accepted for {sourceProfile.Key}.");
            baselineId = baseline.Id;
            sourceBase = baseline.MorphData;
            targetBase = (baselines.FindCounterpart(baseline.Id, targetProfile)
                ?? throw new InvalidDataException($"Generated Player baseline counterpart {baseline.Id} was not accepted for {targetProfile.Key}.")).MorphData;
        }
        c.Current.Evidence["playerBaseline"] = new { Id = baselineId, Source = sourceGame, Target = targetGame, Sex = sex, Rolls = 3 };
        // Native FemShep exercises an iconic face with an independently authored generic scalp.
        var genericScalp = !known && sex == StandalonePlayerSex.Female;
        var sourceTextures = QaPlayerConversionTextures.Expected(sourceGame, sex, genericScalp);
        var targetTextures = QaPlayerConversionTextures.Expected(targetGame, sex, genericScalp);
        head = head with { MorphData = sourceBase, MaterialData = sourceTextures };
        var sourceAssets = StandalonePlayerAssetCatalog.ForHead(sourceGame, head, sourceRegistry.Candidates,
            sourceSeedPath, sourceRegistry.AttachmentMeshes);
        var importer = new StandalonePlayerMorphImportService();
        using var imported = importer.ImportPlayerHead(sourceGame, head, "QaPlayerConversion", sourceAssets);
        // Keep the input and resulting packages beside the report rather than in the temporary workspace.
        var importedSource = reader.Load(imported.Workspace.WorkingPath, imported.ImportedFacePath, decodeTextures: false);
        var baselineSaved = await c.Save(importedSource.Document, sourceSeedPath, "player-source.pcc");
        var baselinePackage = baselineSaved.PackagePath;
        c.TrackInput(baselinePackage);
        var facePath = baselineSaved.FaceInstancedPath;
        var original = ReadPackageHead(baselinePackage, facePath);
        QaPlayerConversionTextures.RequireOverrides(sourceTextures, original.MaterialData, "Authored source PCC");
        var service = new MorphFaceConversionService(c.Profiles, c.Targets, new MorphFacePackageContextService(), c.Textures, playerBaselines: baselines);
        var initialBytes = File.ReadAllBytes(baselinePackage);
        var initialRon = c.Artifact("unedited.ron");
        c.MeasureSync("Convert unedited Player baseline", () => service.ConvertPlayerRon(new(baselinePackage, facePath, targetGame, initialRon)), QaStageKind.FileTransfer);
        QaContext.Require(initialBytes.SequenceEqual(File.ReadAllBytes(baselinePackage)), "Unedited conversion changed the source PCC.");
        var unedited = TseHeadMorphRon.ReadDocument(initialRon);
        PlayerProvenance(unedited.Provenance, targetGame, c.Face.Family);
        QaPlayerConversionTextures.RequireOverrides(targetTextures, unedited.Head.MaterialData, "Unedited converted RON");
        var targetAssets = StandalonePlayerAssetCatalog.ForHead(targetGame, unedited.Head, targetRegistry.Candidates,
            targetSeedPath, targetRegistry.AttachmentMeshes);
        PlayerVertices(targetBase.BakedLods[0], unedited.Head.MorphData.BakedLods[0], "Unedited paired baseline");
        var targetBind = target.BaseHead.Topology.ReferenceSkeleton.ToDictionary(b => b.Name, b => b.Position, StringComparer.OrdinalIgnoreCase);
        foreach (var bone in targetBase.FinalSkeleton)
            PlayerNear(bone.Translation, PlayerBone(unedited.Head.MorphData.FinalSkeleton, targetBind, bone.BoneName), "Unedited bone " + bone.BoneName);

        using var vmReader = new MorphFacePackageReader();
        using var vm = FileWorkflow(c, new QaDialogs(), vmReader, baselines);
        await SelectFileFace(c, vm, baselinePackage, facePath);
        var editor = vm.Editor!;
        QaContext.Require(editor.CanEditMorphFeatures && editor.CanEditBones && !vm.FixMorphCommand.CanExecute(null) && !editor.HasPendingRepair && !editor.IsDirty && !vm.IsDirty,
            "Player PCC did not open cleanly with editable morphs and bones without Fix Morph.");
        var initialDraft = editor.CreateDraft();
        var initialDifferences = QaHeadPayload.GeometryDifferences(original.MorphData,
            new(initialDraft.MorphFeatures, initialDraft.FinalSkeleton, initialDraft.BakedLods), compareNameOrder: false,
            equivalentBoneZeroSigns: !known);
        QaContext.Require(initialDifferences.Count == 0, "Opening Player PCC changed its geometry: " + string.Join("; ", initialDifferences));
        var random = c.RandomFor("player-conversion-rolls");
        var seeds = new List<int>();
        var usedDonors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalog = MorphRandomisationCatalog.LoadEmbedded();
        var targets = c.Targets.Load(sourceProfile);
        var targetTargets = c.Targets.Load(targetProfile);
        var sharedSliders = DirectPlayerSliders(sourceProfile, targets)
            .Intersect(DirectPlayerSliders(targetProfile, targetTargets), StringComparer.OrdinalIgnoreCase)
            .Intersect(editor.Features.Where(f => f.IsEditable).Select(f => f.Name), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? lastPassingPackage = null;
        string? lastPassingFace = null;
        var summary = await QaRandomisationBatch.Run(async roll =>
        {
            var package = c.Artifact($"roll-{roll}-source.pcc");
            File.Copy(baselinePackage, package);
            var loaded = reader.Load(package, facePath, decodeTextures: false);
            var session = new MorphFaceEditingSession(loaded.Document, loaded.BaseHead, targets,
                sourceProfile.MetadataOnlyFeatures, sourceProfile.GetDisplayName(), sourceProfile.FeatureAliases,
                sourceProfile.RecognizesBaseVariant, sourceProfile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath),
                geometryMode: MorphFaceGeometryMode.RelativeBake);
            QaContext.Require(session.CanEditMorphFeatures && session.CanEditBones, "Player source is not editable.");
            var seed = random.Next(); seeds.Add(seed);
            var current = session.Features.Where(f => sharedSliders.Contains(f.Name))
                .ToDictionary(f => f.Name, f => f.Offset, StringComparer.OrdinalIgnoreCase);
            var (donor, scope, proposal) = SelectPlayerConversionDonor(catalog, sourceProfile.Key, seed, current, usedDonors);
            session.SetFeatures(proposal.Values);
            QaContext.Require(scope.Count(n => Math.Abs(session.GetFeature(n) - current[n]) > 0.000001f) >= 2,
                "Randomisation did not change at least two shared editable sliders.");
            var targetBoneNames = targetBind.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var featureBones = targets.SelectMany(t => t.BoneOffsets).Select(b => b.BoneName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bones = session.Evaluation.FinalSkeleton.Select(b => b.BoneName).Where(n => targetBoneNames.Contains(n) && featureBones.Contains(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
            QaContext.Require(bones.Length >= 3, "Fewer than three shared facial bones are available.");
            for (var i = 0; i < bones.Length; i++)
            {
                session.SetBoneAxis(bones[i], 0, session.GetBoneAxis(bones[i], 0) + 0.02f + i * 0.01f);
                session.SetBoneAxis(bones[i], 2, session.GetBoneAxis(bones[i], 2) - 0.03f - i * 0.01f);
            }
            var draft = session.CreateDraft(loaded.Document.HairMeshReference, loaded.Document.OtherMeshReferences, loaded.Document.MaterialOverrides);
            new MorphFacePackageWriter().SaveExisting(draft);
            c.TrackInput(package);
            var persisted = ReadPackageHead(package, facePath).MorphData;
            var draftData = new MorphFaceMorphData(draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods);
            var differences = QaHeadPayload.GeometryDifferences(draftData, persisted);
            QaContext.Require(differences.Count == 0, "Edited source PCC persistence: " + string.Join("; ", differences));
            var savedBytes = File.ReadAllBytes(package);
            var output = c.Artifact($"roll-{roll}-converted.ron");
            var sourceRon = c.Artifact($"roll-{roll}-edited-source.ron");
            new MorphFacePackageContextService().ExportRon(package, facePath, sourceRon,
                new RonExportProvenance(RonExportProducer.MFE, sourceGame, c.Face.Family, true));
            c.TrackInput(sourceRon);
            var sourceRonBytes = File.ReadAllBytes(sourceRon);
            var dialogs = new QaDialogs { ImportGame = sourceGame,
                PlayerConversionChoice = roll == 2 ? null : new(targetGame, output, true, null),
                AllowPlayerConversionCancellation = roll == 2 };
            using var playerReader = new MorphFacePackageReader();
            using var playerVm = FileWorkflow(c, dialogs, playerReader, baselines);
            await c.Measure("Open edited Player RON", () => playerVm.OpenDroppedFileAsync(sourceRon), QaStageKind.FileTransfer);
            CheckVm(playerVm);
            QaContext.Require(playerVm.Editor is not null && playerVm.ConvertMorphCommand.CanExecute(null), "Player RON conversion command is unavailable.");
            var playerEditor = playerVm.Editor!;
            var working = playerEditor.CreateDraft().Source.PackagePath;
            var workingFingerprint = PackageFingerprint.Capture(working);
            MorphFaceDocument? pending = null;
            if (roll == 2)
            {
                var bone = playerEditor.Bones.First(axis => axis.IsAvailable &&
                    axis.BoneName.Equals(bones[0], StringComparison.OrdinalIgnoreCase));
                bone.BeginEdit(); bone.Value += 0.02f; bone.EndEdit();
                pending = playerEditor.CreateDraft();
                QaContext.Require(playerVm.IsDirty && playerEditor.IsDirty, "Pending bone edit did not mark the Player workspace dirty.");
                await c.Measure("Cancel Player conversion with pending edit", () => Execute(playerVm.ConvertMorphCommand), QaStageKind.FileTransfer);
                CheckVm(playerVm);
                QaContext.Require(playerVm.IsDirty && playerEditor.IsDirty &&
                    MorphFaceEditorStateComparer.Equals(pending, playerEditor.CreateDraft()) &&
                    workingFingerprint == PackageFingerprint.Capture(working) && sourceRonBytes.SequenceEqual(File.ReadAllBytes(sourceRon)) &&
                    !File.Exists(output), "Cancelling Player conversion changed the draft, input or workspace.");
                dialogs.PlayerConversionChoice = new(targetGame, output, true, null);
            }
            await c.Measure("Convert edited Player RON", () => Execute(playerVm.ConvertMorphCommand), QaStageKind.FileTransfer);
            c.Current.Evidence[$"playerConversionMessages/{roll}"] = dialogs.Messages.ToArray();
            CheckVm(playerVm);
            QaContext.Require(File.Exists(output), "Player conversion command did not publish its RON.");
            QaContext.Require(savedBytes.SequenceEqual(File.ReadAllBytes(package)) && sourceRonBytes.SequenceEqual(File.ReadAllBytes(sourceRon)),
                "Conversion changed its source PCC or RON.");
            if (pending is null)
                QaContext.Require(workingFingerprint == PackageFingerprint.Capture(working), "Conversion changed the clean Player workspace.");
            else
            {
                QaContext.Require(playerVm.IsDirty && playerEditor.IsDirty && playerEditor.CreateDraft().Source.PackagePath == working &&
                    MorphFaceEditorStateComparer.Equals(pending, playerEditor.CreateDraft()), "Conversion cleared or replaced the pending Player draft.");
                var flushed = ReadPackageHead(working, playerVm.SelectedFace!.InstancedPath).MorphData;
                var pendingData = new MorphFaceMorphData(pending.MorphFeatures, pending.FinalSkeleton, pending.BakedLods);
                var flushDifferences = QaHeadPayload.GeometryDifferences(pendingData, flushed, compareNameOrder: false);
                QaContext.Require(flushDifferences.Count == 0, "Pending Player edit was not flushed: " + string.Join("; ", flushDifferences));
                persisted = pendingData;
                c.Current.Evidence["pendingPlayerConversion"] = "Cancellation preserved the dirty draft and conversion flushed it without changing the original RON.";
            }
            var ron = TseHeadMorphRon.ReadDocument(output);
            PlayerProvenance(ron.Provenance, targetGame, c.Face.Family);
            QaPlayerConversionTextures.RequireOverrides(targetTextures, ron.Head.MaterialData, "Converted Player RON");
            var converted = ron.Head.MorphData;
            foreach (var name in scope)
            {
                var expected = PlayerWeight(targetBase.MorphFeatures, name) + PlayerWeight(persisted.MorphFeatures, name) - PlayerWeight(sourceBase.MorphFeatures, name);
                QaContext.Require(Math.Abs(expected - PlayerWeight(converted.MorphFeatures, name)) <= 0.00001f,
                    $"Converted slider {name}: expected {expected}, got {PlayerWeight(converted.MorphFeatures, name)}.");
            }
            var initialWeights = PlayerWeights(targetBase.MorphFeatures, targetTargets, targetProfile);
            var finalWeights = PlayerWeights(converted.MorphFeatures, targetTargets, targetProfile);
            var zero = new Vector3[targetBase.BakedLods[0].Length];
            var a = SparseMorphEvaluator.EvaluatePositions(zero, initialWeights, 0);
            var b = SparseMorphEvaluator.EvaluatePositions(zero, finalWeights, 0);
            PlayerVertices(targetBase.BakedLods[0].Select((v, i) => v + b[i] - a[i]).ToArray(), converted.BakedLods[0], "Converted relative geometry");
            var sourceBind = source.BaseHead.Topology.ReferenceSkeleton.ToDictionary(bone => bone.Name, bone => bone.Position, StringComparer.OrdinalIgnoreCase);
            var sourceInitial = PlayerWeights(sourceBase.MorphFeatures, targets, sourceProfile);
            var sourceFinal = PlayerWeights(persisted.MorphFeatures, targets, sourceProfile);
            var sharedBones = sourceBind.Keys.Where(targetBind.ContainsKey).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            QaContext.Require(sharedBones.Length >= bones.Length, "Shared reference bones are missing.");
            foreach (var name in sharedBones)
            {
                var residual = PlayerBone(persisted.FinalSkeleton, sourceBind, name) - PlayerBone(sourceBase.FinalSkeleton, sourceBind, name)
                        - (PlayerOffset(sourceFinal, name) - PlayerOffset(sourceInitial, name));
                var expected = PlayerBone(targetBase.FinalSkeleton, targetBind, name) + PlayerOffset(finalWeights, name) - PlayerOffset(initialWeights, name) + residual;
                PlayerNear(expected, PlayerBone(converted.FinalSkeleton, targetBind, name), "Converted bone " + name);
            }
            using var reimported = importer.ImportPlayerRon(targetGame, output, "QaPlayerReturn", targetAssets);
            var reopened = ReadPackageHead(reimported.Workspace.WorkingPath, reimported.ImportedFacePath);
            QaPlayerConversionTextures.RequireOverrides(targetTextures, reopened.MaterialData, "Reimported Player PCC");
            differences = QaHeadPayload.GeometryDifferences(converted, reopened.MorphData);
            QaContext.Require(differences.Count == 0, "Reimported Player geometry: " + string.Join("; ", differences));
            var reimportedDocument = reader.Load(reimported.Workspace.WorkingPath, reimported.ImportedFacePath, decodeTextures: false).Document;
            var retainedSave = await c.Save(reimportedDocument, targetSeedPath, $"roll-{roll}-reimported.pcc");
            var retained = retainedSave.PackagePath;
            var returnedPath = c.Artifact($"roll-{roll}-returned.ron");
            c.MeasureSync("Convert Player back", () => service.ConvertPlayerRon(new(reimported.Workspace.WorkingPath, reimported.ImportedFacePath, sourceGame, returnedPath)), QaStageKind.FileTransfer);
            var returned = TseHeadMorphRon.ReadDocument(returnedPath);
            PlayerProvenance(returned.Provenance, sourceGame, c.Face.Family);
            QaPlayerConversionTextures.RequireOverrides(sourceTextures, returned.Head.MaterialData, "Returned Player RON");
            PlayerVertices(persisted.BakedLods[0], returned.Head.MorphData.BakedLods[0], "Returned geometry");
            foreach (var name in sharedBones) PlayerNear(PlayerBone(persisted.FinalSkeleton, sourceBind, name), PlayerBone(returned.Head.MorphData.FinalSkeleton, sourceBind, name), "Returned bone " + name);
            c.Current.Evidence[$"playerConversionRoll/{roll}"] = new { Seed = seed, Donor = donor.Id, Bones = bones, SharedBoneCount = sharedBones.Length, Sliders = scope.Order(StringComparer.OrdinalIgnoreCase).ToArray() };
            lastPassingPackage = retained;
            lastPassingFace = retainedSave.FaceInstancedPath;
            return Array.Empty<string>();
        }, attempts: 3);
        c.Current.Evidence["playerConversionRolls"] = new { Seeds = seeds, Summary = summary };
        if (lastPassingPackage is not null)
        {
            var preview = await c.Load(lastPassingPackage, lastPassingFace!, MorphFaceGeometryMode.RelativeBake);
            c.Current.Evidence["appliedPlayerTextures"] = QaPlayerConversionTextures.RequireApplied(targetTextures, preview);
            await c.Render(preview.Scene, "player-conversion-reopened.png");
        }
        QaContext.Require(summary.Successful == summary.Attempts, "One or more Player mixed conversion rolls failed: " + string.Join("; ", summary.Failures));
        if (!known && sourceGame == MorphFaceGame.LE1 && targetGame == MorphFaceGame.LE3 && sex == StandalonePlayerSex.Female)
        {
            // Exercise the one explicitly supported approximation route without local reference heads.
            var delta = new Vector3(0.04f, -0.06f, 0.08f);
            var shaped = sourceBase with { BakedLods = [source.BaseHead.Positions.Select(v => v + delta).ToArray()] };
            using var sculpt = importer.ImportPlayerHead(sourceGame, head with { MorphData = shaped }, "QaApproximatePlayer", sourceAssets);
            var bytes = File.ReadAllBytes(sculpt.Workspace.WorkingPath);
            var rejected = c.Artifact("approximate-default-rejected.ron");
            Exception? error = null;
            try { service.ConvertPlayerRon(new(sculpt.Workspace.WorkingPath, sculpt.ImportedFacePath, targetGame, rejected)); }
            catch (Exception ex) { error = ex; }
            QaContext.Require(error is not null && UserFacingMessage.FromException(error)?.LocalizationKey == "loc_PlayerConversion_UnsupportedGeometry" && !File.Exists(rejected),
                "Unrecognised sculpt was not rejected by default.");
            var output = c.Artifact("approximate-opt-in.ron");
            c.MeasureSync("Convert opted-in baked shape", () => service.ConvertPlayerRon(new(
                sculpt.Workspace.WorkingPath, sculpt.ImportedFacePath, targetGame, output, ApproximateBakedShape: true)), QaStageKind.FileTransfer);
            QaContext.Require(bytes.SequenceEqual(File.ReadAllBytes(sculpt.Workspace.WorkingPath)), "Approximation changed its source PCC.");
            var result = TseHeadMorphRon.ReadDocument(output);
            PlayerProvenance(result.Provenance, targetGame, c.Face.Family);
            QaContext.Require(result.Head.MorphData.BakedLods[0].Length == target.BaseHead.Positions.Length, "Approximate RON has an incomplete LOD0.");
            QaPlayerConversionTextures.RequireOverrides(targetTextures, result.Head.MaterialData, "Approximate Player RON");
            using var reimport = importer.ImportPlayerRon(targetGame, output, "QaApproximateReopen", targetAssets);
            var reopened = ReadPackageHead(reimport.Workspace.WorkingPath, reimport.ImportedFacePath);
            QaPlayerConversionTextures.RequireOverrides(targetTextures, reopened.MaterialData, "Reimported approximate Player PCC");
            QaContext.Require(QaHeadPayload.GeometryDifferences(result.Head.MorphData, reopened.MorphData).Count == 0,
                "Approximate Player RON changed on standalone reimport.");
            var preview = await c.Load(reimport.Workspace.WorkingPath, reimport.ImportedFacePath, MorphFaceGeometryMode.RelativeBake);
            c.Current.Evidence["appliedApproximatePlayerTextures"] = QaPlayerConversionTextures.RequireApplied(targetTextures, preview);
            c.Current.Evidence["approximateBakedShape"] = "Default rejected; opt-in produced a complete, reimported LE3 Player RON without changing the source.";
        }
        if (known)
        {
            var lods = sourceBase.BakedLods.Select(l => l.ToArray()).ToArray();
            lods[0][0] += new Vector3(0, 0.1f, 0);
            using var sculpt = importer.ImportPlayerHead(sourceGame, head with { MorphData = sourceBase with { BakedLods = lods } }, "QaRejectedSculpt", sourceAssets);
            var sculptDocument = reader.Load(sculpt.Workspace.WorkingPath, sculpt.ImportedFacePath, decodeTextures: false).Document;
            var sculptSaved = await c.Save(sculptDocument, sourceSeedPath, "unsupported-sculpt.pcc");
            var sculptPath = sculptSaved.PackagePath;
            var before = File.ReadAllBytes(sculptPath);
            using var sculptReader = new MorphFacePackageReader();
            using var sculptVm = FileWorkflow(c, new QaDialogs(), sculptReader, baselines);
            await SelectFileFace(c, sculptVm, sculptPath, sculptSaved.FaceInstancedPath);
            QaContext.Require(!sculptVm.Editor!.CanEditMorphFeatures && sculptVm.Editor.Features.All(f => !f.IsEditable),
                "Unproven extra sculpt exposed morph sliders.");
            var rejected = Path.Combine(Path.GetDirectoryName(sculptPath)!, c.UniqueName("rejected.ron"));
            Exception? error = null;
            try { service.ConvertPlayerRon(new(sculptPath, sculptSaved.FaceInstancedPath, targetGame, rejected)); }
            catch (Exception ex) { error = ex; }
            QaContext.Require(error is not null && UserFacingMessage.FromException(error)?.LocalizationKey == "loc_PlayerConversion_UnsupportedGeometry",
                "Extra sculpt was not rejected as unsupported geometry.");
            QaContext.Require(!File.Exists(rejected) && before.SequenceEqual(File.ReadAllBytes(sculptPath)), "Rejected sculpt created output or changed its input.");
            c.Current.Evidence["unsupportedSculpt"] = "Rejected without output or input changes.";
        }
    }

    private static float PlayerWeight(IReadOnlyList<MorphFeatureValue> features, string name) =>
        features.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Offset ?? 0f;
    private static string PlayerTargetName(MorphTargetAsset target) => target.Source.InstancedPath.Split('.').Last();
    internal static IEnumerable<string> DirectPlayerSliders(MorphFaceProfile profile, IReadOnlyList<MorphTargetAsset> targets) =>
        targets.Select(PlayerTargetName).Where(n => !profile.MetadataOnlyFeatures.Contains(n) && !profile.FeatureAliases.ContainsKey(n));
    private static IReadOnlyList<WeightedMorphTarget> PlayerWeights(IReadOnlyList<MorphFeatureValue> features, IReadOnlyList<MorphTargetAsset> targets, MorphFaceProfile profile)
    {
        var native = targets.Where(t => !profile.MetadataOnlyFeatures.Contains(PlayerTargetName(t))).ToDictionary(PlayerTargetName, StringComparer.OrdinalIgnoreCase);
        return features.Where(f => f.Offset != 0 && native.ContainsKey(f.Name)).Select(f => new WeightedMorphTarget(f.Name, native[f.Name], f.Offset)).ToArray();
    }
    private static Vector3 PlayerBone(IReadOnlyList<BoneTranslation> bones, IReadOnlyDictionary<string, Vector3> bind, string name) =>
        bones.FirstOrDefault(b => string.Equals(b.BoneName, name, StringComparison.OrdinalIgnoreCase))?.Translation ?? bind[name];
    private static Vector3 PlayerOffset(IReadOnlyList<WeightedMorphTarget> targets, string name) => targets.SelectMany(t => t.Target.BoneOffsets
        .Where(b => string.Equals(b.BoneName, name, StringComparison.OrdinalIgnoreCase)).Select(b => b.Offset * t.Weight)).Aggregate(Vector3.Zero, (a, b) => a + b);
    private static void PlayerNear(Vector3 expected, Vector3 actual, string label) =>
        QaContext.Require(Vector3.Distance(expected, actual) <= 0.0004f, $"{label}: expected {expected}, got {actual}.");
    private static void PlayerVertices(IReadOnlyList<Vector3> expected, IReadOnlyList<Vector3> actual, string label)
    {
        QaContext.Require(expected.Count == actual.Count, $"{label}: vertex counts differ.");
        for (var i = 0; i < expected.Count; i++) PlayerNear(expected[i], actual[i], $"{label}, vertex {i}");
    }
    private static void PlayerProvenance(RonExportProvenance? actual, MorphFaceGame game, string family) =>
        QaContext.Require(actual == new RonExportProvenance(RonExportProducer.MFE, game, family, true), "Converted RON has incorrect provenance.");
}
