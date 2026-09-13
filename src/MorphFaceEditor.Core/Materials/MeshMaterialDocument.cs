using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Materials;

/// <summary>Portable material authoring data; the source mesh and preview attachments are not embedded.</summary>
public sealed record MeshMaterialDocument(
    string Game,
    string MeshName,
    IReadOnlyList<MeshMaterialSlotData> Slots,
    IReadOnlyDictionary<string, MorphFaceMaterialData> Parameters)
{
    public const int CurrentVersion = 1;
}

/// <summary>A source slot's assignment. Empty material identity denotes an unassigned slot.</summary>
public sealed record MeshMaterialSlotData(
    int Index,
    string SlotName,
    string MaterialId,
    string MaterialName,
    string Scope,
    HeadMaterialFamily Family);

/// <summary>Maps portable raw parameter names to the editor's species scopes without changing shader names.</summary>
public static class MeshMaterialInterchange
{
    public static bool CanExportTse(CustomMaterialWorkspace workspace) =>
        workspace.Assignments.Count > 0 && workspace.Assignments.All(value => IsHuman(value.Option));

    public static bool CanImportTse(CustomMaterialWorkspace workspace) =>
        workspace.Assignments.Any(value => IsHuman(value.Option));

    public static bool IsHuman(CustomMaterialAssignmentOption option) =>
        option.EffectiveParameterScopeKey.Equals("human", StringComparison.OrdinalIgnoreCase);

    public static MorphFaceMaterialData Scope(MorphFaceMaterialData data, string scope) => new(
        data.Scalars.Select(value => value with { Name = MaterialParameterControlKey.Create(scope, value.Name) }).ToArray(),
        data.Vectors.Select(value => value with { Name = MaterialParameterControlKey.Create(scope, value.Name) }).ToArray(),
        data.Textures.Select(value => value with { Name = MaterialParameterControlKey.Create(scope, value.Name) }).ToArray());

    public static IReadOnlyDictionary<string, MorphFaceMaterialData> Split(MorphFaceMaterialData data)
    {
        var scopes = data.Scalars.Select(value => value.Name).Concat(data.Vectors.Select(value => value.Name))
            .Concat(data.Textures.Select(value => value.Name))
            .Select(MaterialParameterControlKey.ScopeKey).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return scopes.ToDictionary(scope => scope, scope => new MorphFaceMaterialData(
            data.Scalars.Where(value => InScope(value.Name, scope))
                .Select(value => value with { Name = MaterialParameterControlKey.ParameterName(value.Name) }).ToArray(),
            data.Vectors.Where(value => InScope(value.Name, scope))
                .Select(value => value with { Name = MaterialParameterControlKey.ParameterName(value.Name) }).ToArray(),
            data.Textures.Where(value => InScope(value.Name, scope))
                .Select(value => value with { Name = MaterialParameterControlKey.ParameterName(value.Name) }).ToArray()),
            StringComparer.OrdinalIgnoreCase);
    }

    public static MorphFaceMaterialData Flatten(IReadOnlyDictionary<string, MorphFaceMaterialData> scopes)
    {
        var data = scopes.Select(value => Scope(value.Value, value.Key)).ToArray();
        return new(data.SelectMany(value => value.Scalars).ToArray(),
            data.SelectMany(value => value.Vectors).ToArray(), data.SelectMany(value => value.Textures).ToArray());
    }

    private static bool InScope(string name, string scope) =>
        string.Equals(MaterialParameterControlKey.ScopeKey(name), scope, StringComparison.OrdinalIgnoreCase);
}
