using System.Windows;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class EditorMessageWindow : Window
{
    public EditorMessageWindow(
        string title,
        string message,
        bool confirmation = false,
        string primaryLabel = "OK",
        string secondaryLabel = "Cancel")
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        HeadingText.Text = title.ToUpperInvariant();
        MessageText.Text = message;
        PrimaryButton.Content = primaryLabel;
        SecondaryButton.Content = secondaryLabel;
        SecondaryButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnSecondary(object sender, RoutedEventArgs e) => DialogResult = false;
}
