using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Views;

public partial class ActorAssignmentWindow : Window
{
    private readonly ActorAssignmentChooserViewModel _viewModel;

    public ActorAssignmentWindow(ActorAssignmentInventory inventory, ActorAssignmentMode mode)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _viewModel = new ActorAssignmentChooserViewModel(inventory, mode);
        DataContext = _viewModel;
    }

    public ActorAssignmentCandidate? SelectedCandidate { get; private set; }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedChoice is not { IsEligible: true } choice) return;
        SelectedCandidate = choice.Candidate;
        DialogResult = true;
    }
}
