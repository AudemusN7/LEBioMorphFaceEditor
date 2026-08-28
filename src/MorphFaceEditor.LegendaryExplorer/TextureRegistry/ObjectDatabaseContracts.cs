namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Identifies who owns the active ObjectInstanceDB file without exposing its LEC object graph.</summary>
public enum ObjectDatabaseSource
{
    LegendaryExplorer,
    MorphFaceEditor
}

/// <summary>Describes the observable lifecycle of one game's texture discovery database.</summary>
public enum ObjectDatabaseState
{
    Missing,
    Ready,
    Building,
    Cancelled,
    Failed
}

/// <summary>Detached database metadata suitable for settings and later catalog invalidation.</summary>
public sealed record ObjectDatabaseStatus(
    MorphFaceGame Game,
    ObjectDatabaseState State,
    ObjectDatabaseSource? Source,
    int? SchemaVersion,
    string? FilePath,
    long? FileSize,
    DateTimeOffset? LastWriteTime);

/// <summary>Reports bounded build progress without coupling Settings to LegendaryExplorerCore callbacks.</summary>
public sealed record ObjectDatabaseProgress(
    MorphFaceGame Game,
    int PackagesProcessed,
    int TotalPackages);
