namespace MorphFaceEditor.LegendaryExplorer;

public sealed record MorphFaceLoadMetrics(
    TimeSpan Total,
    TimeSpan PackageOpen,
    TimeSpan Document,
    TimeSpan BaseHead,
    TimeSpan Attachment,
    TimeSpan Materials,
    TimeSpan Diagnostics,
    TimeSpan MaterialOverrides,
    TimeSpan MaterialResolution,
    int MaterialCacheHits,
    int MaterialCacheMisses);
