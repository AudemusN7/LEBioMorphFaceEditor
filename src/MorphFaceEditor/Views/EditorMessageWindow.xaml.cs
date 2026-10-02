using System.Windows;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class EditorMessageWindow : Window
{
    public EditorMessageWindow(
        string title,
        string message,
        bool confirmation = false,
        string? primaryLabel = null,
        string? secondaryLabel = null,
        string? cancelLabel = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        HeadingText.Text = title.ToUpperInvariant();
        MessageText.Text = message;
        PrimaryButton.Content = primaryLabel ?? Loc.Text("loc_Common_Ok");
        SecondaryButton.Content = secondaryLabel ?? Loc.Text("loc_Common_Cancel");
        SecondaryButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        if (cancelLabel is not null)
        {
            WasCancelled = true;
            CancelButton.Content = cancelLabel;
            CancelButton.Visibility = Visibility.Visible;
            SecondaryButton.IsCancel = false;
        }
    }

    public bool WasCancelled { get; private set; }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        WasCancelled = false;
        DialogResult = true;
    }
    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        WasCancelled = false;
        DialogResult = false;
    }
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        WasCancelled = true;
        DialogResult = false;
    }
}
