using System.IO;
using System.Windows;
using Microsoft.Win32;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

public partial class SaveMorphToPccWindow : Window
{
    private readonly string _sourcePackagePath;
    private readonly string _suggestedFileName;

    public SaveMorphToPccWindow(string suggestedFileName, string sourcePackagePath)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _sourcePackagePath = sourcePackagePath;
        _suggestedFileName = suggestedFileName;
        DestinationBox.Focus();
    }

    public MorphPackageSaveRequest? Request { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an existing destination PCC",
            Filter = "Mass Effect packages (*.pcc)|*.pcc",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(DestinationBox.Text))
                ? Path.GetDirectoryName(DestinationBox.Text)
                : Path.GetDirectoryName(_sourcePackagePath)
        };
        if (dialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = dialog.FileName;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var destination = DestinationBox.Text.Trim();
        var createNewPackage = string.IsNullOrWhiteSpace(destination);
        if (createNewPackage)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Create a minimal morph PCC",
                Filter = "Mass Effect packages (*.pcc)|*.pcc",
                AddExtension = true,
                DefaultExt = ".pcc",
                FileName = _suggestedFileName,
                InitialDirectory = Path.GetDirectoryName(_sourcePackagePath),
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            destination = dialog.FileName;
            if (File.Exists(destination))
            {
                ValidationText.Text = "That PCC already exists. Use Browse Existing to add the morph to it, or choose a new filename.";
                return;
            }
        }
        else if (!File.Exists(destination))
        {
            ValidationText.Text = "The specified destination does not exist. Clear the field to create a new minimal PCC.";
            return;
        }
        if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(_sourcePackagePath), StringComparison.OrdinalIgnoreCase))
        {
            ValidationText.Text = "Choose a different destination; the open source PCC cannot also be the export target.";
            return;
        }
        Request = new MorphPackageSaveRequest(Path.GetFullPath(destination), createNewPackage);
        DialogResult = true;
    }
}
