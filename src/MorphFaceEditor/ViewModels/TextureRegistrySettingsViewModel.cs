using MorphFaceEditor.Core.Domain;
using System.IO;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.ViewModels;

/// <summary>Owns visible status, progress, and cancellation for explicit per-game registry builds.</summary>
public sealed class TextureRegistrySettingsViewModel : ObservableObject
{
    private readonly TextureRegistryStore _store;
    private readonly TextureRegistryManualAssetService _manualAssets;
    private readonly ITextureRegistryBuilder _builder;
    private readonly Action<MorphFaceGame>? _catalogInvalidator;
    private CancellationTokenSource? _buildCancellation;
    private bool _isBuilding;
    private bool _isRebuildingAll;
    private bool _isRebuildingMods;
    private int _statusGeneration;
    private int _packagesProcessed;
    private int _totalPackages;
    private string _progressLabel = Loc.Text("loc_VM_textureRegistry_preparingBuild");

    public TextureRegistrySettingsViewModel(
        TextureRegistryStore store,
        ITextureRegistryBuilder builder,
        Action<MorphFaceGame>? catalogInvalidator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _manualAssets = new TextureRegistryManualAssetService(_store);
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
    public event Action<MorphFaceGame>? DatabaseChanged;

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (!SetProperty(ref _isBuilding, value)) return;
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ModsActionLabel));
            OnPropertyChanged(nameof(RebuildAllActionLabel));
            OnPropertyChanged(nameof(IsRebuildAllActionVisible));
            OnPropertyChanged(nameof(CanClose));
        }
    }

    public string ActionLabel => IsBuilding ? Loc.Text("loc_VM_textureRegistry_cancel") : Loc.Text("loc_VM_textureRegistry_rebuild");
    public string ModsActionLabel => IsBuilding ? Loc.Text("loc_VM_textureRegistry_cancel") : Loc.Text("loc_VM_textureRegistry_rescanMods");
    public string RebuildAllActionLabel => IsBuilding ? Loc.Text("loc_VM_textureRegistry_cancel") : Loc.Text("loc_VM_textureRegistry_rebuildAll");
    public bool IsRebuildAllActionVisible => !IsBuilding || _isRebuildingAll;
    public bool CanClose => !IsBuilding;
    public int PackagesProcessed { get => _packagesProcessed; private set => SetProperty(ref _packagesProcessed, value); }
    public int TotalPackages { get => _totalPackages; private set => SetProperty(ref _totalPackages, value); }
    public double ProgressPercent => TotalPackages == 0 ? 0 : 100d * PackagesProcessed / TotalPackages;
    public string ProgressLabel { get => _progressLabel; private set => SetProperty(ref _progressLabel, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBuilding) return;
        var generation = _statusGeneration;
        var statuses = await Task.WhenAll(Rows.Select(row =>
            Task.Run(() => (Registry: _store.GetStatus(row.Game),
                Custom: _store.GetCustomStatus(row.Game)), cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _statusGeneration) return;
        foreach (var status in statuses)
        {
            var row = Rows.Single(row => row.Game == status.Registry.Game);
            row.Update(status.Registry);
            row.UpdateCustom(status.Custom);
        }
    }

    public bool HasManualAssets(MorphFaceGame game) => _store.HasManual(game);
    public IReadOnlyList<MorphFaceEditor.Core.Materials.ManualRegistryAsset> GetManualAssets(
        MorphFaceGame game) => _store.GetManualStatus(game).State == TextureRegistryState.Ready
        ? _store.ReadManual(game).ManualAssets
        : [];

    public async Task AppendManualAsync(MorphFaceGame game,
        IReadOnlyList<MorphFaceEditor.Core.Materials.ManualRegistryAsset> selections)
    {
        if (IsBuilding) throw Loc.Error(new InvalidOperationException(Loc.Text("loc_VM_textureRegistry_waitForBuild")));
        _statusGeneration++;
        var status = await Task.Run(() =>
        {
            _manualAssets.Append(game, selections);
            return _store.GetCustomStatus(game);
        });
        Rows.Single(row => row.Game == game).UpdateCustom(status);
        _catalogInvalidator?.Invoke(game);
        DatabaseChanged?.Invoke(game);
    }

    public Task RebuildAsync(MorphFaceGame game, CancellationToken cancellationToken = default,
        bool keepManualAssets = true) => RebuildGameAsync(game, modsOnly: false, keepManualAssets, cancellationToken);

    public Task RebuildModsAsync(MorphFaceGame game, CancellationToken cancellationToken = default) =>
        RebuildGameAsync(game, modsOnly: true, keepManualAssets: true, cancellationToken);

    private async Task RebuildGameAsync(MorphFaceGame game, bool modsOnly, bool keepManualAssets,
        CancellationToken cancellationToken)
    {
        if (IsBuilding)
        {
            CancelBuild();
            return;
        }

        var row = Rows.Single(value => value.Game == game);
        var previous = modsOnly ? row.CustomStatus ?? _store.GetCustomStatus(game) : row.Status;
        BeginBuild(cancellationToken, rebuildingAll: false, activeGame: game, modsOnly);
        UpdateActiveStatus(previous with { State = TextureRegistryState.Building, ErrorMessage = null });
        try
        {
            var status = modsOnly
                ? await _builder.RebuildModsAsync(game, CreateProgress(), _buildCancellation!.Token)
                : await _builder.RebuildAsync(game, CreateProgress(), _buildCancellation!.Token);
            if (!modsOnly && status.State == TextureRegistryState.Ready)
                status = await FinishManualRebuildAsync(game, status, keepManualAssets,
                    _buildCancellation.Token);
            row.Update(modsOnly ? _store.GetStatus(game) : status);
            row.UpdateCustom(modsOnly && status.State != TextureRegistryState.Ready
                ? status : _store.GetCustomStatus(game));
            if (!modsOnly || status.State == TextureRegistryState.Ready)
                _catalogInvalidator?.Invoke(game);
            if (status.State == TextureRegistryState.Ready)
                DatabaseChanged?.Invoke(game);
        }
        catch (OperationCanceledException)
        {
            UpdateActiveStatus(previous with { State = TextureRegistryState.Cancelled, ErrorMessage = null });
        }
        catch (Exception exception)
        {
            AppLog.Error($"REGISTRY:BUILD-FAILED {game} MODE:{(modsOnly ? "MODS" : "FULL")}", exception);
            UpdateActiveStatus(previous with
            {
                State = TextureRegistryState.Failed,
                ErrorMessage = modsOnly ? LocalizedModsBuildError(exception) : Loc.Text("loc_VM_textureRegistry_fullBuildFailed"),
                UserError = modsOnly ? ModsBuildError(exception) : UserFacingMessage.Create("loc_VM_textureRegistry_fullBuildFailed")
            });
        }
        finally
        {
            EndBuild();
        }

        void UpdateActiveStatus(TextureRegistryStatus status)
        {
            if (modsOnly) row.UpdateCustom(status);
            else row.Update(status);
        }
    }

    private static string LocalizedModsBuildError(Exception exception) =>
        UserFacingMessageFormatter.Format(ModsBuildError(exception));

    private static UserFacingMessage ModsBuildError(Exception exception) => UserFacingMessage.Create(exception switch
    {
        TextureRegistryBuildException { Reason: TextureRegistryBuildFailure.OfficialDatabaseRequired } =>
            "loc_VM_textureRegistry_officialDatabaseRequired",
        TextureRegistryBuildException { Reason: TextureRegistryBuildFailure.OfficialDatabaseChanged } =>
            "loc_VM_textureRegistry_officialDatabaseChanged",
        _ => "loc_VM_textureRegistry_modRescanFailed"
    });

    public async Task RebuildAllAsync(CancellationToken cancellationToken = default,
        bool keepManualAssets = true)
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
                    if (status.State == TextureRegistryState.Ready)
                        status = await FinishManualRebuildAsync(row.Game, status, keepManualAssets,
                            _buildCancellation.Token);
                    row.Update(status);
                    row.UpdateCustom(_store.GetCustomStatus(row.Game));
                    _catalogInvalidator?.Invoke(row.Game);
                    if (status.State == TextureRegistryState.Ready)
                        DatabaseChanged?.Invoke(row.Game);
                }
                catch (OperationCanceledException)
                {
                    row.Update(previous with { State = TextureRegistryState.Cancelled, ErrorMessage = null });
                    break;
                }
                catch (Exception exception)
                {
                    AppLog.Error($"REGISTRY:BUILD-FAILED {row.Game} MODE:ALL", exception);
                    row.Update(previous with
                    {
                        State = TextureRegistryState.Failed,
                        ErrorMessage = Loc.Text("loc_VM_textureRegistry_fullBuildFailed"),
                        UserError = UserFacingMessage.Create("loc_VM_textureRegistry_fullBuildFailed")
                    });
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

    private async Task<TextureRegistryStatus> FinishManualRebuildAsync(MorphFaceGame game,
        TextureRegistryStatus status, bool keep, CancellationToken cancellationToken)
    {
        if (!_store.HasManual(game)) return status;
        if (!keep)
        {
            _store.DeleteManual(game);
            return status;
        }
        if (_store.GetManualStatus(game).State == TextureRegistryState.Outdated) return status;
        var failures = await Task.Run(() => _manualAssets.Revalidate(game, cancellationToken),
            cancellationToken);
        if (failures.Count == 0) return status;
        var report = _store.GetManualReportPath(game);
        AppLog.Error($"REGISTRY:RELINK-FAILED {game} REASONS:{string.Join(" | ", failures)}",
            new FileNotFoundException($"See {report}"));
        return status with
        {
            ErrorMessage = failures.Count == 1
                ? Loc.Format("loc_VM_textureRegistry_oneMissingCustomAsset", report)
                : Loc.Format("loc_VM_textureRegistry_missingCustomAssets", failures.Count, report),
            UserError = failures.Count == 1
                ? UserFacingMessage.Create("loc_VM_textureRegistry_oneMissingCustomAsset", report)
                : UserFacingMessage.Create("loc_VM_textureRegistry_missingCustomAssets", failures.Count, report)
        };
    }

    internal void UpdateProgress(TextureRegistryBuildProgress progress)
    {
        PackagesProcessed = progress.PackagesProcessed;
        TotalPackages = progress.TotalPackages;
        ProgressLabel = progress.Phase switch
        {
            TextureRegistryBuildPhase.ScanningPackages =>
                Loc.Format("loc_VM_textureRegistry_scanningPackages", progress.PackagesProcessed, progress.TotalPackages),
            TextureRegistryBuildPhase.WritingRegistry => Loc.Text("loc_VM_textureRegistry_writingRegistry"),
            TextureRegistryBuildPhase.VerifyingRegistry => Loc.Text("loc_VM_textureRegistry_verifyingRegistry"),
            TextureRegistryBuildPhase.Ready => progress.TextureCount == 1
                ? Loc.Text("loc_VM_textureRegistry_oneTextureReady")
                : Loc.Format("loc_VM_textureRegistry_readyTextureCount", progress.TextureCount),
            _ => Loc.Text("loc_VM_textureRegistry_preparingBuild")
        };
        OnPropertyChanged(nameof(ProgressPercent));

        if (_isRebuildingAll)
        {
            var row = Rows.Single(value => value.Game == progress.Game);
            if (row.Status.State != TextureRegistryState.Building)
                row.Update(row.Status with { State = TextureRegistryState.Building, ErrorMessage = null });
        }
    }

    private void BeginBuild(CancellationToken cancellationToken, bool rebuildingAll, MorphFaceGame? activeGame,
        bool modsOnly = false)
    {
        _statusGeneration++;
        _buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRebuildingAll = rebuildingAll;
        _isRebuildingMods = modsOnly;
        PackagesProcessed = TotalPackages = 0;
        ProgressLabel = Loc.Text("loc_VM_textureRegistry_preparingBuild");
        SetBuildActionVisibility(activeGame, building: true);
        IsBuilding = true;
        OnPropertyChanged(nameof(IsRebuildAllActionVisible));
    }

    private void EndBuild()
    {
        _buildCancellation?.Dispose();
        _buildCancellation = null;
        _isRebuildingAll = false;
        _isRebuildingMods = false;
        SetBuildActionVisibility(activeGame: null, building: false);
        IsBuilding = false;
    }

    private void SetBuildActionVisibility(MorphFaceGame? activeGame, bool building)
    {
        foreach (var row in Rows)
            row.SetBuildActionsVisible(!building || row.Game == activeGame && !_isRebuildingMods,
                !building || row.Game == activeGame && _isRebuildingMods, building);
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
    private TextureRegistryStatus? _customStatus;
    private bool _isBuildActionVisible = true;
    private bool _isRescanModsActionVisible = true;
    private bool _isBuildInProgress;

    public TextureRegistrySettingsRowViewModel(MorphFaceGame game, TextureRegistryStatus status)
    {
        Game = game;
        _status = status;
    }

    public MorphFaceGame Game { get; }
    public string GameLabel => Game.ToString();
    public TextureRegistryStatus Status => _status;
    public TextureRegistryStatus? CustomStatus => _customStatus;
    public bool CanAddCustomAsset => _status.State == TextureRegistryState.Ready && !_isBuildInProgress;
    public bool IsAddCustomAssetVisible => !_isBuildInProgress;
    public bool CanRescanMods => _status.State == TextureRegistryState.Ready &&
                                 (!_isBuildInProgress || IsRescanModsActionVisible);
    public string CustomStatusLabel => _customStatus?.State switch
    {
        TextureRegistryState.Ready or TextureRegistryState.Missing when _customStatus.IsStale =>
            Loc.Text("loc_VM_textureRegistry_customAssetsStale"),
        TextureRegistryState.Ready => Loc.Text("loc_VM_textureRegistry_customAssetsAvailable"),
        TextureRegistryState.Missing => Loc.Text("loc_VM_textureRegistry_noCustomAssets"),
        null => Loc.Text("loc_VM_textureRegistry_checkingCustomAssets"),
        _ => Loc.Format("loc_VM_textureRegistry_customDatabaseStatus", LocalizedStatus(_customStatus.State), LocalizedError(_customStatus))
    };
    public string CustomStatusBrushKey => _customStatus?.State switch
    {
        TextureRegistryState.Ready => _customStatus.IsStale ? "StatusPending" : "StatusReady",
        TextureRegistryState.Missing => _customStatus.IsStale ? "StatusPending" : "SecondaryText",
        TextureRegistryState.Failed => "StatusFailed",
        _ => "StatusPending"
    };
    public string WarningLabel => LocalizedError(_status) ?? LocalizedError(_customStatus) ?? string.Empty;
    public string RefreshGuidanceLabel => string.Join(Environment.NewLine, new[]
    {
        _status.IsStale ? Loc.Text("loc_VM_textureRegistry_refreshOfficialSuggestion") : null,
        _customStatus?.IsStale == true ? Loc.Text("loc_VM_textureRegistry_refreshCustomSuggestion") : null
    }.Where(value => !string.IsNullOrEmpty(value)));

    private static string? LocalizedError(TextureRegistryStatus? status) =>
        status?.UnsupportedSchemaVersion is { } version
            ? Loc.Format(status.IsDevelopmentSchema
                ? "loc_VM_textureRegistry_developmentSchemaUnsupported"
                : "loc_VM_textureRegistry_schemaUnsupported", version)
            : status?.ErrorMessage is not { } diagnostic ? null
            : status.UserError is { } message ? UserFacingMessageFormatter.Format(message)
            : UserFacingMessageFormatter.FormatDiagnostic(diagnostic);
    public bool IsBuildActionVisible
    {
        get => _isBuildActionVisible;
        private set => SetProperty(ref _isBuildActionVisible, value);
    }
    public bool IsRescanModsActionVisible
    {
        get => _isRescanModsActionVisible;
        private set => SetProperty(ref _isRescanModsActionVisible, value);
    }
    public string StatusLabel => _status.State switch
    {
        TextureRegistryState.Checking => Loc.Text("loc_VM_textureRegistry_statusChecking"),
        TextureRegistryState.Ready when _status.IsStale => Loc.Text("loc_VM_textureRegistry_statusStale"),
        TextureRegistryState.Ready => Loc.Text("loc_VM_textureRegistry_statusReady"),
        TextureRegistryState.Building => Loc.Text("loc_VM_textureRegistry_statusBuilding"),
        TextureRegistryState.Outdated => Loc.Text("loc_VM_textureRegistry_statusOutdated"),
        TextureRegistryState.Cancelled => Loc.Text("loc_VM_textureRegistry_statusCancelled"),
        TextureRegistryState.Failed => Loc.Text("loc_VM_textureRegistry_statusFailed"),
        _ => Loc.Text("loc_VM_textureRegistry_statusMissing")
    };
    public string StatusBrushKey => _status.State switch
    {
        TextureRegistryState.Ready => _status.IsStale ? "StatusPending" : "StatusReady",
        TextureRegistryState.Checking or TextureRegistryState.Building or
            TextureRegistryState.Outdated or TextureRegistryState.Cancelled => "StatusPending",
        _ => "StatusFailed"
    };
    public string LastBuiltLabel => _status.LastBuilt is { } timestamp
        ? timestamp.LocalDateTime.ToString("g")
        : "—";

    private static string LocalizedStatus(TextureRegistryState? state) => state switch
    {
        TextureRegistryState.Checking => Loc.Text("loc_VM_textureRegistry_statusChecking"),
        TextureRegistryState.Ready => Loc.Text("loc_VM_textureRegistry_statusReady"),
        TextureRegistryState.Building => Loc.Text("loc_VM_textureRegistry_statusBuilding"),
        TextureRegistryState.Outdated => Loc.Text("loc_VM_textureRegistry_statusOutdated"),
        TextureRegistryState.Cancelled => Loc.Text("loc_VM_textureRegistry_statusCancelled"),
        TextureRegistryState.Failed => Loc.Text("loc_VM_textureRegistry_statusFailed"),
        _ => Loc.Text("loc_VM_textureRegistry_statusMissing")
    };

    public void Update(TextureRegistryStatus status)
    {
        if (status.ErrorMessage is { } diagnostic) AppLog.Warning($"REGISTRY:STATUS {status.Game} {diagnostic}");
        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrushKey));
        OnPropertyChanged(nameof(LastBuiltLabel));
        OnPropertyChanged(nameof(CanAddCustomAsset));
        OnPropertyChanged(nameof(CanRescanMods));
        OnPropertyChanged(nameof(WarningLabel));
        OnPropertyChanged(nameof(RefreshGuidanceLabel));
    }

    public void UpdateCustom(TextureRegistryStatus status)
    {
        if (status.ErrorMessage is { } diagnostic) AppLog.Warning($"REGISTRY:CUSTOM-STATUS {status.Game} {diagnostic}");
        _customStatus = status;
        OnPropertyChanged(nameof(CustomStatus));
        OnPropertyChanged(nameof(CustomStatusLabel));
        OnPropertyChanged(nameof(CustomStatusBrushKey));
        OnPropertyChanged(nameof(WarningLabel));
        OnPropertyChanged(nameof(RefreshGuidanceLabel));
    }

    public void SetBuildActionsVisible(bool rebuildVisible, bool modsVisible, bool building)
    {
        _isBuildInProgress = building;
        IsBuildActionVisible = rebuildVisible;
        IsRescanModsActionVisible = modsVisible;
        OnPropertyChanged(nameof(CanAddCustomAsset));
        OnPropertyChanged(nameof(CanRescanMods));
        OnPropertyChanged(nameof(IsAddCustomAssetVisible));
    }
}
