using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

/// <summary>
/// Adapts the shared material RON codec for face workspaces. Face imports apply
/// material parameters only; geometry, bones and preview attachments are never
/// part of the returned payload.
/// </summary>
public static class FaceMaterialFileService
{
    public sealed record PreparedImport(
        MorphFaceMaterialData Parameters,
        bool IsTse,
        IReadOnlyList<string> Warnings);

    /// <summary>Captures face material values without requiring MESH slot assignments.</summary>
    public static MeshMaterialDocument Capture(
        MorphFaceGame game,
        string name,
        MorphFaceMaterialData data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(data);

        var scoped = MeshMaterialInterchange.Split(data)
            .ToDictionary(value => value.Key, value => value.Value,
                StringComparer.OrdinalIgnoreCase);
        AddUnscopedValues(scoped, data);
        if (scoped.Count == 0)
        {
            scoped["face"] = new MorphFaceMaterialData([], [], []);
        }

        return new(game.ToString(), name, [], scoped);
    }

    /// <summary>
    /// Reads an MFE material document or a full tagged MFE/LEX head RON. An
    /// untagged TSE head is accepted only for the Player route.
    /// </summary>
    public static PreparedImport Read(string text, bool allowTse)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            var scopes = MaterialRonCodec.ReadMfeParameterScopes(text);
            var (parameters, warnings) = ReadFaceParameters(scopes);
            return new(parameters, false, warnings);
        }
        catch (InvalidDataException mfeError)
        {
            MorphFaceMaterialData parameters;
            try
            {
                parameters = MaterialRonCodec.ReadTse(text);
            }
            catch (InvalidDataException)
            {
                throw mfeError;
            }

            var taggedHead = HasCompleteMfeProvenanceHeader(text);
            if (!allowTse && !taggedHead)
            {
                throw new InvalidDataException(
                    "This workspace accepts MFE material RONs only; an untagged TSE RON is supported in Player workspaces.");
            }

            return new(parameters, !taggedHead, []);
        }
    }

    private static void AddUnscopedValues(
        IDictionary<string, MorphFaceMaterialData> scoped,
        MorphFaceMaterialData data)
    {
        var unscoped = new MorphFaceMaterialData(
            data.Scalars.Where(value => MaterialParameterControlKey.ScopeKey(value.Name) is null).ToArray(),
            data.Vectors.Where(value => MaterialParameterControlKey.ScopeKey(value.Name) is null).ToArray(),
            data.Textures.Where(value => MaterialParameterControlKey.ScopeKey(value.Name) is null).ToArray());
        if (unscoped.Scalars.Count == 0 && unscoped.Vectors.Count == 0 && unscoped.Textures.Count == 0)
        {
            return;
        }

        // One face workspace has one material parameter namespace. The neutral
        // scope keeps the portable file valid while preserving raw shader names.
        if (scoped.TryGetValue("face", out var existing))
        {
            scoped["face"] = new MorphFaceMaterialData(
                existing.Scalars.Concat(unscoped.Scalars).ToArray(),
                existing.Vectors.Concat(unscoped.Vectors).ToArray(),
                existing.Textures.Concat(unscoped.Textures).ToArray());
        }
        else
        {
            scoped["face"] = unscoped;
        }
    }

    private static (MorphFaceMaterialData Parameters, IReadOnlyList<string> Warnings) ReadFaceParameters(
        IReadOnlyDictionary<string, MorphFaceMaterialData> scopes)
    {
        // A face has one archetype. A single source scope identifies portable
        // raw shader names, regardless of which workspace exported them.
        if (scopes.Count == 1) return (scopes.Values.Single(), []);

        var warnings = new List<string>();
        // A mixed MESH file can define the same shader name in several racial
        // scopes. Import every unambiguous name and leave conflicting ones alone.
        static T[] Unique<T>(IEnumerable<T> source, Func<T, string> name, string kind,
            ICollection<string> warnings)
        {
            var values = new List<T>();
            foreach (var group in source.GroupBy(name, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() == 1) values.Add(group.First());
                else warnings.Add($"Skipped {kind} '{group.Key}' because multiple source material scopes define it.");
            }
            return values.ToArray();
        }

        return (new MorphFaceMaterialData(
            Unique(scopes.Values.SelectMany(value => value.Scalars), value => value.Name, "scalar", warnings),
            Unique(scopes.Values.SelectMany(value => value.Vectors), value => value.Name, "vector", warnings),
            Unique(scopes.Values.SelectMany(value => value.Textures), value => value.Name, "texture", warnings)),
            warnings);
    }

    private static bool HasCompleteMfeProvenanceHeader(string text)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mfeProducer = false;
        foreach (var line in text.Split('\n'))
        {
            var body = line.TrimStart();
            if (!body.StartsWith("//", StringComparison.Ordinal)) continue;
            body = body[2..].TrimStart();
            foreach (var field in new[] { "Exported from", "Game", "Archetype", "Player Morph" })
            {
                if (body.StartsWith(field + ":", StringComparison.OrdinalIgnoreCase))
                {
                    fields.Add(field);
                    if (field.Equals("Exported from", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = body[(field.Length + 1)..].Trim();
                        mfeProducer = value.StartsWith("MFE ", StringComparison.OrdinalIgnoreCase) &&
                                      value.Length > "MFE ".Length;
                    }
                    break;
                }
            }
        }
        return mfeProducer && fields.Count == 4;
    }
}
