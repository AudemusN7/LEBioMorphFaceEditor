using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class TextureRegistrySettingsTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture registry settings: row exposes user-facing state", RowExposesUserFacingState),
        new("texture registry settings: traffic lights distinguish outdated and failed", TrafficLightsDistinguishStates),
        new("texture registry settings: progress labels expose every phase", ProgressLabelsExposeEveryPhase),
        new("texture registry settings: individual build hides unrelated actions", IndividualBuildHidesUnrelatedActions)
    ];

    private static void RowExposesUserFacingState()
    {
        var timestamp = new DateTimeOffset(2026, 8, 28, 3, 19, 0, TimeSpan.Zero);
        var row = new TextureRegistrySettingsRowViewModel(
            MorphFaceGame.LE1,
            new TextureRegistryStatus(MorphFaceGame.LE1, TextureRegistryState.Ready,
                "LE1.mftr", 128, timestamp, 1026, null));

        TestAssert.Equal("LE1", row.GameLabel);
        TestAssert.Equal("MorphFace Editor", row.SourceLabel);
        TestAssert.Equal("Ready", row.StatusLabel);
        TestAssert.Equal(timestamp.LocalDateTime.ToString("g"), row.LastBuiltLabel);
    }

    private static void TrafficLightsDistinguishStates()
    {
        var outdated = Row(TextureRegistryState.Outdated);
        var failed = Row(TextureRegistryState.Failed);

        TestAssert.Equal("#F0B657", outdated.StatusColour);
        TestAssert.Equal("#E36D6D", failed.StatusColour);
    }

    private static void ProgressLabelsExposeEveryPhase()
    {
        var settings = CreateSettings(new FakeBuilder());

        settings.UpdateProgress(new TextureRegistryBuildProgress(
            MorphFaceGame.LE2, TextureRegistryBuildPhase.ScanningPackages, 2418, 6594, "BioG.pcc", 700));
        TestAssert.Equal("Scanning packages — 2,418 / 6,594", settings.ProgressLabel);
        settings.UpdateProgress(new TextureRegistryBuildProgress(
            MorphFaceGame.LE2, TextureRegistryBuildPhase.WritingRegistry, 6594, 6594, null, 1026));
        TestAssert.Equal("Writing compact registry", settings.ProgressLabel);
        settings.UpdateProgress(new TextureRegistryBuildProgress(
            MorphFaceGame.LE2, TextureRegistryBuildPhase.VerifyingRegistry, 6594, 6594, null, 1026));
        TestAssert.Equal("Verifying registry", settings.ProgressLabel);
        settings.UpdateProgress(new TextureRegistryBuildProgress(
            MorphFaceGame.LE2, TextureRegistryBuildPhase.Ready, 6594, 6594, null, 1026));
        TestAssert.Equal("Ready — 1,026 textures", settings.ProgressLabel);
    }

    private static void IndividualBuildHidesUnrelatedActions()
    {
        var builder = new FakeBuilder(block: true);
        var settings = CreateSettings(builder);

        var build = settings.RebuildAsync(MorphFaceGame.LE1);
        TestAssert.True(settings.IsBuilding, "The settings view did not enter building state.");
        TestAssert.True(!settings.IsRebuildAllActionVisible, "Rebuild All remained visible during an individual build.");
        TestAssert.True(settings.Rows.Single(row => row.Game == MorphFaceGame.LE1).IsBuildActionVisible,
            "The active game's Cancel action was hidden.");
        TestAssert.True(settings.Rows.Where(row => row.Game != MorphFaceGame.LE1)
                .All(row => !row.IsBuildActionVisible),
            "An unrelated game's action remained visible during the active build.");

        settings.CancelBuild();
        builder.Release();
        try { build.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
    }

    private static TextureRegistrySettingsRowViewModel Row(TextureRegistryState state) => new(
        MorphFaceGame.LE3,
        new TextureRegistryStatus(MorphFaceGame.LE3, state, "LE3.mftr", 1,
            DateTimeOffset.UtcNow, 1, state == TextureRegistryState.Failed ? "bad payload" : null));

    private static TextureRegistrySettingsViewModel CreateSettings(ITextureRegistryBuilder builder)
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-RegistrySettings-{Guid.NewGuid():N}");
        return new TextureRegistrySettingsViewModel(
            new TextureRegistryStore(new TextureRegistryPaths(root)), builder);
    }

    private sealed class FakeBuilder(bool block = false) : ITextureRegistryBuilder
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TextureRegistryStatus> RebuildAsync(
            MorphFaceGame game,
            IProgress<TextureRegistryBuildProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (block) await _release.Task.WaitAsync(cancellationToken);
            return new TextureRegistryStatus(game, TextureRegistryState.Ready,
                $"{game}.mftr", 1, DateTimeOffset.UtcNow, 1, null);
        }

        public async Task<IReadOnlyList<TextureRegistryStatus>> RebuildAllAsync(
            IProgress<TextureRegistryBuildProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<TextureRegistryStatus>();
            foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
                results.Add(await RebuildAsync(game, progress, cancellationToken));
            return results;
        }

        public void Release() => _release.TrySetResult();
    }
}
