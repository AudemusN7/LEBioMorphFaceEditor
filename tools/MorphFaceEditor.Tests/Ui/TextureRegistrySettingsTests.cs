using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class TextureRegistrySettingsTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("registry rebuild retains custom assets through Keep/cancel and removes them only on Discard", RebuildPreservesCustomDataAndReleasesCancellation),
        new("registry mods-only action preserves official and manually selected data", ModsOnlyActionPreservesOtherSources),
        new("registry mods-only cancellation and failure leave official status available", ModsOnlyCancellationAndFailureStayOnCustomStatus),
        new("registry stale indicators stay independent without disabling the database", StaleIndicatorsRemainAvailable)
    ];

    private static void RebuildPreservesCustomDataAndReleasesCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ManualSettings-{Guid.NewGuid():N}");
        try
        {
            var paths = new TextureRegistryPaths(root);
            var store = new TextureRegistryStore(paths);
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

            var unsupportedManual = File.ReadAllBytes(paths.GetManualPath(MorphFaceGame.LE1));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                unsupportedManual.AsSpan(sizeof(uint), sizeof(int)), TextureRegistrySnapshot.CurrentSchemaVersion - 1);
            File.WriteAllBytes(paths.GetManualPath(MorphFaceGame.LE1), unsupportedManual);
            settings = new TextureRegistrySettingsViewModel(store, new FakeBuilder());
            var changed = false;
            settings.DatabaseChanged += _ => changed = true;
            settings.RebuildAsync(MorphFaceGame.LE1, keepManualAssets: true).GetAwaiter().GetResult();
            var row = settings.Rows.Single(value => value.Game == MorphFaceGame.LE1);
            TestAssert.True(row.Status.State == TextureRegistryState.Ready &&
                            row.CustomStatus?.State == TextureRegistryState.Outdated && changed &&
                            unsupportedManual.SequenceEqual(File.ReadAllBytes(paths.GetManualPath(MorphFaceGame.LE1))),
                "Keeping an unsupported manual sidecar blocked the official build or changed the old file.");

            settings = new TextureRegistrySettingsViewModel(store, new FakeBuilder());
            settings.RebuildAsync(MorphFaceGame.LE1, keepManualAssets: false).GetAwaiter().GetResult();
            TestAssert.True(!store.HasManual(MorphFaceGame.LE1), "Discard retained the custom sidecar.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void ModsOnlyActionPreservesOtherSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ModSettings-{Guid.NewGuid():N}");
        try
        {
            var paths = new TextureRegistryPaths(root);
            var store = new TextureRegistryStore(paths);
            Directory.CreateDirectory(root);
            var officialSource = Path.Combine(root, "official.pcc");
            File.WriteAllBytes(officialSource, [1]);
            store.WriteAtomic(new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
                MorphFaceGame.LE2, DateTimeOffset.UtcNow, 1, [])
            {
                MountedPackagePaths = [officialSource],
                MountedPackages = [MountedPackageFingerprint.Read(officialSource)]
            });
            store.WriteManualAtomic(new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
                MorphFaceGame.LE2, DateTimeOffset.UtcNow, 0, [])
            {
                ManualAssets = [new ManualRegistryAsset(Path.Combine(root, "missing.pcc"), 2,
                    "Hair.HMM_HIR_Custom_MDL", "SkeletalMesh")]
            });
            var officialBytes = File.ReadAllBytes(paths.GetPath(MorphFaceGame.LE2));
            var manualBytes = File.ReadAllBytes(paths.GetManualPath(MorphFaceGame.LE2));
            var builder = new FakeBuilder();
            var invalidated = new List<MorphFaceGame>();
            var changed = new List<MorphFaceGame>();
            var settings = new TextureRegistrySettingsViewModel(store, builder, invalidated.Add);
            settings.DatabaseChanged += changed.Add;
            settings.RefreshAsync().GetAwaiter().GetResult();
            var row = settings.Rows.Single(value => value.Game == MorphFaceGame.LE2);
            TestAssert.True(!row.Status.IsStale && row.RefreshGuidanceLabel.Length == 0,
                "An unchanged official source requested a refresh.");

            File.Delete(officialSource);
            settings.RefreshAsync().GetAwaiter().GetResult();
            TestAssert.True(row.Status.IsStale && row.CustomStatus?.IsStale == false &&
                            row.RefreshGuidanceLabel.Length > 0 && row.WarningLabel.Length == 0,
                "Refreshing Settings missed a removed source or reported freshness as an error.");

            settings.RebuildModsAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();

            TestAssert.True(builder.ModsBuildCount == 1 && builder.FullBuildCount == 0 &&
                            builder.LastGame == MorphFaceGame.LE2,
                "Rescan Mods routed to a full rebuild or the wrong game.");
            TestAssert.True(officialBytes.SequenceEqual(File.ReadAllBytes(paths.GetPath(MorphFaceGame.LE2))) &&
                            manualBytes.SequenceEqual(File.ReadAllBytes(paths.GetManualPath(MorphFaceGame.LE2))),
                "Rescan Mods rewrote official data or revalidated manual selections.");
            TestAssert.True(invalidated.SequenceEqual([MorphFaceGame.LE2]) &&
                            changed.SequenceEqual([MorphFaceGame.LE2]),
                "A completed mod rescan did not publish the catalogue change exactly once.");
            TestAssert.True(row.Status.IsStale && row.CustomStatus?.IsStale == false &&
                            row.RefreshGuidanceLabel.Length > 0 && row.WarningLabel.Length == 0,
                "A mods-only rescan cleared the independent official-source advisory or created an error.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void ModsOnlyCancellationAndFailureStayOnCustomStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ModSettings-Cancel-{Guid.NewGuid():N}");
        try
        {
            var store = new TextureRegistryStore(new TextureRegistryPaths(root));
            store.WriteAtomic(new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
                MorphFaceGame.LE1, DateTimeOffset.UtcNow, 0, []));
            var invalidations = 0;
            var notifications = 0;
            var settings = new TextureRegistrySettingsViewModel(store, new FakeBuilder(block: true),
                _ => invalidations++);
            settings.DatabaseChanged += _ => notifications++;
            settings.RefreshAsync().GetAwaiter().GetResult();
            var row = settings.Rows.Single(value => value.Game == MorphFaceGame.LE1);

            var pending = settings.RebuildModsAsync(MorphFaceGame.LE1);
            TestAssert.True(settings.IsBuilding && !settings.CanClose &&
                            row.Status.State == TextureRegistryState.Ready &&
                            row.CustomStatus?.State == TextureRegistryState.Building &&
                            row.CanRescanMods && row.IsRescanModsActionVisible && !row.IsBuildActionVisible,
                "An active mod rescan changed official status or lost its cancel action.");
            settings.CancelBuild();
            pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            TestAssert.True(settings.CanClose && !settings.IsBuilding &&
                            row.Status.State == TextureRegistryState.Ready &&
                            row.CustomStatus?.State == TextureRegistryState.Cancelled &&
                            row.CanAddCustomAsset && row.CanRescanMods,
                "A cancelled mod rescan disabled the official database or left Settings locked.");

            settings = new TextureRegistrySettingsViewModel(store, new FakeBuilder(fail: true),
                _ => invalidations++);
            settings.DatabaseChanged += _ => notifications++;
            settings.RefreshAsync().GetAwaiter().GetResult();
            settings.RebuildModsAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            row = settings.Rows.Single(value => value.Game == MorphFaceGame.LE1);
            TestAssert.True(row.Status.State == TextureRegistryState.Ready &&
                            row.CustomStatus?.State == TextureRegistryState.Failed && settings.CanClose &&
                            invalidations == 0 && notifications == 0,
                "A failed mod rescan altered official availability or published an unsuccessful change.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void StaleIndicatorsRemainAvailable()
    {
        var ready = new TextureRegistryStatus(MorphFaceGame.LE3, TextureRegistryState.Ready,
            "LE3.mftr", 1, DateTimeOffset.UtcNow, 1, null);
        var row = new TextureRegistrySettingsRowViewModel(MorphFaceGame.LE3, ready with { IsStale = true });
        row.UpdateCustom(ready);
        var freshCustomLabel = row.CustomStatusLabel;
        TestAssert.True(row.Status.State == TextureRegistryState.Ready &&
                        row.StatusBrushKey == "StatusPending" && row.CustomStatusBrushKey == "StatusReady" &&
                        row.CanAddCustomAsset && row.CanRescanMods && row.WarningLabel.Length == 0 &&
                        row.RefreshGuidanceLabel.Length > 0,
            "A stale official database changed availability or marked fresh custom data stale.");

        row.Update(ready);
        var freshOfficialLabel = row.StatusLabel;
        row.UpdateCustom(ready with { IsStale = true });
        TestAssert.True(row.StatusBrushKey == "StatusReady" && row.CustomStatusBrushKey == "StatusPending" &&
                        row.CustomStatus?.State == TextureRegistryState.Ready &&
                        row.CustomStatusLabel != freshCustomLabel && row.CanAddCustomAsset && row.CanRescanMods &&
                        row.WarningLabel.Length == 0 && row.RefreshGuidanceLabel.Length > 0,
            "A stale sidecar affected official status or disabled database actions.");

        row.Update(ready with { IsStale = true });
        TestAssert.True(row.StatusLabel != freshOfficialLabel,
            "The official stale indicator kept its fresh label.");
        row.UpdateCustom(ready with { State = TextureRegistryState.Missing, IsStale = true });
        TestAssert.Equal("StatusPending", row.CustomStatusBrushKey);
        row.UpdateCustom(ready with { State = TextureRegistryState.Failed, ErrorMessage = "probe error" });
        TestAssert.True(row.WarningLabel == Loc.Text("loc_Error_TechnicalWarning") &&
                        row.CustomStatus?.ErrorMessage == "probe error" && row.RefreshGuidanceLabel.Length > 0,
            "A sidecar diagnostic leaked into UI wording or its diagnostic/freshness guidance was lost.");
    }

    private sealed class FakeBuilder(bool block = false, bool fail = false) : ITextureRegistryBuilder
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FullBuildCount { get; private set; }
        public int ModsBuildCount { get; private set; }
        public MorphFaceGame? LastGame { get; private set; }

        public Task<TextureRegistryStatus> RebuildAsync(MorphFaceGame game,
            IProgress<TextureRegistryBuildProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            FullBuildCount++;
            return BuildAsync(game, cancellationToken);
        }

        public Task<TextureRegistryStatus> RebuildModsAsync(MorphFaceGame game,
            IProgress<TextureRegistryBuildProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ModsBuildCount++;
            return BuildAsync(game, cancellationToken);
        }

        private async Task<TextureRegistryStatus> BuildAsync(MorphFaceGame game, CancellationToken cancellationToken)
        {
            LastGame = game;
            if (block) await _release.Task.WaitAsync(cancellationToken);
            if (fail) throw new InvalidDataException("Build failed.");
            return new TextureRegistryStatus(game, TextureRegistryState.Ready,
                $"{game}.mftr", 1, DateTimeOffset.UtcNow, 1, null);
        }

        public Task<IReadOnlyList<TextureRegistryStatus>> RebuildAllAsync(
            IProgress<TextureRegistryBuildProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This contract exercises an individual rebuild.");
    }
}
