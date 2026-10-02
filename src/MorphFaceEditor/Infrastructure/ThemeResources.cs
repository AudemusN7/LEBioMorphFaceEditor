using System.Windows;

namespace MorphFaceEditor.Infrastructure;

internal static class ThemeResources
{
    // Find the shared colour sources rather than replacing semantic brush roles.
    internal static ResourceDictionary Palette(ResourceDictionary resources) =>
        Find(resources) ?? throw new InvalidOperationException("The editor palette has not been loaded.");

    private static ResourceDictionary? Find(ResourceDictionary resources)
    {
        if (resources.Keys.Cast<object>().Contains("Palette.Window")) return resources;
        foreach (var merged in resources.MergedDictionaries.Reverse())
        {
            var match = Find(merged);
            if (match is not null) return match;
        }
        return null;
    }
}
