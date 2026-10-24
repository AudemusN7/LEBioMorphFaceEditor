using System.Windows.Controls;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Controls;

public partial class GameSelector : ListBox
{
    public GameSelector()
    {
        InitializeComponent();
        ItemsSource = new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 };
        SelectedIndex = 0;
    }

    public MorphFaceGame? SelectedGame => SelectedItem is MorphFaceGame game ? game : null;
}
