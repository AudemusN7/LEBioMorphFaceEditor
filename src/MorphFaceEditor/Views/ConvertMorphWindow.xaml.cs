using System.IO;
using System.Windows;
using Microsoft.Win32;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

public partial class ConvertMorphWindow : Window
{
    private readonly string _sourcePackagePath;
    private readonly string _suggestedFileName;

    public ConvertMorphWindow(
        MorphFaceGame sourceGame,
        string suggestedFileName,
        string sourcePackagePath)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _sourcePackagePath = sourcePackagePath;
        _suggestedFileName = suggestedFileName;
        TargetGameBox.ItemsSource = sourceGame == MorphFaceGame.LE3
            ? new[] { new GameChoice(MorphFaceGame.LE1, "LE1"), new GameChoice(MorphFaceGame.LE2, "LE2") }
            : new[] { new GameChoice(MorphFaceGame.LE3, "LE3") };
        TargetGameBox.SelectedIndex = 0;
        TargetGameBox.IsEnabled = TargetGameBox.Items.Count > 1;
        DestinationBox.Focus();
    }

    public MorphConversionSaveRequest? Request { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = CreatePackageOpenDialog("Choose an existing destination PCC");
        if (dialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = dialog.FileName;
        }
    }

    private void OnConvert(object sender, RoutedEventArgs e)
    {
        if (TargetGameBox.SelectedItem is not GameChoice target)
        {
            ValidationText.Text = "Choose a target game.";
            return;
        }
        var destination = DestinationBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(destination))
        {
            if (!File.Exists(destination))
            {
                ValidationText.Text = "The specified destination does not exist. Clear the field to create a new PCC.";
                return;
            }
            if (IsSource(destination))
            {
                ValidationText.Text = "Choose a different destination PCC.";
                return;
            }
            Request = new MorphConversionSaveRequest(
                target.Game,
                Path.GetFullPath(destination),
                CreateNewPackage: false,
                TemplatePackagePath: null);
            DialogResult = true;
            return;
        }

        var templateDialog = CreatePackageOpenDialog(
            $"Choose a {target.Label} template PCC containing a matching BioMorphFace");
        if (templateDialog.ShowDialog(this) != true)
        {
            return;
        }
        var outputDialog = new SaveFileDialog
        {
            Title = $"Create a converted {target.Label} morph PCC",
            Filter = "Mass Effect packages (*.pcc)|*.pcc",
            AddExtension = true,
            DefaultExt = ".pcc",
            FileName = _suggestedFileName,
            InitialDirectory = Path.GetDirectoryName(_sourcePackagePath),
            OverwritePrompt = false
        };
        if (outputDialog.ShowDialog(this) != true)
        {
            return;
        }
        if (File.Exists(outputDialog.FileName))
        {
            ValidationText.Text = "That output PCC already exists. Choose a new filename.";
            return;
        }
        if (IsSource(outputDialog.FileName) ||
            string.Equals(
                Path.GetFullPath(templateDialog.FileName),
                Path.GetFullPath(outputDialog.FileName),
                StringComparison.OrdinalIgnoreCase))
        {
            ValidationText.Text = "The source, template, and output PCCs must be different files.";
            return;
        }
        Request = new MorphConversionSaveRequest(
            target.Game,
            Path.GetFullPath(outputDialog.FileName),
            CreateNewPackage: true,
            Path.GetFullPath(templateDialog.FileName));
        DialogResult = true;
    }

    private OpenFileDialog CreatePackageOpenDialog(string title) => new()
    {
        Title = title,
        Filter = "Mass Effect packages (*.pcc)|*.pcc",
        CheckFileExists = true,
        Multiselect = false,
        InitialDirectory = Path.GetDirectoryName(_sourcePackagePath)
    };

    private bool IsSource(string path) => string.Equals(
        Path.GetFullPath(path),
        Path.GetFullPath(_sourcePackagePath),
        StringComparison.OrdinalIgnoreCase);

    private sealed record GameChoice(MorphFaceGame Game, string Label);
}
