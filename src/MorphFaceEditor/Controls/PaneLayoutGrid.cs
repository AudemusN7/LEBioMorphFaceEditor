using System.Windows;
using System.Windows.Controls;

namespace MorphFaceEditor.Controls;

/// <summary>Keeps the two pixel-sized side panes inside the space offered by the window.</summary>
public sealed class PaneLayoutGrid : Grid
{
    protected override Size MeasureOverride(Size constraint)
    {
        // Fit before measuring: otherwise oversized pixel columns inflate DesiredSize
        // and ActualWidth, hiding the overflow from a later SizeChanged handler.
        FitSidePanes(constraint.Width);
        return base.MeasureOverride(constraint);
    }

    internal void FitSidePanes(double width)
    {
        if (!double.IsFinite(width) || ColumnDefinitions.Count != 5) return;
        var leftColumn = ColumnDefinitions[0];
        var rightColumn = ColumnDefinitions[4];
        var available = width - ColumnDefinitions[2].MinWidth
            - ColumnDefinitions[1].Width.Value - ColumnDefinitions[3].Width.Value;
        if (available < leftColumn.MinWidth + rightColumn.MinWidth) return;
        var left = Math.Max(leftColumn.MinWidth, leftColumn.Width.Value);
        var right = Math.Max(rightColumn.MinWidth, rightColumn.Width.Value);
        var excess = left + right - available;
        if (excess <= 0) return;
        var leftSpare = left - leftColumn.MinWidth;
        var rightSpare = right - rightColumn.MinWidth;
        var spare = leftSpare + rightSpare;
        if (spare <= 0) return;
        leftColumn.Width = new GridLength(Math.Max(leftColumn.MinWidth, left - excess * leftSpare / spare));
        rightColumn.Width = new GridLength(Math.Max(rightColumn.MinWidth, right - excess * rightSpare / spare));
    }
}
