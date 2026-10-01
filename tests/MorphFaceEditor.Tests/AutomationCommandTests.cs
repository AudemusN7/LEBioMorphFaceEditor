using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Tests;

internal static class AutomationCommandTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("awaitable commands expose completion and reject overlapping execution", CompletionAndReentrancy),
        new("awaitable command failure restores availability and preserves the original exception", FailureRestoresAvailability)
    ];

    private static void CompletionAndReentrancy()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var command = new AsyncRelayCommand(async () => { calls++; await completion.Task; });
        var pending = command.ExecuteAsync();
        TestAssert.True(!pending.IsCompleted && !command.CanExecute(null), "An active command reported completion/availability too early.");
        command.ExecuteAsync().GetAwaiter().GetResult();
        TestAssert.Equal(1, calls);
        completion.SetResult();
        pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        TestAssert.True(command.CanExecute(null), "A finished command remained unavailable.");
    }

    private static void FailureRestoresAvailability()
    {
        var expected = new InvalidOperationException("automation evidence");
        var command = new AsyncRelayCommand(() => Task.FromException(expected));
        try
        {
            command.ExecuteAsync().GetAwaiter().GetResult();
            throw new Exception("Command failure was swallowed.");
        }
        catch (InvalidOperationException actual)
        {
            TestAssert.True(ReferenceEquals(expected, actual), "Automation lost the original command exception.");
        }
        TestAssert.True(command.CanExecute(null), "A faulted command remained unavailable.");
    }
}
