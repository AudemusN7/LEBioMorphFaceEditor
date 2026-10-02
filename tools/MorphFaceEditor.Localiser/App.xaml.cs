using System.Windows;

namespace MorphFaceEditor.Localiser;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.OpenInitial(e.Args.FirstOrDefault());
    }
}
