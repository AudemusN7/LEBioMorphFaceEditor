using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

public partial class MeshSaveFormatWindow : Window
{
    public MeshSaveFormatWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    public MeshSaveChoice? Choice { get; private set; }
    private void OnPcc(object sender, RoutedEventArgs e) => Complete(MeshSaveChoice.Pcc);
    private void OnMaterials(object sender, RoutedEventArgs e) => Complete(MeshSaveChoice.MaterialsRon);
    private void Complete(MeshSaveChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
