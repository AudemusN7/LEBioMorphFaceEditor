using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.ViewModels;

/// <summary>Provides detached ObjectInstanceDB status rows for the settings window.</summary>
public sealed class ObjectDatabaseSettingsViewModel : ObservableObject
{
    private readonly ObjectDatabaseProvider _provider;
    private readonly ObjectDatabaseBuilder _builder;

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

    public void Refresh()
    {
        foreach (var row in Rows)
        {
            row.Update(_provider.GetStatus(row.Game));
        }
    }

    public async Task RebuildAsync(MorphFaceGame game, CancellationToken cancellationToken = default)
    {
        var row = Rows.Single(value => value.Game == game);
        row.Update(new ObjectDatabaseStatus(game, ObjectDatabaseState.Building, null, null, null, null, null));
        var status = await _builder.RebuildAsync(game, cancellationToken: cancellationToken).ConfigureAwait(false);
        row.Update(status);
    }
}

/// <summary>Formats one game's OIDB status for WPF while retaining the full detached status for commands.</summary>
public sealed class ObjectDatabaseSettingsRowViewModel : ObservableObject
{
    private ObjectDatabaseStatus _status;

    public ObjectDatabaseSettingsRowViewModel(MorphFaceGame game, ObjectDatabaseStatus status)
    {
        Game = game;
        _status = status;
    }

    public MorphFaceGame Game { get; }
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
        OnPropertyChanged(nameof(SchemaLabel));
        OnPropertyChanged(nameof(FileSizeLabel));
        OnPropertyChanged(nameof(LastBuiltLabel));
    }

    private static string FormatFileSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024d:0.0} KiB",
        _ => $"{size / 1024d / 1024d:0.0} MiB"
    };
}
