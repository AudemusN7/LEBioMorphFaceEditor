using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

/// <summary>
/// Adapts the shared material RON codec for face workspaces. Face imports apply
/// material parameters and optional attachment references; geometry and bones
/// are never part of the returned payload.
/// </summary>
public static class FaceMaterialFileService
{
    public sealed record PreparedImport(
        MorphFaceMaterialData Parameters,
        bool IsTse,
        IReadOnlyList<string> Warnings,
        string? HairMesh = null,
        IReadOnlyList<string>? AccessoryMeshes = null);

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

        var input = RonInputSnapshot.Parse(text);
        var attachments = MaterialRonCodec.ReadAttachmentReferences(input);
        if (input.Root.ContainsKey("format"))
        {
            var scopes = MaterialRonCodec.ReadMfeParameterScopes(input);
            var (parameters, warnings) = ReadFaceParameters(scopes);
            return new(parameters, false, warnings, attachments.HairMesh, attachments.AccessoryMeshes);
        }
        var tseParameters = MaterialRonCodec.ReadTse(input);
        var taggedHead = TseHeadMorphRon.ReadProvenance(input)?.Producer == RonExportProducer.MFE;
        if (!allowTse && !taggedHead && MaterialRonCodec.IdentifyMaterialFile(input) != MaterialRonCodec.MaterialFileKind.Tse)
            throw new InvalidDataException(Loc.Text("loc_Services_FaceMaterialFile_OnlyTaggedMfeRonsAccepted"));
        return new(tseParameters, !taggedHead, [], attachments.HairMesh, attachments.AccessoryMeshes);
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
                else warnings.Add(Loc.Format("loc_Services_FaceMaterialFile_AmbiguousParameterSkipped", kind, group.Key));
            }
            return values.ToArray();
        }

        return (new MorphFaceMaterialData(
            Unique(scopes.Values.SelectMany(value => value.Scalars), value => value.Name, "scalar", warnings),
            Unique(scopes.Values.SelectMany(value => value.Vectors), value => value.Name, "vector", warnings),
            Unique(scopes.Values.SelectMany(value => value.Textures), value => value.Name, "texture", warnings)),
            warnings);
    }

}
