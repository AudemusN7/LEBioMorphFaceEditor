using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.QA;

internal sealed class QaContext : IDisposable
{
    public string Root { get; }
    public string Corpus { get; }
    public string Output { get; }
    public int Seed { get; }
    public string RunToken { get; } = Guid.NewGuid().ToString("N")[..12];
    private int _outputNumber;
    private int _stageNumber;
    private string _stagePhase = "Test workflow";
    public MorphFaceMeshReferences? PreparedPlayer { get; set; }
    public QaResult Current { get; set; } = null!;
    public QaFaceSpec Face { get; set; } = QaCases.DefaultFace;
    public Dictionary<int, HeadMaterialFamily> HmmSlotFamilies { get; } = [];
    public MorphFaceProfileRegistry Profiles { get; } = MorphFaceProfileRegistry.CreateDefault();
    public MorphTargetCatalog Targets { get; } = new();
    public HeadPreviewSceneFactory Scenes { get; } = new();
    public TextureRegistryStore Store { get; } = new(TextureRegistryPaths.CreateDefault());
    public TextureCatalogService Textures { get; }
    public Dictionary<string, IReadOnlySet<string>> ExistingPackageBaselines { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExistingPackageSources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? AuditOutputDirectory { get; set; }
    private readonly Dictionary<string, PackageFingerprint> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private QaResult? _sourceCase;
    private MorphFaceMeshReferences? _selectedSource;
    private readonly Dictionary<QaResult, List<string>> _recordedFailures = [];
    private readonly HashSet<MorphFaceGame> _warmedGames = [];
    public string? RecordedFailureSummary => _recordedFailures.TryGetValue(Current, out var failures)
        ? string.Join(Environment.NewLine, failures) : null;
    public void ThrowRecordedFailures()
    {
        if (RecordedFailureSummary is { } failure) throw new InvalidDataException(failure);
    }
    public Random RandomFor(string purpose) => QaCoverageSelection.Stream(Seed, Current.Id, purpose);

    public QaContext(string root, string corpus, string output, int seed)
    {
        Root = root; Corpus = Path.GetFullPath(corpus); Output = output; Seed = seed;
        Directory.CreateDirectory(Output);
        LegendaryExplorerCoreRuntime.Initialize();
        Textures = new TextureCatalogService(Store);
    }

    public async Task WarmGame(MorphFaceGame game)
    {
        if (_warmedGames.Contains(game)) return;
        var path = CorpusPackage(game);
        await Measure($"Cold game warm-up · {game}", async () =>
        {
            var source = MorphFaceReferenceInspector.Inspect(path)
                .OrderBy(value => value.FacePath, StringComparer.OrdinalIgnoreCase).First();
            using var reader = new MorphFacePackageReader();
            using var loader = new MorphFacePreviewLoadService(Scenes, Targets, Profiles, reader);
            await loader.LoadAsync(path, source.FacePath);
            Current.Evidence["coldGameWarmUp"] = new { Game = game, Package = path, source.FacePath,
                Scope = "First game load in this QA session; OS disk caches are not flushed. No warm-load limit." };
        }, QaStageKind.ColdLoad);
        _warmedGames.Add(game);
    }

    public string Artifact(string filename)
    {
        var directory = Path.GetFullPath(Path.Combine(Output, Current.Id));
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, UniqueName(filename)));
        Current.Artifacts.Add(path);
        return path;
    }

    public string UniqueName(string filename)
    {
        return $"{RunToken}-{Interlocked.Increment(ref _outputNumber):D4}-{Path.GetFileName(filename)}";
    }

    public string CorpusPackage(MorphFaceGame game)
    {
        var path = Path.Combine(Corpus, $"{game} GlobalMorphs.pcc");
        TrackInput(path);
        return path;
    }

    public void TrackInput(string path)
    {
        if (!File.Exists(path)) throw new QaPrerequisiteException($"Required input is missing: {path}");
        if (!_inputs.ContainsKey(path)) _inputs[path] = PackageFingerprint.Capture(path);
    }

    public void VerifyInputs()
    {
        foreach (var (path, fingerprint) in _inputs)
            Require(PackageFingerprint.Capture(path) == fingerprint, $"Source input changed: {path}");
        Current.Evidence["sourceFilesUnchanged"] = _inputs.Keys.ToArray();
    }

    public async Task<T> Measure<T>(string name, Func<Task<T>> operation, QaStageKind kind = QaStageKind.Diagnostic)
    {
        var stageName = StageName(name);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var stopwatch = Stopwatch.StartNew();
        try { return await operation(); }
        finally
        {
            stopwatch.Stop();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            Current.Stages.Add(new QaStage(stageName, stopwatch.Elapsed.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(true) - allocated, process.WorkingSet64, process.PrivateMemorySize64,
                process.HandleCount, GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1],
                GC.CollectionCount(2) - collections[2], kind));
        }
    }

    public async Task Measure(string name, Func<Task> operation, QaStageKind kind = QaStageKind.Diagnostic) =>
        await Measure(name, async () => { await operation(); return true; }, kind);

    public T MeasureSync<T>(string name, Func<T> operation, QaStageKind kind) =>
        Measure(name, () => Task.FromResult(operation()), kind).GetAwaiter().GetResult();

    public string StageName(string purpose) =>
        $"{Interlocked.Increment(ref _stageNumber):D3} · {_stagePhase} · {purpose}";

    public IDisposable UseStagePhase(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var previous = _stagePhase;
        _stagePhase = phase;
        return new StagePhaseScope(this, previous);
    }

    public void AddWarnings(string origin, string path, IEnumerable<string> warnings,
        string? face = null, string? relatedPath = null, bool informational = false)
    {
        var location = relatedPath is null
            ? Path.GetFullPath(path)
            : $"{Path.GetFullPath(relatedPath)} -> {Path.GetFullPath(path)}";
        var faceLabel = string.IsNullOrWhiteSpace(face) ? "" : $", {face}";
        foreach (var warning in warnings)
            (informational ? Current.Notes : Current.Warnings).Add($"[{_stagePhase}: {origin}, {location}{faceLabel}] {warning}");
    }

    public void AddWarningReviews(string origin, string path, IEnumerable<QaWarningReview> reviews,
        string? face = null, string? relatedPath = null)
    {
        foreach (var review in reviews)
            AddWarnings(origin, path, [review.Message], face, relatedPath,
                informational: review.Classification is "Verified export in saved output" or "Allowed optional LE2 native import");
    }

    public async Task<MorphFacePreviewLoadResult> Load(string path, string face,
        MorphFaceGeometryMode? geometryMode = null)
    {
        geometryMode ??= Face.GeometryMode;
        using var reader = new MorphFacePackageReader();
        using var loader = new MorphFacePreviewLoadService(Scenes, Targets, Profiles, reader);
        var result = await Measure($"Load · {face}",
            () => loader.LoadAsync(path, face, geometryMode: geometryMode), QaStageKind.Load);
        AddWarnings("face preview load", path, result.Loaded.Warnings, face);
        Current.Evidence["profile"] = result.Profile.Key;
        Current.Evidence["baseHeadDonor"] = result.Loaded.BaseHead.Source;
        Current.Evidence["topology"] = result.Loaded.TopologyDiagnostics.Issues;
        Current.Evidence["oracle"] = result.EditingSession.Evaluation.OriginalOracleReport;
        Current.Evidence["canEditMorphs"] = result.EditingSession.CanEditMorphFeatures;
        Current.Evidence["canEditBones"] = result.EditingSession.CanEditBones;
        Current.Evidence["textureDonors"] = result.Loaded.Materials.Materials.Values.SelectMany(material =>
            material.Textures.Select(texture => new { Material = material.Source, Parameter = texture.Key,
                Donor = texture.Value.Texture.Source, texture.Value.Texture.Width, texture.Value.Texture.Height,
                PixelSha256 = Convert.ToHexString(SHA256.HashData(texture.Value.Texture.Rgba8)) })).ToArray();
        Require(result.Scene.Meshes.Count > 0, "Face load produced an empty preview scene.");
        Require(result.Loaded.TopologyDiagnostics.Issues.All(issue =>
            issue.Severity != MorphFaceEditor.Core.Diagnostics.DiagnosticSeverity.Error ||
            result.Profile.IgnoresAuthoredGeometry && issue.Code == "FACE_VERTEX_COUNT"),
            "Face load reported structural topology errors.");
        if (result.Profile.IgnoresAuthoredGeometry)
        {
            Require(!result.EditingSession.CanEditMorphFeatures && !result.EditingSession.CanEditBones,
                "Material-only profile exposed unsafe geometry edits.");
            Current.Evidence["ignoredAuthoredBake"] = "Profile intentionally ignores authored geometry; FACE_VERTEX_COUNT is retained diagnostically. The base mesh and all other structural checks remain required.";
        }
        if (result.EditingSession.Evaluation.OriginalOracleReport is { IsWithinTolerance: false } oracle &&
            geometryMode is not MorphFaceGeometryMode.RelativeBake && !result.Profile.IgnoresAuthoredGeometry)
            throw new InvalidDataException($"Deformation oracle differs by {oracle.MaximumError:G9}.");
        return result;
    }

    public MorphFaceMeshReferences Source(IReadOnlySet<string>? excludedFacePaths = null)
    {
        if (PreparedPlayer is not null) { Current.Evidence["sourceFace"] = PreparedPlayer; return PreparedPlayer; }
        if (ReferenceEquals(_sourceCase, Current) && _selectedSource is not null) return _selectedSource;
        var packagePath = Face.Package(this);
        var references = MeasureSync($"Inspect faces · {Path.GetFileName(packagePath)}",
            () => MorphFaceReferenceInspector.Inspect(packagePath), QaStageKind.Load);
        var candidates = QaCoverageSelection.ExcludePaths((Face.IsPlayer
            ? references.Where(value => value.FacePath.Equals(Face.Selector, StringComparison.OrdinalIgnoreCase))
            : references.Where(value => Face.Family == "TUF" ||
                value.FacePath.StartsWith(Face.Family + ".", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(value => value.FacePath, StringComparer.OrdinalIgnoreCase),
            value => value.FacePath,
            excludedFacePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var source = QaCoverageSelection.Pick(candidates, RandomFor("source"));
        _sourceCase = Current;
        _selectedSource = source;
        Current.Evidence["sourceSelection"] = new { Candidates = candidates.Count, Seed, source.FacePath };
        Current.Evidence["sourceFace"] = source;
        return source;
    }

    public Task<EditorScope> Editor(MorphFacePreviewLoadResult preview, string path, bool? player = null,
        DetachedMeshPreviewLoadResult? detached = null) =>
        Measure("Editor asset catalogue",
            () => CreateEditor(preview, path, player, detached), QaStageKind.Load);

    private async Task<EditorScope> CreateEditor(MorphFacePreviewLoadResult preview, string path, bool? player,
        DetachedMeshPreviewLoadResult? detached)
    {
        var reader = new MorphFacePackageReader();
        var isPlayer = player ?? Face.IsPlayer;
        try
        {
            var references = new PackageReferenceService(reader, Textures);
            var registry = await Textures.ReadAsync(preview.Loaded.Game);
            if (!registry.IsAvailable) throw new QaPrerequisiteException(
                $"{preview.Loaded.Game} asset database must be Ready and current. Rebuild it in MFE settings. {registry.RegistryStatus.ErrorMessage}");
            var packageReferences = detached is null
                ? await references.ReadCatalogAsync(path)
                : new PackageReferenceCatalog([], []);
            var random = RandomFor(PreparedPlayer is null ? "editor" : "prepared-player-editor");
            var seeds = new List<int>();
            Current.Evidence["operationSeeds/" + _stagePhase] = seeds;
            int NextSeed() { var seed = random.Next(); seeds.Add(seed); return seed; }
            var editor = new FaceEditorViewModel(preview.EditingSession, preview.Profile.UiProfile,
                preview.MaterialEditingSession, new QaColorDialog(), references, path,
                isPlayer ? PlayerWorkspaceReferencePolicy.SelectPackageAssets(packageReferences.Textures) : packageReferences.Textures,
                packageReferences.SkeletalMeshes, preview.Loaded.Document.HairMeshReference,
                preview.Loaded.Document.OtherMeshReferences, message => throw new InvalidOperationException(message),
                preview.Profile.Key, MorphRandomisationCatalog.LoadEmbedded(), NextSeed,
                registryTextureCandidates: isPlayer ? PlayerWorkspaceReferencePolicy.SelectRegistryTextures(registry.Candidates) : registry.Candidates,
                textureCatalogProfile: TextureCatalogProfiles.For(preview.Profile), isTextureRegistryAvailable: true,
                ignoresAuthoredGeometry: preview.Profile.IgnoresAuthoredGeometry,
                allowsAttachmentEditing: detached is null,
                customMaterialWorkspace: detached?.CustomMaterials, customMaterialOptions: detached?.MaterialOptions,
                materialRandomisationProfileKey: detached is null ? null : "le2-salarian",
                playerRandomisationGame: isPlayer ? preview.Loaded.Game : null);
            editor.UpdateRegistryAttachmentMeshes(registry.AttachmentMeshes, isPlayer);
            editor.MorphRandomisationStrength = 100;
            editor.MaterialRandomisationStrength = 100;
            editor.RandomiseMorphs = detached is null;
            editor.RandomiseMaterials = true;
            return new EditorScope(editor, reader);
        }
        catch { reader.Dispose(); throw; }
    }

    public async Task Render(HeadPreviewScene scene, string filename)
    {
        await Measure($"Render PNG · {Path.GetFileName(filename)}", () =>
        {
            const int width = 640, height = 640;
            using var renderer = new HeadPreviewRenderer(width, height);
            renderer.SetScene(scene);
            var camera = new HeadOrbitCamera();
            camera.Fit(scene.Bounds); camera.ResetThreeQuarter();
            renderer.Render(camera, new HeadPreviewOptions());
            var frame = renderer.Render(camera, new HeadPreviewOptions());
            var pixels = frame.BgraPixels.ToArray();
            Require(pixels.Length == width * height * 4, "Renderer returned an incomplete pixel buffer.");
            Require(pixels.Where((_, index) => index % 4 != 3).Distinct().Take(3).Count() >= 3,
                "Rendered image is blank or uniform.");
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Artifact(filename)); encoder.Save(output);
            Current.Evidence["renderer"] = renderer.DeviceKind;
            Current.Evidence["renderMs"] = frame.RenderTime.TotalMilliseconds;
            return Task.CompletedTask;
        }, QaStageKind.Render);
    }

    public HeadPreviewScene Scene(MorphFacePreviewLoadResult preview, FaceEditorViewModel editor)
    {
        var loaded = preview.Loaded with { Materials = editor.Material.Materials };
        return editor.UsesLiveDeformationPreview ? Scenes.CreateEditable(loaded, editor.Evaluation) : Scenes.Create(loaded);
    }

    public async Task<QaRollSummary> Randomise100(FaceEditorViewModel editor, string purpose = "Production randomisation",
        Action? beforeFinalRoll = null, Action<int>? afterRoll = null)
    {
        var diagnostics = new List<string>();
        void Observe(string level, string message)
        {
            if (level is "WARN" or "ERROR" || message.Contains("unresolved", StringComparison.OrdinalIgnoreCase))
                lock (diagnostics) diagnostics.Add(message);
        }
        AppLog.MessageWritten += Observe;
        QaRollSummary summary;
        try
        {
            summary = await Measure(purpose + " · 100 rolls", () => QaRandomisationBatch.Run(async roll =>
            {
                lock (diagnostics) diagnostics.Clear();
                if (roll == QaRandomisationBatch.RollCount) beforeFinalRoll?.Invoke();
                try
                {
                    await editor.RandomiseAsync();
                    afterRoll?.Invoke(roll);
                }
                catch (Exception error)
                {
                    lock (diagnostics) diagnostics.Add(error.ToString());
                }
                lock (diagnostics) return (IReadOnlyList<string>)diagnostics.Distinct().ToArray();
            }, roll => Console.WriteLine($"  Randomisation {roll}/100")), QaStageKind.Randomisation);
        }
        finally { AppLog.MessageWritten -= Observe; }
        // The performance threshold applies to one operation, not the sum of 100.
        // Total time and attempts remain explicit in the batch evidence.
        var stage = Current.Stages[^1];
        Current.Stages[^1] = stage with { Name = stage.Name + " · average", DurationMs = summary.AverageMs,
            SampleCount = summary.Attempts, TotalDurationMs = summary.TotalMs };
        Current.Evidence["randomisation/" + purpose] = summary;
        if (summary.Failures.Count > 0)
        {
            if (!_recordedFailures.TryGetValue(Current, out var failures))
                _recordedFailures[Current] = failures = [];
            failures.Add($"{purpose}: {summary.Attempts - summary.Successful}/100 rolls failed. " +
                $"Per-roll failures are retained in evidence 'randomisation/{purpose}'.");
        }
        Console.WriteLine($"  {summary.Successful}/100 successful · average {summary.AverageMs:F1} ms");
        return summary;
    }

    public async Task<MorphFaceSaveResult> Save(MorphFaceDocument draft, string logicalSource, string filename)
    {
        var output = Artifact(filename);
        var result = await Measure($"Save PCC · {Path.GetFileName(filename)}", () => Task.Run(() =>
            new MorphFacePackageWriter().SaveMorphToPackage(draft, logicalSource, output, true)), QaStageKind.FileTransfer);
        var warnings = QaWarningDiagnostics.InspectSavedPackage(output, result.Warnings);
        if (warnings.Length > 0) Current.Evidence["saveWarningVerification/" + filename] = warnings;
        AddWarningReviews("PCC export", output, warnings, result.FaceInstancedPath, logicalSource);
        return result;
    }

    public static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }

    public static string FindWorkspace()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "MorphFaceEditor.slnx"))) return directory.FullName;
        throw new QaPrerequisiteException("Run from the MFE checkout, or place the built utility within it.");
    }

    public void Dispose() { }

    private sealed class StagePhaseScope(QaContext context, string previous) : IDisposable
    {
        private QaContext? _context = context;
        public void Dispose()
        {
            if (_context is not { } context) return;
            context._stagePhase = previous;
            _context = null;
        }
    }
}

internal sealed class EditorScope(FaceEditorViewModel editor, MorphFacePackageReader reader) : IDisposable
{
    public FaceEditorViewModel Editor { get; } = editor;
    public void Dispose() { Editor.Dispose(); reader.Dispose(); }
}

internal sealed class QaColorDialog : IHdrColorDialogService
{
    public bool ExtendedSliders { get; set; }
    public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview) =>
        throw new InvalidOperationException("A QA case unexpectedly requested an interactive colour dialog.");
    public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview) => Edit(title, value, livePreview);
}
