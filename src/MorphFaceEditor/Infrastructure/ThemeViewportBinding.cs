using System.Numerics;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Infrastructure;

/// <summary>Keeps the renderer's default background in step with the active UI theme.</summary>
internal sealed class ThemeViewportBinding : IDisposable
{
    private readonly ThemeManager _themes;
    private readonly MainWindowViewModel _viewModel;

    internal ThemeViewportBinding(ThemeManager themes, MainWindowViewModel viewModel)
    {
        _themes = themes;
        _viewModel = viewModel;
        _themes.Changed += Apply;
        Apply(null, EventArgs.Empty);
    }

    private void Apply(object? sender, EventArgs args)
    {
        var colour = _themes.ViewportBackground;
        _viewModel.ApplyThemeBackgroundColor(new Vector4(colour.R / 255f, colour.G / 255f, colour.B / 255f, 1));
    }

    public void Dispose() => _themes.Changed -= Apply;
}
