using System.Windows;
using System.Windows.Navigation;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        VersionText.Text = $"Version {ApplicationInfo.Version}";
        BuildDateText.Text = $"Built {ApplicationInfo.BuildDate}";
        LibraryList.ItemsSource = ThirdPartyNotices.All;
        LibraryList.SelectedIndex = 0;
    }

    private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        ExternalLinks.Open(this, e.Uri);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
