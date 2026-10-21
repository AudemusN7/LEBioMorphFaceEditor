using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Randomisation;

/// <summary>Required members are shared by installed-donor projection and atomic texture decoding.</summary>
public static class MaterialTextureFamilyPolicy
{
    public static bool IsRequiredMember(string family, string parameterName)
    {
        family = family[(family.IndexOf('|') + 1)..];
        parameterName = MaterialParameterControlKey.ParameterName(parameterName);
        return family.Equals("human-face", StringComparison.OrdinalIgnoreCase)
            ? parameterName is "HED_Diff" or "HED_Norm"
            : family.Equals("human-scalp", StringComparison.OrdinalIgnoreCase)
                ? parameterName is "HED_Scalp_Diff" or "HED_Scalp_Norm"
                : family.EndsWith("-face", StringComparison.OrdinalIgnoreCase)
                    ? parameterName.EndsWith("_HED_Diff", StringComparison.OrdinalIgnoreCase) ||
                      parameterName.EndsWith("_HED_Norm", StringComparison.OrdinalIgnoreCase)
                    : true;
    }

    public static bool IsInScope(IReadOnlyDictionary<string, string> family,
        IReadOnlySet<string> requestedTextureNames, IReadOnlySet<string> availableTextureNames)
    {
        var applicable = family.Keys.Where(availableTextureNames.Contains).ToArray();
        return applicable.Length > 0 && applicable.All(requestedTextureNames.Contains);
    }
}
