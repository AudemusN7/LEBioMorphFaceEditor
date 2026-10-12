namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record MaterialReadMetrics(
    TimeSpan Overrides,
    TimeSpan Resolution,
    int CacheHits,
    int CacheMisses)
{
    public TimeSpan Defaults { get; init; }
    public TimeSpan ShaderLookup { get; init; }
    public TimeSpan TextureDecode { get; init; }
}
