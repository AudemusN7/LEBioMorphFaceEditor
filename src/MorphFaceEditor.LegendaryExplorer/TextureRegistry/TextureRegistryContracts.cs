using MorphFaceEditor.Core.Domain;
namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Observable lifecycle of one MFE-owned compact texture registry.</summary>
public enum TextureRegistryState
{
    Checking,
    Missing,
    Ready,
    Building,
    Outdated,
    Cancelled,
    Failed
}

public enum TextureRegistryBuildFailure
{
    OfficialDatabaseRequired,
    OfficialDatabaseChanged
}

public sealed class TextureRegistryBuildException(
    TextureRegistryBuildFailure reason, string message) : IOException(message)
{
    public TextureRegistryBuildFailure Reason { get; } = reason;
}

/// <summary>Detached registry metadata suitable for the settings window.</summary>
public sealed record TextureRegistryStatus(
    MorphFaceGame Game,
    TextureRegistryState State,
    string? FilePath,
    long? FileSize,
    DateTimeOffset? LastBuilt,
    int? TextureCount,
    string? ErrorMessage)
{
    public int? UnsupportedSchemaVersion { get; init; }
    public bool IsDevelopmentSchema { get; init; }
    public bool IsStale { get; init; }
}
