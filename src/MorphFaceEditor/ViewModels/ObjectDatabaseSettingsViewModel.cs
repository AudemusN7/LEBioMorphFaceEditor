using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.ViewModels;

/// <summary>Provides detached ObjectInstanceDB status rows for the settings window.</summary>
public sealed class ObjectDatabaseSettingsViewModel : ObservableObject
{
    private readonly ObjectDatabaseProvider _provider;
    private readonly ObjectDatabaseBuilder _builder;
    private CancellationTokenSource? _buildCancellation;
    private bool _isBuilding;
    private bool _isRebuildingAll;
    private int _packagesProcessed;
    private int _totalPackages;

    public ObjectDatabaseSettingsViewModel(
        ObjectDatabaseProvider provider,
        ObjectDatabaseBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(builder);
        _provider = provider;
        _builder = builder;
        Rows =
        [
            new ObjectDatabaseSettingsRowViewModel(MorphFaceGame.LE1, provider.GetStatus(MorphFaceGame.LE1)),
            new ObjectDatabaseSettingsRowViewModel(MorphFaceGame.LE2, provider.GetStatus(MorphFaceGame.LE2)),
            new ObjectDatabaseSettingsRowViewModel(MorphFaceGame.LE3, provider.GetStatus(MorphFaceGame.LE3))
        ];
    }

    public IReadOnlyList<ObjectDatabaseSettingsRowViewModel> Rows { get; }
    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (SetProperty(ref _isBuilding, value))
            {
                OnPropertyChanged(nameof(ActionLabel));
                OnPropertyChanged(nameof(RebuildAllActionLabel));
                OnPropertyChanged(nameof(IsRebuildAllActionVisible));
            }
        }
    }
    public string ActionLabel => IsBuilding ? "Cancel" : "Rebuild";
    public string RebuildAllActionLabel => IsBuilding ? "Cancel" : "Rebuild All";
    public bool IsRebuildAllActionVisible => !IsBuilding || _isRebuildingAll;
    public int PackagesProcessed { get => _packagesProcessed; private set => SetProperty(ref _packagesProcessed, value); }
    public int TotalPackages { get => _totalPackages; private set => SetProperty(ref _totalPackages, value); }
    public double ProgressPercent => TotalPackages == 0 ? 0 : 100d * PackagesProcessed / TotalPackages;
    public string ProgressLabel => TotalPackages == 0 ? "Preparing rebuild…" : $"{PackagesProcessed:N0} / {TotalPackages:N0} packages";

    public void Refresh()
    {
        foreach (var row in Rows)
        {
            row.Update(_provider.GetStatus(row.Game));
        }
    }

    public async Task RebuildAsync(MorphFaceGame game, CancellationToken cancellationToken = default)
    {
        if (IsBuilding)
        {
            CancelBuild();
            return;
        }
        BeginBuild(cancellationToken);
        _isRebuildingAll = false;
        OnPropertyChanged(nameof(RebuildAllActionLabel));
        OnPropertyChanged(nameof(IsRebuildAllActionVisible));
        SetBuildActionVisibility(game);
        var row = Rows.Single(value => value.Game == game);
        row.Update(new ObjectDatabaseStatus(game, ObjectDatabaseState.Building, null, null, null, null, null));
        try
        {
            var status = await _builder.RebuildAsync(game, CreateProgress(), _buildCancellation!.Token);
            row.Update(status);
        }
        catch (OperationCanceledException)
        {
            row.Update(new ObjectDatabaseStatus(game, ObjectDatabaseState.Cancelled, null, null, null, null, null));
        }
        finally { EndBuild(); }
    }

    public async Task RebuildAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsBuilding)
        {
            CancelBuild();
            return;
        }
        BeginBuild(cancellationToken);
        _isRebuildingAll = true;
        OnPropertyChanged(nameof(RebuildAllActionLabel));
        OnPropertyChanged(nameof(IsRebuildAllActionVisible));
        SetBuildActionVisibility(null);
        foreach (var row in Rows)
        {
            row.Update(new ObjectDatabaseStatus(row.Game, ObjectDatabaseState.Building, null, null, null, null, null));
        }

        try
        {
            var statuses = await _builder.RebuildAllAsync(CreateProgress(), _buildCancellation!.Token);
            foreach (var status in statuses)
                Rows.Single(row => row.Game == status.Game).Update(status);
        }
        catch (OperationCanceledException)
        {
            foreach (var row in Rows.Where(row => row.Status.State == ObjectDatabaseState.Building))
                row.Update(new ObjectDatabaseStatus(row.Game, ObjectDatabaseState.Cancelled, null, null, null, null, null));
        }
        finally { EndBuild(); }
    }

    public void BeginBuild(CancellationToken cancellationToken = default)
    {
        _buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PackagesProcessed = TotalPackages = 0;
        IsBuilding = true;
    }

    public void CancelBuild() => _buildCancellation?.Cancel();

    private void EndBuild()
    {
        _buildCancellation?.Dispose();
        _buildCancellation = null;
        _isRebuildingAll = false;
        SetBuildActionVisibility(activeGame: null, building: false);
        IsBuilding = false;
    }

    private void SetBuildActionVisibility(MorphFaceGame? activeGame, bool building = true)
    {
        foreach (var row in Rows)
        {
            row.SetBuildActionVisible(!building || row.Game == activeGame);
        }
    }

    private IProgress<ObjectDatabaseProgress> CreateProgress() => new Progress<ObjectDatabaseProgress>(progress =>
    {
        PackagesProcessed = progress.PackagesProcessed;
        TotalPackages = progress.TotalPackages;
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressLabel));
    });
}

/// <summary>Formats one game's OIDB status for WPF while retaining the full detached status for commands.</summary>
public sealed class ObjectDatabaseSettingsRowViewModel : ObservableObject
{
    private ObjectDatabaseStatus _status;
    private bool _isBuildActionVisible = true;

    public ObjectDatabaseSettingsRowViewModel(MorphFaceGame game, ObjectDatabaseStatus status)
    {
        Game = game;
        _status = status;
    }

    public MorphFaceGame Game { get; }
    public bool IsBuildActionVisible
    {
        get => _isBuildActionVisible;
        private set => SetProperty(ref _isBuildActionVisible, value);
    }
    public string GameLabel => Game.ToString();
    public ObjectDatabaseStatus Status => _status;
    public string StatusLabel => _status.State switch
    {
        ObjectDatabaseState.Ready => "Ready",
        ObjectDatabaseState.Building => "Building",
        ObjectDatabaseState.Cancelled => "Cancelled",
        ObjectDatabaseState.Failed => "Failed",
        _ => "Missing"
    };
    public string SourceLabel => _status.Source switch
    {
        ObjectDatabaseSource.MorphFaceEditor => "MFE",
        ObjectDatabaseSource.LegendaryExplorer => "Legendary Explorer",
        _ => "—"
    };
    public string StatusColour => _status.State switch
    {
        ObjectDatabaseState.Ready => "#63C174",
        ObjectDatabaseState.Building or ObjectDatabaseState.Cancelled => "#F0B657",
        _ => "#E36D6D"
    };
    public string SchemaLabel => _status.SchemaVersion is { } version ? $"v{version}" : "—";
    public string FileSizeLabel => _status.FileSize is { } size ? FormatFileSize(size) : "—";
    public string LastBuiltLabel => _status.LastWriteTime is { } timestamp
        ? timestamp.LocalDateTime.ToString("g")
        : "—";

    public void Update(ObjectDatabaseStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(StatusColour));
        OnPropertyChanged(nameof(SchemaLabel));
        OnPropertyChanged(nameof(FileSizeLabel));
        OnPropertyChanged(nameof(LastBuiltLabel));
    }

    public void SetBuildActionVisible(bool visible) => IsBuildActionVisible = visible;

    private static string FormatFileSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024d:0.0} KiB",
        _ => $"{size / 1024d / 1024d:0.0} MiB"
    };
}
