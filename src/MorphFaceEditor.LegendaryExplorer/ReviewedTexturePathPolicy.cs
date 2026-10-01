namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Reviewed corpus aliases for exact installed texture identities.</summary>
public static class ReviewedTexturePathPolicy
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        const string corpusRoot = "BIOG_HMM_HED_PROMorph_R.";
        return path.StartsWith(corpusRoot, StringComparison.OrdinalIgnoreCase)
            ? "BIOG_HMM_HED_PROMorph." + path[corpusRoot.Length..]
            : path;
    }
}
