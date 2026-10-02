using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MorphFaceEditor.Controls;

namespace MorphFaceEditor.Infrastructure;

// Temporary authoring tool: excluded from Release builds, including persistence.
internal sealed class DebugThemePalette
{
    internal sealed record Definition(string Key, string Group, string Name, string[] Roles);
    internal static readonly IReadOnlyList<Definition> Definitions =
    [
        new("Palette.Window", "Backgrounds", "WindowBackground", ["WindowBackground", "InputBackground", "TextureWellBackground", "PreviewHintBackground", "BusyOverlayBackground", "BonePadBackground"]),
        new("Palette.Panel", "Backgrounds", "PanelBackground", ["PanelBackground", "SectionBackground", "NavyCaption"]),
        new("Palette.Raised", "Backgrounds", "RaisedBackground", ["RaisedBackground", "BoneSelectionBackground"]),
        new("Palette.Hover", "Interaction", "HoverBackground", ["HoverBackground"]),
        new("Palette.Pressed", "Interaction", "PressedBackground", ["PressedBackground"]),
        new("Palette.Primary", "Foregrounds", "PrimaryText", ["PrimaryText", "OnColourText", "ErrorBannerText", "CopyErrorStroke", "LightText"]),
        new("Palette.Secondary", "Foregrounds", "SecondaryText", ["SecondaryText", "SwatchBorder", "PuckOutline"]),
        new("Palette.Muted", "Foregrounds", "MutedText", ["MutedText", "LocalDetailText", "PlaceholderText", "BonePadBorder", "BoneGridLine", "BoneRail"]),
        new("Palette.Accent", "Accent", "Accent", ["Accent", "BadgeBorder", "TextSelectionBrush"]),
        new("Palette.Border", "Borders", "BorderBrush", ["BorderBrush", "NavyBorder", "HintBorder"]),
        new("Palette.SelectedRow", "Accent", "SelectionBackground", ["SelectionBackground", "BadgeBackground"]),
        new("Palette.Ready", "Feedback", "StatusReady", ["StatusReady", "ActorIncludeBackground"]),
        new("Palette.Pending", "Feedback", "StatusPending", ["StatusPending"]),
        new("Palette.Failed", "Feedback", "StatusFailed", ["StatusFailed", "ValidationForeground", "WarningText", "ErrorBannerBorder", "ActorRemoveBackground", "ErrorBannerBackground"]),
        new("Palette.PreviewHost", "Specialist", "PreviewHostBackground", ["PreviewHostBackground"]),
        .. ArchetypeTag.Definitions.Select(tag =>
            new Definition(tag.ColourKey, "Archetype tags", tag.DisplayName, [tag.BrushKey]))
    ];

    private readonly ResourceDictionary _resources;
    public string FilePath { get; }
    public event EventHandler? Changed;
    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    public DebugThemePalette(ResourceDictionary resources, string filePath)
    {
        _resources = ThemeResources.Palette(resources);
        FilePath = Path.GetFullPath(filePath);
    }

    public static DebugThemePalette CreateDefault(ResourceDictionary resources) => new(resources,
        Path.Combine(ApplicationDataPaths.CreateDefault().CurrentDirectory, "Debug", "theme-palette.json"));

    public Color Get(string key) => ((ThemeColour)_resources[key]).Value;
    public double RoleOpacity(string role) => ((SolidColorBrush)_resources[role]).Opacity;

    public void Set(string key, Color colour)
    {
        if (!Definitions.Any(d => d.Key == key)) throw new ArgumentException("Unknown palette colour.", nameof(key));
        ((ThemeColour)_resources[key]).Value = colour;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        var colours = Definitions.ToDictionary(d => d.Key, d => Format(Get(d.Key)));
        var roles = Definitions.SelectMany(d => d.Roles.Select(role => (role, d.Key)))
            .ToDictionary(r => r.role, r => r.Key);
        var opacities = roles.Keys.Where(role => RoleOpacity(role) != 1)
            .ToDictionary(role => role, RoleOpacity);
        var document = new PaletteDocument(2, "WPF #AARRGGBB", DateTimeOffset.UtcNow, colours, roles, opacities);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Validate the entire file first: malformed or old data never partially changes the UI.
    public string? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<PaletteDocument>(File.ReadAllText(FilePath), JsonOptions);
            if (document is not { SchemaVersion: 1 or 2, Colours: not null })
                throw new FormatException("Unsupported palette file.");
            var colours = new Dictionary<string, Color>();
            foreach (var (key, value) in document.Colours)
            {
                if (value is not { Length: 9 } || value[0] != '#' || !value[1..].All(Uri.IsHexDigit))
                    throw new FormatException($"Invalid colour: {key}.");
                // Preserve v1's surviving colours; retired standalone swatches now
                // derive from their shared source rather than overriding Ryan's links.
                if (document.SchemaVersion == 1 && key is "Palette.SelectedText" or "Palette.ErrorBanner" or
                    "Palette.BadgeBorder" or "Palette.HintBorder" or "Palette.BoneSelection") continue;
                if (!Definitions.Any(d => d.Key == key)) throw new FormatException($"Unknown colour: {key}.");
                colours.Add(key, (Color)ColorConverter.ConvertFromString(value));
            }
            foreach (var (key, colour) in colours) Set(key, colour);
            return "Loaded saved debug colours.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or FormatException or ArgumentException)
        {
            return $"Saved palette was not loaded: {exception.Message}";
        }
    }

    internal static string Format(Color colour) => $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private sealed record PaletteDocument(int SchemaVersion, string ColourFormat, DateTimeOffset SavedAtUtc,
        Dictionary<string, string> Colours, Dictionary<string, string> LinkedRoles,
        Dictionary<string, double>? RoleOpacities);
}

internal sealed class DebugThemeColour(DebugThemePalette palette, DebugThemePalette.Definition definition) : ObservableObject
{
    public string Key => definition.Key;
    public string Group => definition.Group;
    public string Name => definition.Name;
    public string Roles => string.Join(", ", definition.Roles.Select(role =>
        palette.RoleOpacity(role) == 1 ? role : $"{role} ({palette.RoleOpacity(role) * 100:0.#}% opacity)"));
    public Color Colour => palette.Get(Key);
    public string Hex => DebugThemePalette.Format(Colour);
    public SolidColorBrush Brush => new(Colour);
    public void Refresh()
    {
        OnPropertyChanged(nameof(Brush));
        OnPropertyChanged(nameof(Hex));
    }
}
