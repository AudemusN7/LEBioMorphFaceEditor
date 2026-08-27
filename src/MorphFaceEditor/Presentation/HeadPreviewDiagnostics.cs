namespace MorphFaceEditor.Presentation;

public sealed class HeadPreviewDiagnostics
{
    public int RendererCreations { get; internal set; }
    public int RendererDisposals { get; internal set; }
    public int ResizeOperations { get; internal set; }
    public int FramesRendered { get; internal set; }
    public int CoalescedRenderRequests { get; internal set; }
}
