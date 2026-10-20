using System.Text;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Services;

/// <summary>Shared identification and target fields for MFE RON exports.</summary>
public sealed record MfeRonMetadata(string Format, string Game, MaterialFaceTarget? Target)
{
    public const string Head = "MorphFaceEditor.Head";
    public const string Morphs = "MorphFaceEditor.Morphs";
    public const string Materials = "MorphFaceEditor.Materials";
    public const int Version = 1;

    public static MfeRonMetadata? TryRead(RonInputSnapshot input)
    {
        var root = input.Root;
        if (root.GetValueOrDefault("format") is not string format || format is not (Head or Morphs or Materials) ||
            root.GetValueOrDefault("version") is not float version || version != Version ||
            root.GetValueOrDefault("game") is not string game || game is not ("LE1" or "LE2" or "LE3")) return null;
        MaterialFaceTarget? target = null;
        if (root.ContainsKey("target_profile") || root.ContainsKey("target_kind"))
        {
            if (root.GetValueOrDefault("target_profile") is not string profile || string.IsNullOrWhiteSpace(profile) ||
                root.GetValueOrDefault("target_kind") is not string kind || kind is not ("Player" or "NPC")) return null;
            target = new(profile, kind == "Player");
        }
        return new(format, game, target);
    }

    public void Write(StringBuilder output)
    {
        output.AppendLine(Format switch
        {
            Head => "// Full head; authored geometry, bones, materials and attachments.",
            Morphs => "// Morph features only; no authored geometry, bone offsets or materials.",
            _ => "// Material settings only; no authored geometry or bone offsets."
        });
        output.AppendLine("(");
        output.AppendLine($"    format: \"{Format}\",\n    version: {Version},\n    game: \"{Game}\",");
        if (Target is { } target)
            output.AppendLine($"    target_profile: \"{target.ProfileKey}\",\n    target_kind: \"{(target.PlayerMorph ? "Player" : "NPC")}\",");
    }
}
