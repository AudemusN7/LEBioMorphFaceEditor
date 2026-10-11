namespace MorphFaceEditor.Models;

public sealed record BioMorphFaceListItem(
    int UIndex,
    string InstancedPath,
    string ObjectName,
    string ProfileTag = "",
    string ProfileTagColor = "#66717D",
    string ProfileKey = "")
{
    public bool SupportsMorphRonExport { get; init; } = true;
    public string DisplayName => ObjectName;
}
