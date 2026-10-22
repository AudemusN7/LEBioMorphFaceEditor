using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class TextureRegistrySettingsTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("registry rebuild retains custom assets through Keep/cancel and removes them only on Discard", RebuildPreservesCustomDataAndReleasesCancellation)
    ];

    private static void RebuildPreservesCustomDataAndReleasesCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ManualSettings-{Guid.NewGuid():N}");
        try
        {
            var store = new TextureRegistryStore(new TextureRegistryPaths(root));
            store.WriteManualAtomic(new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
                MorphFaceGame.LE1, DateTimeOffset.UtcNow, 0, [])
            {
                ManualAssets = [new ManualRegistryAsset(Path.Combine(root, "missing.pcc"), 2,
                    "Hair.HMM_HIR_Custom_MDL", "SkeletalMesh")]
            });
            var settings = new TextureRegistrySettingsViewModel(store, new FakeBuilder());
            settings.RebuildAsync(MorphFaceGame.LE1, keepManualAssets: true).GetAwaiter().GetResult();
            TestAssert.True(store.HasManual(MorphFaceGame.LE1) &&
                            store.ReadManual(MorphFaceGame.LE1).ManualAssets.Single().IsMissing &&
                            File.Exists(store.GetManualReportPath(MorphFaceGame.LE1)),
                "Keep removed a custom asset or lost its missing-source report.");

            settings = new TextureRegistrySettingsViewModel(store, new FakeBuilder(block: true));
            var pending = settings.RebuildAsync(MorphFaceGame.LE1);
            TestAssert.True(settings.IsBuilding && !settings.CanClose, "An active build lost its progress authority.");
            settings.CancelBuild();
            pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            TestAssert.True(settings.CanClose && !settings.IsBuilding &&
                            settings.Rows.Single(row => row.Game == MorphFaceGame.LE1).Status.State == TextureRegistryState.Cancelled &&
                            store.HasManual(MorphFaceGame.LE1),
                "Cancellation left the view locked or discarded custom assets.");

            settings = new TextureRegistrySettingsViewModel(store, new FakeBuilder());
            settings.RebuildAsync(MorphFaceGame.LE1, keepManualAssets: false).GetAwaiter().GetResult();
            TestAssert.True(!store.HasManual(MorphFaceGame.LE1), "Discard retained the custom sidecar.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeBuilder(bool block = false) : ITextureRegistryBuilder
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TextureRegistryStatus> RebuildAsync(MorphFaceGame game,
            IProgress<TextureRegistryBuildProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (block) await _release.Task.WaitAsync(cancellationToken);
            return new TextureRegistryStatus(game, TextureRegistryState.Ready,
                $"{game}.mftr", 1, DateTimeOffset.UtcNow, 1, null);
        }

        public Task<IReadOnlyList<TextureRegistryStatus>> RebuildAllAsync(
            IProgress<TextureRegistryBuildProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This contract exercises an individual rebuild.");
    }
}
