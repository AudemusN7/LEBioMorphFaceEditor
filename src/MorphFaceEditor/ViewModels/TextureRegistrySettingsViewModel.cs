using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.ViewModels;

/// <summary>Owns visible status, progress, and cancellation for explicit per-game registry builds.</summary>
public sealed class TextureRegistrySettingsViewModel : ObservableObject
{
    private readonly TextureRegistryStore _store;
    private readonly ITextureRegistryBuilder _builder;
    private readonly Action<MorphFaceGame>? _catalogInvalidator;
    private CancellationTokenSource? _buildCancellation;
    private bool _isBuilding;
    private bool _isRebuildingAll;
    private int _packagesProcessed;
    private int _totalPackages;
    private string _progressLabel = "Preparing build…";

    public TextureRegistrySettingsViewModel(
        TextureRegistryStore store,
        ITextureRegistryBuilder builder,
        Action<MorphFaceGame>? catalogInvalidator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _catalogInvalidator = catalogInvalidator;
        Rows =
        [
            CreateCheckingRow(MorphFaceGame.LE1),
            CreateCheckingRow(MorphFaceGame.LE2),
            CreateCheckingRow(MorphFaceGame.LE3)
        ];
    }

    public IReadOnlyList<TextureRegistrySettingsRowViewModel> Rows { get; }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (!SetProperty(ref _isBuilding, value)) return;
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(RebuildAllActionLabel));
            OnPropertyChanged(nameof(IsRebuildAllActionVisible));
            OnPropertyChanged(nameof(CanClose));
        }
    }

    public string ActionLabel => IsBuilding ? "Cancel" : "Rebuild";
    public string RebuildAllActionLabel => IsBuilding ? "Cancel" : "Rebuild All";
    public bool IsRebuildAllActionVisible => !IsBuilding || _isRebuildingAll;
    public bool CanClose => !IsBuilding;
    public int PackagesProcessed { get => _packagesProcessed; private set => SetProperty(ref _packagesProcessed, value); }
    public int TotalPackages { get => _totalPackages; private set => SetProperty(ref _totalPackages, value); }
    public double ProgressPercent => TotalPackages == 0 ? 0 : 100d * PackagesProcessed / TotalPackages;
    public string ProgressLabel { get => _progressLabel; private set => SetProperty(ref _progressLabel, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var statuses = await Task.WhenAll(Rows.Select(row =>
            Task.Run(() => _store.GetStatus(row.Game), cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var status in statuses)
            Rows.Single(row => row.Game == status.Game).Update(status);
    }

    public async Task RebuildAsync(MorphFaceGame game, CancellationToken cancellationToken = default)
    {
        if (IsBuilding)
        {
            CancelBuild();
            return;
        }

        BeginBuild(cancellationToken, rebuildingAll: false, activeGame: game);
        var row = Rows.Single(value => value.Game == game);
        var previous = row.Status;
        row.Update(previous with { State = TextureRegistryState.Building, ErrorMessage = null });
        try
        {
            var status = await _builder.RebuildAsync(game, CreateProgress(), _buildCancellation!.Token);
            row.Update(status);
            _catalogInvalidator?.Invoke(game);
        }
        catch (OperationCanceledException)
        {
            row.Update(previous with { State = TextureRegistryState.Cancelled, ErrorMessage = null });
        }
        catch (Exception exception)
        {
            AppLog.Error($"Texture registry build failed for {game}.", exception);
            row.Update(previous with { State = TextureRegistryState.Failed, ErrorMessage = exception.Message });
        }
        finally
        {
            EndBuild();
        }
    }

    public async Task RebuildAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsBuilding)
        {
            CancelBuild();
            return;
        }

        BeginBuild(cancellationToken, rebuildingAll: true, activeGame: null);
        try
        {
            foreach (var row in Rows)
            {
                var previous = row.Status;
                row.Update(previous with { State = TextureRegistryState.Building, ErrorMessage = null });
                try
                {
                    var status = await _builder.RebuildAsync(row.Game, CreateProgress(), _buildCancellation!.Token);
                    row.Update(status);
                    _catalogInvalidator?.Invoke(row.Game);
                }
                catch (OperationCanceledException)
                {
                    row.Update(previous with { State = TextureRegistryState.Cancelled, ErrorMessage = null });
                    break;
                }
                catch (Exception exception)
                {
                    AppLog.Error($"Texture registry build failed for {row.Game} during Rebuild All.", exception);
                    row.Update(previous with { State = TextureRegistryState.Failed, ErrorMessage = exception.Message });
                    break;
                }
            }
        }
        finally
        {
            EndBuild();
        }
    }

    public void CancelBuild() => _buildCancellation?.Cancel();

    internal void UpdateProgress(TextureRegistryBuildProgress progress)
    {
        PackagesProcessed = progress.PackagesProcessed;
        TotalPackages = progress.TotalPackages;
        ProgressLabel = progress.Phase switch
        {
            TextureRegistryBuildPhase.ScanningPackages =>
                $"Scanning packages — {progress.PackagesProcessed:N0} / {progress.TotalPackages:N0}",
            TextureRegistryBuildPhase.WritingRegistry => "Writing compact registry",
            TextureRegistryBuildPhase.VerifyingRegistry => "Verifying registry",
            TextureRegistryBuildPhase.Ready => $"Ready — {progress.TextureCount:N0} textures",
            _ => "Preparing build…"
        };
        OnPropertyChanged(nameof(ProgressPercent));

        if (_isRebuildingAll)
        {
            var row = Rows.Single(value => value.Game == progress.Game);
            if (row.Status.State != TextureRegistryState.Building)
                row.Update(row.Status with { State = TextureRegistryState.Building, ErrorMessage = null });
        }
    }

    private void BeginBuild(CancellationToken cancellationToken, bool rebuildingAll, MorphFaceGame? activeGame)
    {
        _buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRebuildingAll = rebuildingAll;
        PackagesProcessed = TotalPackages = 0;
        ProgressLabel = "Preparing build…";
        SetBuildActionVisibility(activeGame, building: true);
        IsBuilding = true;
        OnPropertyChanged(nameof(IsRebuildAllActionVisible));
    }

    private void EndBuild()
    {
        _buildCancellation?.Dispose();
        _buildCancellation = null;
        _isRebuildingAll = false;
        SetBuildActionVisibility(activeGame: null, building: false);
        IsBuilding = false;
    }

    private void SetBuildActionVisibility(MorphFaceGame? activeGame, bool building)
    {
        foreach (var row in Rows)
            row.SetBuildActionVisible(!building || row.Game == activeGame);
    }

    private IProgress<TextureRegistryBuildProgress> CreateProgress() =>
        new Progress<TextureRegistryBuildProgress>(UpdateProgress);

    private static TextureRegistrySettingsRowViewModel CreateCheckingRow(MorphFaceGame game) => new(
        game,
        new TextureRegistryStatus(game, TextureRegistryState.Checking,
            null, null, null, null, null));
}

public sealed class TextureRegistrySettingsRowViewModel : ObservableObject
{
    private TextureRegistryStatus _status;
    private bool _isBuildActionVisible = true;

    public TextureRegistrySettingsRowViewModel(MorphFaceGame game, TextureRegistryStatus status)
    {
        Game = game;
        _status = status;
    }

    public MorphFaceGame Game { get; }
    public string GameLabel => Game.ToString();
    public string SourceLabel => "MorphFace Editor";
    public TextureRegistryStatus Status => _status;
    public bool IsBuildActionVisible
    {
        get => _isBuildActionVisible;
        private set => SetProperty(ref _isBuildActionVisible, value);
    }
    public string StatusLabel => _status.State switch
    {
        TextureRegistryState.Checking => "Checking",
        TextureRegistryState.Ready => "Ready",
        TextureRegistryState.Building => "Building",
        TextureRegistryState.Outdated => "Outdated",
        TextureRegistryState.Cancelled => "Cancelled",
        TextureRegistryState.Failed => "Failed",
        _ => "Missing"
    };
    public string StatusColour => _status.State switch
    {
        TextureRegistryState.Ready => "#63C174",
        TextureRegistryState.Checking or TextureRegistryState.Building or
            TextureRegistryState.Outdated or TextureRegistryState.Cancelled => "#F0B657",
        _ => "#E36D6D"
    };
    public string LastBuiltLabel => _status.LastBuilt is { } timestamp
        ? timestamp.LocalDateTime.ToString("g")
        : "—";

    public void Update(TextureRegistryStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusColour));
        OnPropertyChanged(nameof(LastBuiltLabel));
    }

    public void SetBuildActionVisible(bool visible) => IsBuildActionVisible = visible;
}
