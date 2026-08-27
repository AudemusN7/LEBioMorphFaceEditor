namespace MorphFaceEditor.Presentation;

public static class PreviewCameraGrouping
{
    public static string ForProfile(string profileKey)
    {
        var species = SpeciesForProfile(profileKey);
        return species is "human-male" or "human-female" or "asari" or "batarian"
            ? "humanoid"
            : species;
    }

    public static string SpeciesForProfile(string profileKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        var separator = profileKey.IndexOf('-');
        return separator > 0 && profileKey.AsSpan(0, separator).StartsWith("le", StringComparison.OrdinalIgnoreCase)
            ? profileKey[(separator + 1)..].ToLowerInvariant()
            : profileKey.ToLowerInvariant();
    }
}
