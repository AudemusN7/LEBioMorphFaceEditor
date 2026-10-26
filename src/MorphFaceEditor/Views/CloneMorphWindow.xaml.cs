using System.Windows;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class CloneMorphWindow : Window
{
    private readonly IReadOnlySet<string> _existingNames;
    private readonly bool _importMode;

    public CloneMorphWindow(
        string suggestedName,
        IReadOnlyCollection<string> existingObjectNames,
        bool importMode = false,
        bool newPlayerMode = false,
        bool newNpcMode = false)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _importMode = importMode;
        if (importMode)
        {
            Title = Loc.Text("loc_CloneMorphWindow_NameImportedBioMorphFace");
            IntroText.Text = Loc.Text("loc_CloneMorphWindow_ImportedMorphIntro");
            PrimaryButton.Content = Loc.Text("loc_CloneMorphWindow_ImportMorph");
        }
        if (newPlayerMode)
        {
            Title = Loc.Text("loc_CloneMorphWindow_NameNewPlayerMorph");
            PrimaryButton.Content = Loc.Text("loc_CloneMorphWindow_CreateMorph");
        }
        if (newNpcMode)
        {
            Title = Loc.Text("loc_CloneMorphWindow_NameNewNpcMorph");
            IntroText.Text = Loc.Text("loc_CloneMorphWindow_NewNpcIntro");
            PrimaryButton.Content = Loc.Text("loc_CloneMorphWindow_CreateMorph");
        }
        _existingNames = existingObjectNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ObjectNameBox.Text = suggestedName;
        ObjectNameBox.SelectAll();
        ObjectNameBox.Focus();
    }

    public string? ObjectName { get; private set; }

    private void OnClone(object sender, RoutedEventArgs e)
    {
        var name = ObjectNameBox.Text.Trim();
        var error = Validate(name);
        if (error is not null)
        {
            ValidationText.Text = error;
            ObjectNameBox.Focus();
            return;
        }

        ObjectName = name;
        DialogResult = true;
    }

    private string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return _importMode
                ? Loc.Text("loc_CloneMorphWindow_EnterImportedBioMorphFaceName")
                : Loc.Text("loc_CloneMorphWindow_EnterClonedBioMorphFaceName");
        }
        if (!(char.IsLetter(name[0]) || name[0] == '_') ||
            name.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return Loc.Text("loc_CloneMorphWindow_NameCharactersRule");
        }
        return _existingNames.Contains(name)
            ? Loc.Text("loc_CloneMorphWindow_BioMorphFaceNameAlreadyExists")
            : null;
    }
}
