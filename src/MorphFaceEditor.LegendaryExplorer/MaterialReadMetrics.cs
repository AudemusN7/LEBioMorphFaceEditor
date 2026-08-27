namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record MaterialReadMetrics(
    TimeSpan Overrides,
    TimeSpan Resolution,
    int CacheHits,
    int CacheMisses);
