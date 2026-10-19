using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Head and features-only schema over the shared RON syntax snapshot.</summary>
internal static class HeadMorphRonSchema
{
    internal static HeadMorphPayload Read(RonInputSnapshot input, bool featuresOnly)
    {
        string hair = "None";
        IReadOnlyList<string> accessories = [];
        IReadOnlyList<MorphFeatureValue> features = [];
        IReadOnlyList<BoneTranslation> bones = [];
        var lods = new Vector3[4][];
        IReadOnlyList<ScalarMaterialOverride> scalars = [];
        IReadOnlyList<VectorMaterialOverride> colours = [];
        IReadOnlyList<TextureMaterialOverride> textures = [];

        if (input.Root.Opening != '(') throw Error("Expected '('.");
        foreach (var (field, value) in input.Root)
        {
            switch (field)
            {
                case "hair_mesh": hair = Text(value); break;
                case "accessory_mesh": accessories = Sequence(value, '[').Select(Text).ToArray(); break;
                case "morph_features": features = Map(value, '{')
                    .Select(v => new MorphFeatureValue(v.Key, Number(v.Value))).ToArray(); break;
                case "offset_bones": bones = Map(value, '{')
                    .Select(v => new BoneTranslation(v.Key, Vector3Value(v.Value))).ToArray(); break;
                case "lod0_vertices": lods[0] = Vertices(value); break;
                case "lod1_vertices": lods[1] = Vertices(value); break;
                case "lod2_vertices": lods[2] = Vertices(value); break;
                case "lod3_vertices": lods[3] = Vertices(value); break;
                case "scalar_parameters": scalars = Map(value, '{')
                    .Select(v => new ScalarMaterialOverride(v.Key, Number(v.Value))).ToArray(); break;
                case "vector_parameters": colours = Map(value, '{')
                    .Select(v => new VectorMaterialOverride(v.Key, Vector4Value(v.Value))).ToArray(); break;
                case "texture_parameters": textures = Map(value, '{').Select(v =>
                {
                    var path = Text(v.Value);
                    return new TextureMaterialOverride(v.Key, string.IsNullOrWhiteSpace(path) ||
                        path.Equals("None", StringComparison.OrdinalIgnoreCase) ? null :
                        new AssetIdentity(string.Empty, path, 0, "Texture2D"));
                }).ToArray(); break;
                default: throw Error($"Unsupported HeadMorph field '{field}'.");
            }
        }
        var lastLod = Array.FindLastIndex(lods, value => value is { Length: > 0 });
        var baked = lods.Take(lastLod + 1).Select(value => value ?? []).ToArray();
        if ((!featuresOnly && baked.Length == 0) || baked.Any(value => value.Length == 0))
            throw Error("HeadMorph LOD arrays must begin at LOD0 and remain contiguous.");
        if (featuresOnly && (baked.Length != 0 || bones.Count != 0 || accessories.Count != 0 ||
            hair != "None" || scalars.Count != 0 || colours.Count != 0 || textures.Count != 0))
            throw Error("A Morphs-only RON must contain only morph features.");
        return new(hair, accessories, new(features, bones, baked), new(scalars, colours, textures));

        InvalidDataException Error(string message) =>
            new($"Invalid Trilogy Save Editor RON near character {input.Source.Length}: {message}");
        string Text(object value) => value as string ?? throw Error("Expected '\"'.");
        float Number(object value) => value is float number ? number : throw Error("Expected a finite floating-point number.");
        RonMap Map(object value, char opening) => value is RonMap map && map.Opening == opening
            ? map : throw Error($"Expected '{opening}'.");
        RonSequence Sequence(object value, char opening) => value is RonSequence sequence && sequence.Opening == opening
            ? sequence : throw Error($"Expected '{opening}'.");
        RonSequence Tuple(object value, int components)
        {
            var sequence = Sequence(value, '(');
            if (sequence.HasOmittedCommas) throw Error("Expected ','.");
            if (sequence.Count != components) throw Error("Expected ')'.");
            return sequence;
        }
        Vector3[] Vertices(object value) => Sequence(value, '[').Select(Vector3Value).ToArray();
        Vector3 Vector3Value(object value)
        {
            if (value is RonMap)
            {
                var map = Map(value, '(');
                if (map.Count != 3 || map.Keys.Any(key => key is not ("x" or "y" or "z")))
                    throw Error("A vector must contain exactly x, y and z components.");
                return new(Number(map["x"]), Number(map["y"]), Number(map["z"]));
            }
            var tuple = Tuple(value, 3);
            return new(Number(tuple[0]), Number(tuple[1]), Number(tuple[2]));
        }
        Vector4 Vector4Value(object value)
        {
            var tuple = Tuple(value, 4);
            return new(Number(tuple[0]), Number(tuple[1]), Number(tuple[2]), Number(tuple[3]));
        }
    }
}
