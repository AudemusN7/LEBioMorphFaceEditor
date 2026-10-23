using System.IO;
using System.Numerics;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Rendering;

/// <summary>
/// The current renderable CPU scene. Once attached, apply changes through the
/// renderer so its derived GPU buffers are updated with this state.
/// </summary>
public sealed class HeadPreviewSceneState
{
    internal const int MaximumBoneCount = 128;
    private IReadOnlyList<Mesh> _meshes = [];

    public bool IsLoaded { get; private set; }
    public bool HasAnimatedMaterials => _meshes.Any(mesh => mesh.Sections.Any(section =>
        section.Material.PreviewModel == HumanMaterialPreviewModel.VisorLens));

    internal IReadOnlyList<Mesh> Meshes => _meshes;
    internal IReadOnlyList<Matrix4x4> SkinningPalette { get; private set; } = [];

    public void SetScene(HeadPreviewScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var palette = scene.SkinningPalette ?? [];
        ValidateSkinningPalette(palette);
        // The previous renderer used one case-insensitive material dictionary:
        // the last material with a shared key supplied every matching section.
        var meshes = scene.Meshes.Where(mesh => mesh.Vertices.Count > 0 && mesh.Indices.Count > 0).ToArray();
        var materials = new Dictionary<string, HeadPreviewMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in meshes)
        foreach (var section in mesh.Sections)
        {
            materials[section.Material.Key] = section.Material;
        }
        _meshes = meshes.Select(mesh => new Mesh(mesh, mesh.Sections.Select(section =>
            materials.TryGetValue(section.Material.Key, out var material) && !ReferenceEquals(section.Material, material)
                ? section with { Material = material }
                : section).ToArray())).ToArray();
        SkinningPalette = palette;
        IsLoaded = true;
    }

    public void ClearScene()
    {
        _meshes = [];
        SkinningPalette = [];
        IsLoaded = false;
    }

    public void UpdateDeformation(HeadPreviewDeformationUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!IsLoaded)
        {
            throw new InvalidOperationException("A preview scene must be loaded before deformation can be updated.");
        }
        var mesh = RequireMesh(update.MeshName);
        ValidateVertices(mesh, update.Vertices);
        var attachments = update.AttachmentUpdates.Select(attachment =>
        {
            var attachmentMesh = RequireMesh(attachment.MeshName);
            ValidateVertices(attachmentMesh, attachment.Vertices);
            return (Mesh: attachmentMesh, attachment.Vertices);
        }).ToArray();
        ValidateSkinningPalette(update.SkinningPalette);

        mesh.Vertices = update.Vertices;
        foreach (var attachment in attachments)
        {
            attachment.Mesh.Vertices = attachment.Vertices;
        }
        SkinningPalette = update.SkinningPalette;
    }

    public void UpdateMaterials(IReadOnlyDictionary<string, HeadPreviewMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if (!IsLoaded)
        {
            throw new InvalidOperationException("A preview scene must be loaded before materials can be updated.");
        }
        var replacements = new Dictionary<string, HeadPreviewMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials.Values)
        {
            replacements[material.Key] = material;
        }
        foreach (var mesh in _meshes)
        {
            HeadPreviewSection[]? sections = null;
            for (var index = 0; index < mesh.Sections.Count; index++)
            {
                var section = mesh.Sections[index];
                if (replacements.TryGetValue(section.Material.Key, out var material) &&
                    !ReferenceEquals(section.Material, material))
                {
                    sections ??= mesh.Sections.ToArray();
                    sections[index] = section with { Material = material };
                }
            }
            if (sections is not null) mesh.Sections = sections;
        }
    }

    private Mesh RequireMesh(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _meshes.SingleOrDefault(mesh => string.Equals(mesh.Name, name, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Preview mesh '{name}' is not loaded.");
    }

    private static void ValidateVertices(Mesh mesh, IReadOnlyList<HeadPreviewVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Count != mesh.Vertices.Count)
        {
            throw new InvalidDataException($"Preview mesh '{mesh.Name}' expects {mesh.Vertices.Count} vertices; received {vertices.Count}.");
        }
    }

    private static void ValidateSkinningPalette(IReadOnlyList<Matrix4x4> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (palette.Count > MaximumBoneCount)
        {
            throw new InvalidDataException($"Skinning palette contains {palette.Count} bones; the renderer supports {MaximumBoneCount}.");
        }
    }

    internal sealed class Mesh(HeadPreviewMesh mesh, IReadOnlyList<HeadPreviewSection> sections)
    {
        public string Name { get; } = mesh.Name;
        public IReadOnlyList<HeadPreviewVertex> Vertices { get; internal set; } = mesh.Vertices;
        public IReadOnlyList<uint> Indices { get; } = mesh.Indices;
        public IReadOnlyList<HeadPreviewSection> Sections { get; internal set; } = sections;
        public bool IsAttachment { get; } = mesh.IsAttachment;
        public bool ApplySkinning { get; } = mesh.ApplySkinning;
    }
}
