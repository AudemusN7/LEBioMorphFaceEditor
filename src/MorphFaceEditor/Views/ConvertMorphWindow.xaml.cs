using MorphFaceEditor.Core.Domain;
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
    private readonly bool _playerRonMode;
    private readonly bool _allowBakedShapeTransfer;

    public ConvertMorphWindow(
        MorphFaceGame sourceGame,
        string suggestedFileName,
        string sourcePackagePath,
        bool playerRonMode = false,
        bool allowBakedShapeTransfer = false)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _sourcePackagePath = sourcePackagePath;
        _suggestedFileName = suggestedFileName;
        _playerRonMode = playerRonMode;
        _allowBakedShapeTransfer = playerRonMode && allowBakedShapeTransfer && sourceGame == MorphFaceGame.LE1;
        if (playerRonMode)
        {
            Width = 420;
            Height = double.NaN;
            MinHeight = 280;
            SizeToContent = SizeToContent.Height;
            DescriptionText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "loc_PlayerConversion_Description");
            PackageDestinationPanel.Visibility = Visibility.Collapsed;
        }
        TargetGameSelector.SelectionChanged += (_, _) => UpdateBakedShapeVisibility();
        TargetGameSelector.ItemsSource = new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }
            .Where(game => game != sourceGame)
            .ToArray();
        TargetGameSelector.SelectedIndex = 0;
        UpdateBakedShapeVisibility();
        if (!playerRonMode) DestinationBox.Focus();
    }

    public MorphConversionSaveRequest? Request { get; private set; }

    private void UpdateBakedShapeVisibility()
    {
        var visible = _allowBakedShapeTransfer && TargetGameSelector.SelectedGame == MorphFaceGame.LE3;
        BakedShapePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) ApproximateBakedShapeCheckBox.IsChecked = false;
    }

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
        if (TargetGameSelector.SelectedGame is not MorphFaceGame target)
        {
            ValidationText.Text = Loc.Text("loc_ConvertMorphWindow_ChooseTargetGame");
            return;
        }
        if (_playerRonMode)
        {
            var ronDialog = new SaveFileDialog
            {
                Title = Loc.Text("loc_PlayerConversion_SaveTitle"),
                Filter = EditorFileDrop.RonMorphFilter,
                AddExtension = true,
                DefaultExt = ".ron",
                FileName = _suggestedFileName,
                InitialDirectory = Path.GetDirectoryName(_sourcePackagePath),
                OverwritePrompt = false
            };
            if (ronDialog.ShowDialog(this) != true) return;
            if (IsSource(ronDialog.FileName) || File.Exists(ronDialog.FileName))
            {
                ValidationText.Text = Loc.Text("loc_PlayerConversion_ChooseNewFile");
                return;
            }
            Request = new MorphConversionSaveRequest(target, Path.GetFullPath(ronDialog.FileName), true, null,
                ApproximateBakedShape: BakedShapePanel.Visibility == Visibility.Visible && ApproximateBakedShapeCheckBox.IsChecked == true);
            DialogResult = true;
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
                target,
                Path.GetFullPath(destination),
                CreateNewPackage: false,
                TemplatePackagePath: null);
            DialogResult = true;
            return;
        }

        var outputDialog = new SaveFileDialog
        {
            Title = Loc.Format("loc_ConvertMorphWindow_CreateConvertedMorphPcc", target),
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
            target,
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
}
