using System.Windows;
using System.Windows.Controls;

namespace MorphFaceEditor.Controls;

public sealed class SquareDecorator : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return new Size();
        }

        var side = double.IsInfinity(constraint.Width)
            ? Math.Max(Child.DesiredSize.Width, Child.DesiredSize.Height)
            : constraint.Width;
        if (double.IsNaN(side) || side <= 0)
        {
            side = 196;
        }
        Child.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var side = arrangeSize.Width;
        Child?.Arrange(new Rect(0, 0, side, side));
        return new Size(side, side);
    }
}
