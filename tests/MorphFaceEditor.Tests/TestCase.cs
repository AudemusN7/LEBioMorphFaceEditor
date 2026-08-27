using System.Numerics;

namespace MorphFaceEditor.Tests;

// Minimal runner contract: tests remain ordinary methods without a framework dependency.
public sealed record TestCase(string Name, Action Run);

// Assertions throw concise failures for the console runner to collect.
public static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected {expected}; got {actual}.");
        }
    }

    public static void Near(float expected, float actual, float tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new Exception($"Expected {expected}; got {actual}.");
        }
    }

    public static void Near(Vector3 expected, Vector3 actual, float tolerance)
    {
        if (Vector3.Distance(expected, actual) > tolerance)
        {
            throw new Exception($"Expected {expected}; got {actual}.");
        }
    }
}
