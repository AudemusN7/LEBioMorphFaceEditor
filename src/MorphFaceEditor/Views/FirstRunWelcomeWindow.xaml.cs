using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class FirstRunWelcomeWindow : Window
{
    // Temporary release target: replace with the dedicated tutorial page when it exists.
    internal static readonly Uri TutorialUri = new("https://github.com/AudemusN7/LEBioMorphFaceEditor");

    public FirstRunWelcomeWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    public bool OpenTextureDatabasesRequested { get; private set; }
    public bool DontShowAgain => DontShowAgainCheckBox.IsChecked == true;

    private void OnTutorialNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(TutorialUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.Error("The GitHub tutorial could not be opened.", exception);
            _ = new EditorMessageWindow(
                Loc.Text("loc_FirstRunWelcomeWindow_TutorialUnavailable"),
                Loc.Format("loc_FirstRunWelcomeWindow_TutorialCouldNotOpen", TutorialUri.AbsoluteUri))
            {
                Owner = this
            }.ShowDialog();
        }
        e.Handled = true;
    }

    private void OnBuildTextureDatabases(object sender, RoutedEventArgs e)
    {
        OpenTextureDatabasesRequested = true;
        DialogResult = true;
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => DialogResult = true;
}
