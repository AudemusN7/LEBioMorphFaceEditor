using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static async Task<(string Path, ImportedMeshAsset Mesh)> ExportMesh(QaContext c, MorphMeshFormat format)
    {
        var source = c.Source();
        var preview = await c.Load(source.PackagePath, source.FacePath);
        using var scope = await c.Editor(preview, source.PackagePath);
        scope.Editor.RandomiseMaterials = false;
        var original = scope.Editor.CreateDraft();
        await c.Randomise100(scope.Editor, "Mesh morph randomisation");
        var bake = scope.Editor.CreateDraft();
        QaContext.Require(!original.MorphFeatures.SequenceEqual(bake.MorphFeatures) &&
            original.BakedLods[0].Zip(bake.BakedLods[0]).Any(pair => pair.First != pair.Second),
            "HMM slider roll did not change the exported baked geometry.");
        var saved = await c.Save(bake, source.PackagePath, "randomised-mesh-source.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath);
        AssertGeometry(bake, reopened.Loaded.Document);
        var slots = reopened.Loaded.BaseHead.RenderData?.MaterialSlots
            ?? throw new InvalidDataException("HMM head has no material slots.");
        QaContext.Require(slots.Count == 4, "The selected HMM head does not have four material slots.");
        c.HmmSlotFamilies.Clear();
        for (var index = 0; index < slots.Count; index++)
            c.HmmSlotFamilies[index] = reopened.Loaded.Materials.Find(slots[index])?.Family
                ?? throw new InvalidDataException($"HMM source slot {index} lacks a resolved material.");
        var directory = Path.Combine(c.Output, c.Current.Id, "mesh-" + format);
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var exporter = new MorphFaceInterchangeService();
        var exported = await c.Measure($"{format} export through UModel", () => exporter.ExportMeshAsync(
            saved.PackagePath, saved.FaceInstancedPath, directory, format, timeout.Token), QaStageKind.FileTransfer);
        var renamed = QaOutputFiles.NamespaceMeshExport(c, directory);
        foreach (var file in renamed.Values)
            c.Current.Artifacts.Add(file);
        var path = renamed[exported.ProducedFiles.Select(Path.GetFullPath).First(file => format == MorphMeshFormat.Psk
            ? Path.GetExtension(file).Equals(".psk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".pskx", StringComparison.OrdinalIgnoreCase)
            : Path.GetExtension(file).Equals(".gltf", StringComparison.OrdinalIgnoreCase))];
        var mesh = await c.Measure("detached mesh decoding", () => Task.Run(() => exporter.ReadMesh(path)), QaStageKind.FileTransfer);
        QaContext.Require(mesh.Positions.Length > 0 && mesh.Indices.Length > 0 && mesh.Sections.Count > 0,
            "Reimported mesh lost geometry or material sections.");
        QaContext.Require(mesh.Sections.Select(value => value.MaterialIndex).Distinct().Count() == 4,
            "Export/reimport lost one or more of the four HMM material slots.");
        var candidates = exporter.ReadMeshPositions(path, reopened.Loaded.BaseHead);
        var maxError = candidates.Where(value => value.Positions.Count == bake.BakedLods[0].Length)
            .Select(value => value.Positions.Zip(bake.BakedLods[0]).Max(pair => System.Numerics.Vector3.Distance(pair.First, pair.Second)))
            .DefaultIfEmpty(float.PositiveInfinity).Min();
        QaContext.Require(maxError <= 0.0001f, $"Randomised baked mesh roundtrip differs by {maxError:G9}.");
        c.Current.Evidence["correctedBakeRecoveryMaximumError"] = maxError;
        using var map = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path + ".mfe.json"));
        var coordinateSystem = map.RootElement.GetProperty("CoordinateSystem").GetString();
        var mapping = map.RootElement.GetProperty("ExportedToBase").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var raw = candidates.Single(value => value.CoordinateSystem == coordinateSystem);
        QaContext.Require(mapping.Length == bake.BakedLods[0].Length && mapping.Distinct().Count() == mapping.Length &&
            mapping.All(index => index >= 0 && index < mapping.Length), "Mesh export has no valid raw vertex correspondence.");
        var rawError = raw.Positions.Select((position, index) =>
            System.Numerics.Vector3.Distance(position, bake.BakedLods[0][mapping[index]])).Max();
        c.Current.Evidence["rawExportMaximumError"] = rawError;
        // glTF coordinates can be rounded by UModel; measure before applying MFE's recovery corrections.
        c.Current.Evidence["rawMappedErrorScope"] = "Uses the exporter's inverse-morph sidecar map; independently checks the raw vertex set below because interchange may reorder vertices.";
        var rawMatches = candidates.Where(value => value.Positions.Count == bake.BakedLods[0].Length &&
                value.CoordinateSystem.EndsWith("file order", StringComparison.Ordinal))
            .Select(value => (value.CoordinateSystem, Error: MatchRawVertices(value.Positions, bake.BakedLods[0])))
            .Append((CoordinateSystem: "Custom decoder canonical coordinates", Error: mesh.Positions.Length == bake.BakedLods[0].Length
                ? MatchRawVertices(mesh.Positions, bake.BakedLods[0]) : null))
            .Where(value => value.Error is not null).OrderBy(value => value.Error).ToArray();
        QaContext.Require(rawMatches.Length > 0,
            "No raw interchange coordinate basis has a one-to-one vertex match to the randomised bake within 0.001 units.");
        c.Current.Evidence["rawVertexSetMaximumError"] = rawMatches[0];
        if (rawError > 0.001f)
            c.AddWarnings("Mesh inverse-morph readback", path,
                [$"MFE inverse-morph importer diagnostic: its sidecar coordinate basis/map differs from the bake by {rawError:G9} units before correction. Corrected recovery passed ({maxError:G9}); independent raw Custom mesh geometry passed ({rawMatches[0].Error:G9}). This concerns the inverse-morph route, not deformation of the exported Custom mesh."]);
        c.Current.Evidence["sourceSlotFamilies"] = c.HmmSlotFamilies;
        c.Current.Evidence["mesh"] = new { Format = format, Vertices = mesh.Positions.Length,
            Indices = mesh.Indices.Length, Sections = mesh.Sections, Bones = mesh.Bones.Count };
        return (path, mesh);
    }

    private static float? MatchRawVertices(IReadOnlyList<System.Numerics.Vector3> raw,
        IReadOnlyList<System.Numerics.Vector3> expected)
    {
        var available = Enumerable.Range(0, expected.Count).ToHashSet();
        var maximum = 0f;
        foreach (var position in raw)
        {
            var nearest = -1;
            var distance = 0.001f * 0.001f;
            foreach (var index in available)
            {
                var candidate = System.Numerics.Vector3.DistanceSquared(position, expected[index]);
                if (candidate <= distance) { distance = candidate; nearest = index; }
            }
            if (nearest < 0) return null;
            available.Remove(nearest);
            maximum = Math.Max(maximum, MathF.Sqrt(distance));
        }
        return available.Count == 0 ? maximum : null;
    }

    private static async Task MeshRoundTrip(QaContext c, MorphMeshFormat format)
    {
        var (path, mesh) = await ExportMesh(c, format);
        var detached = await c.LoadCustomWorkspace(mesh, MorphFaceGame.LE2);
        QaContext.Require(detached.CustomMaterials is not null, "Mesh import did not create a Custom material workspace.");
        QaContext.Require(!detached.Preview.EditingSession.CanEditMorphFeatures, "Imported custom mesh exposed unproven morph controls.");
        QaContext.Require(detached.Detached.Source.Positions.Length == mesh.Positions.Length, "Custom import changed the vertex stream length.");
        QaContext.Require(detached.Detached.Source.Indices.SequenceEqual(mesh.Indices), "Custom import changed triangle order.");
        c.AddWarnings("Custom workspace import", path, detached.Detached.Warnings);
        c.Current.Evidence["customWorkspace"] = new { Slots = detached.CustomMaterials!.UsedSlots,
            Morphs = detached.Preview.EditingSession.CanEditMorphFeatures,
            Bones = detached.Preview.EditingSession.CanEditBones };
        await c.Render(detached.Preview.Scene, "custom-import.png");
    }

    private static async Task CustomRandomisation(QaContext c)
    {
        // Keep this case independent: a seed replay with --case must use the
        // same donor and bake as a full run, regardless of earlier mesh cases.
        var (path, mesh) = await ExportMesh(c, MorphMeshFormat.Gltf);
        c.Current.Artifacts.Add(path);
        var reader = c.CustomReader;
        var detached = await c.LoadCustomWorkspace(mesh, MorphFaceGame.LE2);
        var workspace = detached.CustomMaterials ?? throw new InvalidDataException("Custom material workspace was not created.");
        QaContext.Require(workspace.UsedSlots.Count == 4, "Custom randomisation must cover the four HMM material slots.");
        var skins = new[] { HeadMaterialFamily.Skin, HeadMaterialFamily.AsariSkin, HeadMaterialFamily.SalarianSkin, HeadMaterialFamily.BatarianSkin,
            HeadMaterialFamily.TurianSkin, HeadMaterialFamily.KroganSkin, HeadMaterialFamily.VorchaSkin };
        var options = detached.MaterialOptions;
        var rng = c.RandomFor("custom-families");
        foreach (var slot in workspace.UsedSlots)
        {
            var family = c.HmmSlotFamilies[slot.MaterialIndex];
            var matching = options.Where(option => skins.Contains(family) ? skins.Contains(option.Family)
                : family == HeadMaterialFamily.Eyes ? option.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes or
                    HeadMaterialFamily.TurianEyes or HeadMaterialFamily.KroganEyes or HeadMaterialFamily.VorchaEyes
                : option.Family == family).OrderBy(option => option.Id).ToArray();
            QaContext.Require(matching.Length > 0, $"No installed {family} material template is available for slot {slot.MaterialIndex}.");
            workspace.Assign(slot, matching[rng.Next(matching.Length)]);
        }
        c.Current.Evidence["reusedMesh"] = path;
        c.Current.Evidence["assignments"] = workspace.Assignments.Select(value => new { value.Slot.MaterialIndex, value.Option.Id, value.Option.Family }).ToArray();
        using var scope = await c.Editor(detached.Preview, path, detached: detached);
        var editor = scope.Editor;
        var before = MaterialSignature(editor.Material.CaptureInterchangeData());
        await c.Randomise100(editor, "Custom Material randomisation");
        var parameters = editor.Material.CaptureInterchangeData();
        c.Current.Evidence["customMaterialParameters"] = parameters;
        QaContext.Require(before != MaterialSignature(parameters), "Custom randomisation changed no material values.");
        var registry = await c.Textures.ReadAsync(MorphFaceGame.LE2);
        var saved = await c.Measure("Custom mesh PCC export and verification", () => Task.Run(() =>
            new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(workspace.Source, workspace, parameters,
                MorphFaceGame.LE2, c.Artifact("custom-randomised.pcc"), "QaCustomHead",
                new ImportedSkeletalMeshPackageWriter(), registry.Candidates))), QaStageKind.FileTransfer);
        var saveWarnings = QaWarningDiagnostics.InspectSavedPackage(saved.PackagePath, saved.Warnings);
        if (saveWarnings.Length > 0) c.Current.Evidence["customSaveWarningVerification"] = saveWarnings;
        c.AddWarningReviews("Custom mesh PCC export", saved.PackagePath, saveWarnings, relatedPath: path);
        using var savedReader = new MorphFacePackageReader();
        var attachment = c.MeasureSync("Custom PCC mesh/material load", () =>
            savedReader.LoadDetachedAttachment(saved.PackagePath, saved.MeshPath), QaStageKind.Load);
        QaContext.Require(attachment.Mesh.Positions.Length == workspace.Source.Positions.Length &&
            attachment.Mesh.Positions.Zip(workspace.Source.Positions).All(pair =>
                System.Numerics.Vector3.Distance(pair.First, pair.Second) <= 0.00001f),
            "Custom PCC export changed vertex positions.");
        QaContext.Require(attachment.Mesh.RenderData is { } renderData &&
            renderData.Indices.SequenceEqual(workspace.Source.Indices), "Custom PCC export changed triangle indices.");
        QaContext.Require(attachment.Materials.Materials.Count > 0, "Custom PCC lost its material graph.");
        var scopes = MeshMaterialInterchange.Split(parameters);
        var textureReadback = new List<object>();
        c.Current.Evidence["customTextureReadback"] = textureReadback;
        foreach (var assignment in workspace.Assignments)
        {
            var identity = attachment.Mesh.RenderData!.MaterialSlots[assignment.Slot.MaterialIndex];
            var material = attachment.Materials.Find(identity);
            QaContext.Require(material is not null, $"Saved Custom slot {assignment.Slot.MaterialIndex} lost its material.");
            var values = scopes[assignment.Option.EffectiveParameterScopeKey];
            var template = assignment.Option.Template;
            foreach (var scalar in values.Scalars.Where(value => template.Supports(value.Name, MaterialParameterKind.Scalar) &&
                         CustomMeshPccMaterializer.IsSerializableMaterialParameter(value.Name)))
                QaContext.Require(material!.Scalars.TryGetValue(scalar.Name, out var value) && Math.Abs(value - scalar.Value) <= 0.00001f,
                    $"Saved Custom scalar {scalar.Name} differs.");
            foreach (var vector in values.Vectors.Where(value => template.Supports(value.Name, MaterialParameterKind.Vector) &&
                         CustomMeshPccMaterializer.IsSerializableMaterialParameter(value.Name)))
                QaContext.Require(material!.Vectors.TryGetValue(vector.Name, out var value) &&
                    System.Numerics.Vector4.Distance(value, vector.Value) <= 0.00001f, $"Saved Custom vector {vector.Name} differs.");
            foreach (var texture in values.Textures.Where(value => value.TextureReference is not null &&
                         template.Supports(value.Name, MaterialParameterKind.Texture) &&
                         CustomMeshPccMaterializer.IsSerializableMaterialParameter(value.Name)))
            {
                var expected = texture.TextureReference!;
                var actual = material!.Textures.GetValueOrDefault(texture.Name)?.Texture.Source;
                textureReadback.Add(new { Slot = assignment.Slot.MaterialIndex, texture.Name, Expected = expected,
                    Actual = actual, ExpectedPath = QaTextureIdentity.CanonicalPath(expected),
                    ActualPath = actual is null ? null : QaTextureIdentity.CanonicalPath(actual) });
                QaContext.Require(actual is not null && QaTextureIdentity.SameTexture(expected, actual),
                    $"Saved Custom texture {texture.Name} differs in slot {assignment.Slot.MaterialIndex}: " +
                    $"expected '{QaTextureIdentity.CanonicalPath(expected)}'; got '{(actual is null ? "missing" : QaTextureIdentity.CanonicalPath(actual))}'.");
            }
        }
        var loaded = detached.Preview.Loaded with
        {
            BaseHead = attachment.Mesh, Materials = attachment.Materials, UsesCustomBaseMesh = true,
            Document = detached.Preview.Loaded.Document with { BaseHeadReference = attachment.Source }
        };
        c.Current.Evidence["savedCustomMesh"] = saved;
        await c.Render(c.Scenes.Create(loaded), "custom-randomised-reopened.png");
        // Retain the package, geometry/material evidence and PNG before reporting
        // a flag defect. The final aggregate audit checks the same policy again.
        c.MeasureSync("Custom mesh PCC ForcedExport readback", () =>
        {
            using var package = LegendaryExplorerCore.Packages.MEPackageHandler.OpenMEPackage(saved.PackagePath, forceLoadFromDisk: true);
            VerifyCustomMeshForcedExports(package);
            return true;
        }, QaStageKind.FileTransfer);
    }

    private static async Task MaterialRon(QaContext c)
    {
        var source = c.Source();
        var preview = await c.Load(source.PackagePath, source.FacePath);
        using var scope = await c.Editor(preview, source.PackagePath);
        var editor = scope.Editor;
        var initial = editor.CreateDraft();
        await EditMaterial(c, editor, 0.02f);
        var desired = editor.Material.CaptureInterchangeData();
        var ron = MaterialRonCodec.WriteMfe(FaceMaterialFileService.Capture(preview.Loaded.Game, "QaFaceMaterials", desired));
        var file = c.Artifact("materials.ron");
        await c.Measure("material RON export", () => MeshMaterialFileService.WriteAsync(file, ron, source.PackagePath), QaStageKind.FileTransfer);
        editor.UndoCommand.Execute(null);
        AssertState(initial, editor.CreateDraft(), "material export setup undo");
        var (prepared, warnings) = await c.Measure("material RON import and application", async () =>
        {
            var data = FaceMaterialFileService.Read(await File.ReadAllTextAsync(file), allowTse: false);
            return (data, await editor.MergeMaterialDataAsync(data.Parameters));
        }, QaStageKind.FileTransfer);
        c.AddWarnings("Material RON import", file, prepared.Warnings.Concat(warnings), source.FacePath);
        var actual = editor.Material.CaptureInterchangeData();
        c.Current.Evidence["materialRonExpected"] = desired;
        c.Current.Evidence["materialRonParsed"] = prepared.Parameters;
        c.Current.Evidence["materialRonActual"] = actual;
        var differences = MaterialDifferences(desired, actual);
        c.Current.Evidence["materialRonDifferences"] = differences;
        QaContext.Require(MaterialSignature(desired) == MaterialSignature(actual),
            "Material RON did not restore effective parameter values:\n" + string.Join("\n", differences));
        var imported = editor.CreateDraft();
        AssertGeometry(initial, imported);
        editor.UndoCommand.Execute(null);
        AssertState(initial, editor.CreateDraft(), "one undo removes material RON import");
        editor.RedoCommand.Execute(null);
        AssertState(imported, editor.CreateDraft(), "one redo restores material RON import");
        var saved = await c.Save(imported, source.PackagePath, "ron-roundtrip.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath);
        AssertMaterials(imported, reopened.Loaded.Document);
        if (source.Game == MorphFaceGame.LE2 && !c.Face.IsPlayer &&
            c.Face.Family is "ASA" or "HMM" or "HMF")
        {
            var baseHeadPath = reopened.Loaded.BaseHead.Source.InstancedPath;
            using var package = MEPackageHandler.OpenMEPackage(saved.PackagePath, forceLoadFromDisk: true);
            var baseHead = package.FindExport(baseHeadPath, "SkeletalMesh")
                ?? throw new InvalidDataException($"Saved LE2 NPC base head '{baseHeadPath}' is not an export.");
            var evidence = QaNpcEyeGraphCheck.Inspect(package, baseHead, c.Face.Family,
                new InstalledGameAssetResolver());
            c.Current.Evidence["npcEyeMaterialGraph"] = evidence;
            QaNpcEyeGraphCheck.RequireValid(evidence);
        }
        await c.Render(reopened.Scene, "ron-reopened.png");
    }
}
