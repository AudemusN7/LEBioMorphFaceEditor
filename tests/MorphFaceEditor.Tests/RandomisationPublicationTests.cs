using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class RandomisationPublicationTests
{
    private const string ProfileKey = "le1-human-male";
    private const string TextureFamily = "human-face";
    private const int OlderSeed = 1;
    private const int NewerSeed = 3;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("normal randomisation does not publish after Reset", () => ResetDuringPendingRoll(cursedMode: false)),
        new("Cursed randomisation does not publish after Reset", () => ResetDuringPendingRoll(cursedMode: true)),
        new("normal older randomisation cannot overwrite a newer roll", () => OlderRollCannotOverwriteNewer(cursedMode: false)),
        new("Cursed older randomisation cannot overwrite a newer roll", () => OlderRollCannotOverwriteNewer(cursedMode: true))
    ];

    private static void ResetDuringPendingRoll(bool cursedMode) => RunWithDispatcher(async () =>
    {
        using var fixture = CreateFixture([101], cursedMode);
        var initialScalar = GetScalar(fixture.Editor);
        TestAssert.Near(6, initialScalar, 0);
        TestAssert.True(fixture.Editor.RandomiseCommand.CanExecute(null), "The fixture was not eligible for a normal Randomise command.");

        var expectedTextures = DonorPaths(fixture.Catalog, 101);
        var pendingRoll = fixture.Editor.RandomiseAsync();
        await fixture.Loader.WaitForPendingPathAsync(expectedTextures);

        TestAssert.True(fixture.Editor.SetToDefaultsCommand.CanExecute(null),
            "Set to Defaults became disabled while texture preparation was pending.");
        fixture.Editor.SetToDefaultsCommand.Execute(null);
        AssertResetState(fixture.Editor);

        await CompleteRequestAsync(fixture.Loader, expectedTextures);
        await pendingRoll;

        AssertResetState(fixture.Editor);
        TestAssert.True(fixture.Editor.UndoCommand.CanExecute(null), "Reset did not create its expected history entry.");
        fixture.Editor.UndoCommand.Execute(null);
        AssertInitialState(fixture.Editor, initialScalar);
        TestAssert.True(!fixture.Editor.UndoCommand.CanExecute(null),
            "The discarded randomisation left an extra Undo entry after undoing Reset.");
    });

    private static void OlderRollCannotOverwriteNewer(bool cursedMode) => RunWithDispatcher(async () =>
    {
        using var fixture = CreateFixture([OlderSeed, NewerSeed], cursedMode);
        var initialScalar = GetScalar(fixture.Editor);
        var olderTextures = DonorPaths(fixture.Catalog, OlderSeed);
        var newerTextures = DonorPaths(fixture.Catalog, NewerSeed);
        TestAssert.True(!olderTextures.SetEquals(newerTextures), "The deterministic seeds selected the same donor pair.");
        TestAssert.True(fixture.Editor.RandomiseCommand.CanExecute(null), "The fixture was not eligible for a normal Randomise command.");

        var olderRoll = fixture.Editor.RandomiseAsync();
        await fixture.Loader.WaitForPendingPathAsync(olderTextures);
        TestAssert.True(fixture.Editor.RandomiseCommand.CanExecute(null),
            "Randomise became disabled while the first roll was awaiting texture preparation.");
        var newerRoll = fixture.Editor.RandomiseAsync();
        await fixture.Loader.WaitForPendingPathAsync(newerTextures);

        await CompleteRequestAsync(fixture.Loader, newerTextures);
        await newerRoll;
        AssertTextures(fixture.Editor, newerTextures);

        await CompleteRequestAsync(fixture.Loader, olderTextures);
        await olderRoll;
        AssertTextures(fixture.Editor, newerTextures);

        TestAssert.True(fixture.Editor.UndoCommand.CanExecute(null), "The newer roll did not create an Undo entry.");
        fixture.Editor.UndoCommand.Execute(null);
        AssertInitialState(fixture.Editor, initialScalar);
        TestAssert.True(!fixture.Editor.UndoCommand.CanExecute(null),
            "The stale older roll left an extra Undo entry after undoing the newer roll.");
    });

    private static Fixture CreateFixture(int[] seeds, bool cursedMode)
    {
        var mesh = TestFixtures.CreateMesh();
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source, null, [], [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty, [mesh.Positions.ToArray()], []);
        var materialIdentity = TestFixtures.CreateIdentity("HeadMaterial", "MaterialInstanceConstant");
        var resolvedMaterial = new ResolvedHeadMaterial(
            "probe-material", materialIdentity, "BIOG_HMM_HED_PROMorph", HeadMaterialFamily.Skin,
            HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["HED_Norm_Blend"] = 2 },
            new Dictionary<string, Vector4>(), new Dictionary<string, MaterialTextureBinding>())
        {
            DefaultScalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["HED_Norm_Blend"] = 2 },
            SupportedScalars = new HashSet<string>(["HED_Norm_Blend"], StringComparer.OrdinalIgnoreCase),
            SupportedTextures = new HashSet<string>(["HED_Diff", "HED_Norm"], StringComparer.OrdinalIgnoreCase)
        };
        var materialSession = new MaterialEditingSession(
            new MorphFaceMaterialOverrides(null, [new ScalarMaterialOverride("HED_Norm_Blend", 6)], [], []),
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [resolvedMaterial.Key] = resolvedMaterial
            }));
        var catalog = CreateCatalog();
        var loader = new ControlledTextureLoader();
        var textureCandidates = catalog.Corpus.Pools.Values.SelectMany(value => value)
            .SelectMany(value => value.MaterialTextureFamilies.Values)
            .SelectMany(value => value.Values)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((path, index) => new PackageAssetListItem(new AssetIdentity(
                "probe-donors.pcc", path, 100 + index, "Texture2D")))
            .ToArray();
        var reader = new MorphFacePackageReader();
        var references = new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService());
        var session = new MorphFaceEditingSession(face, mesh, []);
        var seedIndex = 0;
        var editor = new FaceEditorViewModel(session, new HumanMaleFeatureMetadataCatalog(), materialSession,
            new NullColorDialog(), references, "probe.pcc", textureCandidates,
            [], null, [], _ => { }, ProfileKey, catalog,
            () => seeds[Math.Min(seedIndex++, seeds.Length - 1)]);

        // Replace only the decode boundary; selection, eligibility, command/core execution and history stay real.
        foreach (var texture in editor.Material.Textures)
            typeof(MaterialTextureEditorViewModel).GetField("_references", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(texture, loader);

        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 0;
        editor.CursedMode = cursedMode;
        if (!editor.RandomiseCommand.CanExecute(null))
        {
            editor.Dispose();
            reader.Dispose();
            throw new InvalidOperationException("The editor fixture did not pass the normal Randomise command eligibility check.");
        }
        return new Fixture(editor, catalog, loader, reader);
    }

    private static MorphRandomisationCatalog CreateCatalog()
    {
        MorphRandomisationDonor Donor(string id, string stem) => new(
            id, ProfileKey, new HashSet<string>(), new Dictionary<string, float>())
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [TextureFamily] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HED_Diff"] = $"BIOG_MFE_Publication.Textures.{stem}_Diff",
                    ["HED_Norm"] = $"BIOG_MFE_Publication.Textures.{stem}_Norm"
                }
            }
        };
        var corpus = new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
            {
                [MorphRandomisationPoolKey.HumanMaleLe12] = [Donor("donor-A", "A"), Donor("donor-B", "B")]
            })
        {
            MaterialProfiles = new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [ProfileKey] = new MaterialRandomisationProfile(ProfileKey,
                    new Dictionary<string, MaterialScalarStatistics>(),
                    new Dictionary<string, MaterialVectorStatistics>(),
                    new HashSet<string>([TextureFamily], StringComparer.OrdinalIgnoreCase))
            }
        };
        return new MorphRandomisationCatalog(corpus);
    }

    private static HashSet<string> DonorPaths(MorphRandomisationCatalog catalog, int seed) =>
        catalog.SelectMaterialDonor(ProfileKey, seed).MaterialTextureFamilies[TextureFamily].Values
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task CompleteRequestAsync(ControlledTextureLoader loader,
        IReadOnlySet<string> paths)
    {
        var remaining = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var path = await loader.WaitForPendingPathAsync(remaining);
            loader.Complete(path);
            remaining.Remove(path);
        }
    }

    private static void AssertResetState(FaceEditorViewModel editor)
    {
        TestAssert.Near(2, GetScalar(editor), 0);
        TestAssert.Equal(0, TexturePaths(editor).Length);
    }

    private static void AssertInitialState(FaceEditorViewModel editor, float initialScalar)
    {
        TestAssert.Near(initialScalar, GetScalar(editor), 0);
        TestAssert.Equal(0, TexturePaths(editor).Length);
        TestAssert.True(editor.CreateDraft().MaterialOverrides.Scalars
                .Any(value => value.Name.Equals("HED_Norm_Blend", StringComparison.OrdinalIgnoreCase) && value.Value == initialScalar),
            "Undo did not restore the authored scalar override.");
    }

    private static void AssertTextures(FaceEditorViewModel editor, IReadOnlySet<string> expected)
    {
        var actual = TexturePaths(editor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TestAssert.True(actual.SetEquals(expected), $"Expected texture pair [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private static string[] TexturePaths(FaceEditorViewModel editor) => editor.CreateDraft().MaterialOverrides.Textures
        .Where(value => value.TextureReference is not null)
        .Select(value => value.TextureReference!.InstancedPath)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static float GetScalar(FaceEditorViewModel editor) =>
        editor.Material.Scalars.Single(value => value.Name.Equals("HED_Norm_Blend", StringComparison.OrdinalIgnoreCase)).Value;

    private static void RunWithDispatcher(Func<Task> action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Task task;
            try
            {
                task = action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                return;
            }
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted)
                    failure = ExceptionDispatchInfo.Capture(completed.Exception!.GetBaseException());
                else if (completed.IsCanceled)
                    failure = ExceptionDispatchInfo.Capture(new TaskCanceledException(completed));
                dispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
            }, TaskScheduler.Default);
            try { Dispatcher.Run(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private sealed class Fixture(FaceEditorViewModel editor, MorphRandomisationCatalog catalog,
        ControlledTextureLoader loader, MorphFacePackageReader reader) : IDisposable
    {
        public FaceEditorViewModel Editor { get; } = editor;
        public MorphRandomisationCatalog Catalog { get; } = catalog;
        public ControlledTextureLoader Loader { get; } = loader;
        public void Dispose()
        {
            Editor.Dispose();
            reader.Dispose();
        }
    }

    private sealed class ControlledTextureLoader : ITextureReferenceLoader
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<DecodedTextureAsset>> _pending =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(IReadOnlySet<string> Paths, TaskCompletionSource<string> Completion)> _waiters = [];

        public Task<DecodedTextureAsset> LoadTextureAsync(string packagePath, string texturePath,
            MaterialParameterDefinition definition, CancellationToken cancellationToken = default)
        {
            _ = packagePath;
            _ = cancellationToken;
            var source = new TaskCompletionSource<DecodedTextureAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (!_pending.TryAdd(texturePath, source))
                    throw new InvalidOperationException($"Duplicate pending texture request '{texturePath}'.");
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    if (!_waiters[index].Paths.Contains(texturePath)) continue;
                    _waiters[index].Completion.TrySetResult(texturePath);
                    _waiters.RemoveAt(index);
                }
            }
            return source.Task;
        }

        public Task<string> WaitForPendingPathAsync(IReadOnlySet<string> paths)
        {
            lock (_gate)
            {
                var pendingPath = paths.FirstOrDefault(path =>
                    _pending.TryGetValue(path, out var request) && !request.Task.IsCompleted);
                if (pendingPath is not null) return Task.FromResult(pendingPath);
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((paths, completion));
                return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public void Complete(string path)
        {
            TaskCompletionSource<DecodedTextureAsset> source;
            lock (_gate)
            {
                if (!_pending.TryGetValue(path, out source!))
                    throw new InvalidOperationException($"No pending decode exists for '{path}'.");
            }
            var parameterName = path.EndsWith("_Norm", StringComparison.OrdinalIgnoreCase) ? "HED_Norm" : "HED_Diff";
            var definition = HumanMaterialProfiles.Describe(parameterName,
                MaterialParameterKind.Texture, HeadMaterialFamily.Skin);
            source.TrySetResult(new DecodedTextureAsset(
                new AssetIdentity("probe-donors.pcc", path, 42, "Texture2D"), 1, 1,
                [64, 128, 192, 255], "PF_B8G8R8A8", definition.TextureRole,
                definition.ColorSpace, definition.AlphaPolicy, false, path));
        }
    }

    private sealed class NullColorDialog : IHdrColorDialogService
    {
        public bool ExtendedSliders { get; set; }
        public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview) => null;
        public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview) => null;
    }
}
