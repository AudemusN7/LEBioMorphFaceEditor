using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MorphFaceEditor.Infrastructure;

/// <summary>Completes one captured editor gesture when its control or input lifetime ends.</summary>
internal sealed class ContinuousEditGesture : IDisposable
{
    private static readonly TimeSpan WheelIdleTime = TimeSpan.FromMilliseconds(450);
    private readonly DispatcherTimer _idleTimer = new() { Interval = WheelIdleTime };
    private object? _owner;
    private Action? _complete;
    private Action? _detach;
    private long _generation;
    private long? _completeAt;

    public ContinuousEditGesture() => _idleTimer.Tick += OnIdle;

    public bool IsActiveFor(object owner) => ReferenceEquals(_owner, owner);

    public void Begin(FrameworkElement control, Action begin, Action complete, object? owner = null)
    {
        Complete();
        var generation = ++_generation;
        _owner = owner ?? control;
        _complete = complete;
        RoutedEventHandler ended = (_, _) => Complete(generation);
        MouseEventHandler captureLost = (_, _) => Complete(generation);
        KeyboardFocusChangedEventHandler focusLost = (_, _) => Complete(generation);
        DependencyPropertyChangedEventHandler contextChanged = (_, _) => Complete(generation);
        control.Unloaded += ended;
        control.LostMouseCapture += captureLost;
        control.LostKeyboardFocus += focusLost;
        control.DataContextChanged += contextChanged;
        _detach = () =>
        {
            control.Unloaded -= ended;
            control.LostMouseCapture -= captureLost;
            control.LostKeyboardFocus -= focusLost;
            control.DataContextChanged -= contextChanged;
        };
        try { begin(); }
        catch
        {
            Complete(generation);
            throw;
        }
    }

    public void Complete(object owner)
    {
        if (IsActiveFor(owner)) Complete();
    }

    public void RestartWheelTimeout()
    {
        if (_complete is null) return;
        _completeAt = Environment.TickCount64 + (long)WheelIdleTime.TotalMilliseconds;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    public void Complete()
    {
        _idleTimer.Stop();
        _completeAt = null;
        var complete = _complete;
        _complete = null;
        _owner = null;
        _detach?.Invoke();
        _detach = null;
        complete?.Invoke();
    }

    private void Complete(long generation)
    {
        if (generation == _generation) Complete();
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        // A queued tick from an earlier wheel burst cannot complete a newer gesture.
        if (_completeAt is { } due && Environment.TickCount64 >= due) Complete();
    }

    public void Dispose()
    {
        Complete();
        _idleTimer.Tick -= OnIdle;
    }
}
