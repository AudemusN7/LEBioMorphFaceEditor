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
        else label += " · " + parts[2].Split('.')[0].ToUpperInvariant();
        return utility + " · " + label;
    }
}
