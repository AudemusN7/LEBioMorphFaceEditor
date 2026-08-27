using System.Windows;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class CloneMorphWindow : Window
{
    private readonly IReadOnlySet<string> _existingNames;

    public CloneMorphWindow(string suggestedName, IReadOnlyCollection<string> existingObjectNames)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
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
            return "Enter a name for the cloned BioMorphFace.";
        }
        if (!(char.IsLetter(name[0]) || name[0] == '_') ||
            name.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return "Use letters, digits, and underscores; the first character cannot be a digit.";
        }
        return _existingNames.Contains(name)
            ? "A BioMorphFace with that name already exists at this package path."
            : null;
    }
}
