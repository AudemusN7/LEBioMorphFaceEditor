using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MorphFaceEditor.Localiser;

internal static class LocaliserDialog
{
    internal static MessageBoxResult Show(Window owner, string title, string text, bool confirmation = false)
    {
        var result = confirmation ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        var window = new Window
        {
            Owner = owner, Title = title, Resources = owner.Resources, FontFamily = owner.FontFamily, FontSize = owner.FontSize,
            Background = (Brush)owner.FindResource("WindowBackground"), Foreground = (Brush)owner.FindResource("PrimaryText"),
            Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        MainWindow.ApplyDarkCaption(window);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 400 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var (label, value) in confirmation
                     ? new[] { ("Save", MessageBoxResult.Yes), ("Discard", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) }
                     : new[] { ("OK", MessageBoxResult.OK) })
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), IsDefault = value is MessageBoxResult.Yes or MessageBoxResult.OK, IsCancel = value == MessageBoxResult.Cancel };
            button.Click += (_, _) => { result = value; window.DialogResult = value != MessageBoxResult.Cancel; };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); window.Content = panel; window.ShowDialog(); return result;
    }
}
