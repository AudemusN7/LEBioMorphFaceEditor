namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Observable lifecycle of one MFE-owned compact texture registry.</summary>
public enum TextureRegistryState
{
    Missing,
    Ready,
    Building,
    Outdated,
    Cancelled,
    Failed
}

/// <summary>Detached registry metadata suitable for the settings window.</summary>
public sealed record TextureRegistryStatus(
    MorphFaceGame Game,
    TextureRegistryState State,
    string? FilePath,
    long? FileSize,
    DateTimeOffset? LastBuilt,
    int? TextureCount,
    string? ErrorMessage);
