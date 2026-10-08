namespace MorphFaceEditor.QA;

internal static class QaDisplay
{
    internal static string Utility(string value) => value switch
    {
        "load" => "Load", "material-ron" => "Material RON", "clipboard" => "Clipboard",
        "undo-redo" => "Undo / Redo", "benchmark" => "Benchmark", "conversion" => "Conversion",
        "npc-randomisation" => "NPC Randomisation", "player-randomisation" => "Player Randomisation",
        "custom-randomisation" => "Custom Randomisation", "mesh-psk" => "PSK Mesh",
        "mesh-gltf" => "glTF Mesh", "pcc-audit" => "PCC Audit", "runner-self-check" => "Runner Checks",
        "custom-human-materials" => "Custom Human Materials", "custom-human-hair" => "Custom Human Hair",
        "native-human-hair" => "Native Human Hair", "cc-scalp-switch" => "CC Scalp Switch",
        "player-cc-scalp" => "Player CC Scalp", "kaidan-preview" => "Kaidan Preview",
        _ => value
    };

    internal static string Case(string id)
    {
        var parts = id.Split('/');
        var utility = Utility(parts[0]);
        if (parts.Length == 1) return utility;
        var label = parts[1].Replace("-to-", " → ", StringComparison.OrdinalIgnoreCase);
        if (parts.Length == 2)
            label = string.Join(" · ", label.Split('-').Select(token => token.ToLowerInvariant() switch
            {
                "player" => "Player", "npc" => "NPC", _ => token.ToUpperInvariant()
            }));
        else if (parts[0] is "custom-human-hair" or "native-human-hair")
            label = string.Join(" · ", label.Split('-').Select(token => token.ToUpperInvariant())) + " · " +
                (parts[2] switch
                {
                    "human-hair" => "Hair",
                    "human-iconic-femshep-hair" or "human-advanced-hair" => "Advanced Hair",
                    "human-james-hair" => "James Vega Hair",
                    _ => parts[2]
                });
        else label += " · " + parts[2].Split('.')[0].ToUpperInvariant();
        return utility + " · " + label;
    }
}
