namespace MorphFaceEditor.Presentation;

public readonly record struct PreviewActivityState(
    bool IsVisible,
    bool IsActive,
    bool IsMinimized,
    bool IsClosed,
    double HostWidth,
    double HostHeight);

public static class PreviewRenderPolicy
{
    public static bool CanRender(PreviewActivityState state) =>
        state.IsVisible &&
        state.IsActive &&
        !state.IsMinimized &&
        !state.IsClosed &&
        state.HostWidth > 0 &&
        state.HostHeight > 0;
}
