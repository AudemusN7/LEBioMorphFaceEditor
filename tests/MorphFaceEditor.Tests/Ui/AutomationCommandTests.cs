using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Tests;

internal static class AutomationCommandTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("awaitable command excludes overlap and releases availability after failure", CompletionAndFailureRestoreAvailability)
    ];

    private static void CompletionAndFailureRestoreAvailability()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("automation evidence");
        var calls = 0;
        var command = new AsyncRelayCommand(async () => { calls++; await completion.Task; throw expected; });
        var pending = command.ExecuteAsync();
        TestAssert.True(!pending.IsCompleted && !command.CanExecute(null), "An active command reported completion early.");
        command.ExecuteAsync().GetAwaiter().GetResult();
        TestAssert.Equal(1, calls);
        completion.SetResult();
        try
        {
            pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            throw new Exception("Command failure was swallowed.");
        }
        catch (InvalidOperationException actual)
        {
            TestAssert.True(ReferenceEquals(expected, actual), "Automation lost its original exception.");
        }
        TestAssert.True(command.CanExecute(null), "A faulted command remained unavailable.");
    }
}
