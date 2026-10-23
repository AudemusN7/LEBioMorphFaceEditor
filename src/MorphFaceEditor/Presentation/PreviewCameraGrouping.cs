using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Presentation;

public static class PreviewCameraGrouping
{
    public static string ForProfile(MorphFaceProfile profile)
    {
        var species = profile.Archetype.ToLowerInvariant();
        return species is "human-male" or "human-female" or "asari" or "batarian"
            ? "humanoid"
            : species;
    }
}
