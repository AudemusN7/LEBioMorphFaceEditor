namespace MorphFaceEditor.Localisation;

/// <summary>Shipped dictionaries compiled from the single editable source catalogue.</summary>
public static class EmbeddedCatalogue
{
    public static CatalogueData Read(string language)
    {
        using var stream = typeof(EmbeddedCatalogue).Assembly.GetManifestResourceStream(
            $"MorphFaceEditor.Localisation.Strings.{language}.xaml")
            ?? throw new InvalidOperationException($"The embedded localisation catalogue '{language}' is missing.");
        return StringCatalogue.Read(stream);
    }
}
