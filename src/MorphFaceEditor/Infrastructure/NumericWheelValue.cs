namespace MorphFaceEditor.Infrastructure;

public static class NumericWheelValue
{
    public static float Adjust(float current, float increment, int direction)
    {
        if (!float.IsFinite(current) || !float.IsFinite(increment) || increment <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(increment));
        }
        return MathF.Round(current + Math.Sign(direction) * increment, 6);
    }
}
