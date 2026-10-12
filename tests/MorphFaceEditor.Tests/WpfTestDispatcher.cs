using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

/// <summary>Runs WPF smoke work on the process-wide application dispatcher.</summary>
internal static class WpfTestDispatcher
{
    private static readonly object Sync = new();
    private static Host? _host;

    internal static void Invoke(Action action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var current = Application.Current;
        if (current is not null)
        {
            Invoke(current.Dispatcher, action, timeout ?? TimeSpan.FromSeconds(30));
            return;
        }

        Host host;
        lock (Sync) host = _host ??= new Host();
        host.Invoke(action, timeout ?? TimeSpan.FromSeconds(30));
    }

    private static void Invoke(Dispatcher dispatcher, Action action, TimeSpan timeout)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }
        var operation = dispatcher.InvokeAsync(action);
        var completed = Task.WhenAny(operation.Task, Task.Delay(timeout)).GetAwaiter().GetResult();
        if (completed != operation.Task)
            throw new TimeoutException($"WPF test action did not finish within {timeout.TotalSeconds:0} seconds.");
        operation.Task.GetAwaiter().GetResult();
    }

    private sealed class Host
    {
        private readonly ManualResetEventSlim _ready = new();
        private Dispatcher? _dispatcher;
        private Exception? _startupFailure;

        public Host()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var application = new SmokeApplication();
                    application.InitializeComponent();
                    application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception exception)
                {
                    _startupFailure = exception;
                }
                finally
                {
                    _ready.Set();
                }

                if (_startupFailure is null) Dispatcher.Run();
            })
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("WPF test dispatcher did not start within fifteen seconds.");
            if (_startupFailure is not null)
                throw new Exception($"WPF test application failed to start: {_startupFailure}", _startupFailure);
        }

        public void Invoke(Action action, TimeSpan timeout)
        {
            if (_startupFailure is not null)
                throw new Exception($"WPF test application failed to start: {_startupFailure}", _startupFailure);
            WpfTestDispatcher.Invoke(_dispatcher!, action, timeout);
        }
    }
}

internal sealed class SmokeApplication : Application
{
    public void InitializeComponent()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/MorphFaceEditor;component/Themes/EditorTheme.xaml", UriKind.Relative)
        });
        Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());
        Resources.Add("PreviewRenderModeLabelConverter", new PreviewRenderModeLabelConverter());
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/MorphFaceEditor;component/Localisation/LanguageFlags.xaml", UriKind.Relative)
        });
        Loc.Initialize(Resources, "en-GB");
    }
}
