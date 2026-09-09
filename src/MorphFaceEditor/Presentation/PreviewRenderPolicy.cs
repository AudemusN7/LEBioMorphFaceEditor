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
        CanRenderExplicitly(state) &&
        state.IsActive;

    /// <summary>Allows an explicit scene/material publication while inactive, but never while hidden, minimized, closed, or unrealized.</summary>
    public static bool CanRenderExplicitly(PreviewActivityState state) =>
        state.IsVisible &&
        !state.IsMinimized &&
        !state.IsClosed &&
        state.HostWidth > 0 &&
        state.HostHeight > 0;
}
