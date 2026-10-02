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
        TargetGameBox.ItemsSource = new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }
            .Where(game => game != sourceGame)
            .Select(game => new GameChoice(game, game.ToString()))
            .ToArray();
        TargetGameBox.SelectedIndex = 0;
        TargetGameBox.IsEnabled = TargetGameBox.Items.Count > 1;
        DestinationBox.Focus();
    }

    public MorphConversionSaveRequest? Request { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = CreatePackageOpenDialog(Loc.Text("loc_ConvertMorphWindow_ChooseExistingDestinationPcc"));
        if (dialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = dialog.FileName;
        }
    }

    private void OnConvert(object sender, RoutedEventArgs e)
    {
        if (TargetGameBox.SelectedItem is not GameChoice target)
        {
            ValidationText.Text = Loc.Text("loc_ConvertMorphWindow_ChooseTargetGame");
            return;
        }
        var destination = DestinationBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(destination))
        {
            if (!File.Exists(destination))
            {
                ValidationText.Text = Loc.Text("loc_ConvertMorphWindow_DestinationDoesNotExist");
                return;
            }
            if (IsSource(destination))
            {
                ValidationText.Text = Loc.Text("loc_ConvertMorphWindow_ChooseDifferentDestinationPcc");
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

        var outputDialog = new SaveFileDialog
        {
            Title = Loc.Format("loc_ConvertMorphWindow_CreateConvertedMorphPcc", target.Label),
            Filter = EditorFileDrop.PackageFilter,
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
            ValidationText.Text = Loc.Text("loc_ConvertMorphWindow_OutputPccAlreadyExists");
            return;
        }
        if (IsSource(outputDialog.FileName))
        {
            ValidationText.Text = Loc.Text("loc_ConvertMorphWindow_SourceAndOutputMustDiffer");
            return;
        }
        Request = new MorphConversionSaveRequest(
            target.Game,
            Path.GetFullPath(outputDialog.FileName),
            CreateNewPackage: true,
            TemplatePackagePath: null);
        DialogResult = true;
    }

    private OpenFileDialog CreatePackageOpenDialog(string title) => new()
    {
        Title = title,
        Filter = EditorFileDrop.PackageFilter,
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
