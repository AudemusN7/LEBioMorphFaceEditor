using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.QA;

internal static class QaPlayerConversionTextures
{
    // Independently verified installed Texture2D paths, including the authored stacked face normal.
    internal static MorphFaceMaterialData Expected(MorphFaceGame game, StandalonePlayerSex sex, bool genericScalp = false)
    {
        const string femaleHead = "BIOG_HMF_HED_PROMorph_R.";
        var female = sex == StandalonePlayerSex.Female;
        var le3Female = female && game == MorphFaceGame.LE3;
        var group = female ? femaleHead + (le3Female ? "PROShepard." : "PROSheppard.") : "BIOG_HMM_HED_PROMorph.Sheppard.";
        var prefix = female ? "HMF_HED_" : "HMM_HED_";
        string Skin(string part) => group + prefix + (le3Female ? $"PROShepard_{part}" : $"PROSheppard_{part}_Stack");
        static TextureMaterialOverride Row(string name, string path) => new(name, new AssetIdentity("", path, 0, "Texture2D"));

        var useGenericScalp = female && genericScalp;
        var rows = new List<TextureMaterialOverride>
        {
            Row("HED_Diff", Skin("Face_Diff")),
            Row("HED_Norm", group + prefix + "PROSheppard_Face_Norm_Stack"),
            Row("HED_Scalp_Diff", useGenericScalp ? femaleHead + "Diffuse.HMF_HED_PROBase_Scalp_Bald_Diff" : Skin("Scalp_Diff")),
            Row("HED_Scalp_Norm", useGenericScalp ? femaleHead + "Normal.HMF_HED_PROBase_Scalp_Norm" : Skin("Scalp_Norm"))
        };
        if (female) rows.Add(Row("HED_Lash_Diff", group + "EyeLashMASK_Iconic_Femshep"));
        return new MorphFaceMaterialData([], [], rows);
    }

    internal static void RequireOverrides(MorphFaceMaterialData expected, MorphFaceMaterialData actual, string stage)
    {
        foreach (var row in expected.Textures)
        {
            var matches = actual.Textures.Where(value => value.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            QaContext.Require(matches.Length == 1, $"{stage}: expected one {row.Name} override, found {matches.Length}.");
            var source = matches[0].TextureReference;
            QaContext.Require(source is not null && QaTextureIdentity.SameTexture(row.TextureReference!, source),
                $"{stage}: {row.Name} expected '{row.TextureReference!.InstancedPath}', got '{source?.InstancedPath ?? "<missing>"}'.");
        }
    }

    internal static IReadOnlyDictionary<string, string> RequireApplied(MorphFaceMaterialData expected, MorphFacePreviewLoadResult preview)
    {
        var sections = preview.Scene.Meshes.Single(mesh => !mesh.IsAttachment).Sections;
        var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        QaContext.Require(expected.Textures.Count > 0, "Player conversion has no expected texture bindings.");
        foreach (var row in expected.Textures)
        {
            var family = row.Name switch
            {
                "HED_Diff" or "HED_Norm" => HeadMaterialFamily.Skin,
                "HED_Scalp_Diff" or "HED_Scalp_Norm" => HeadMaterialFamily.Scalp,
                "HED_Lash_Diff" => HeadMaterialFamily.Lashes,
                _ => throw new InvalidDataException($"Unknown Player texture role '{row.Name}'.")
            };
            var drawn = sections.Where(s => s.IndexCount > 0 && s.Material.Family == family).ToArray();
            QaContext.Require(drawn.Length > 0, $"The reopened head has no {family} section for {row.Name}.");
            foreach (var section in drawn)
            {
                var material = preview.Loaded.Materials.Materials[section.Material.Key];
                var bindings = HeadPreviewMaterialBindings.Create(section.Material);
                var selected = row.Name.EndsWith("_Norm", StringComparison.Ordinal) ? bindings.Normal : bindings.Diffuse;
                var label = $"{family} slot {section.MaterialSlot}, {row.Name}";
                if (selected is null || !selected.ParameterName.Equals(row.Name, StringComparison.OrdinalIgnoreCase) ||
                    !material.Textures.TryGetValue(row.Name, out var resolved))
                    throw new InvalidDataException($"{label}: renderer selected '{selected?.ParameterName ?? "<missing>"}'.");
                var decoded = resolved.Texture;
                var actualPath = QaTextureIdentity.CanonicalPath(decoded.Source);
                QaContext.Require(QaTextureIdentity.SameTexture(row.TextureReference!, decoded.Source),
                    $"{label}: expected '{row.TextureReference!.InstancedPath}', got '{actualPath}'.");
                QaContext.Require(selected.Key == decoded.CacheKey && selected.Width > 0 && selected.Height > 0 &&
                    selected.Rgba8.LongLength == 4L * selected.Width * selected.Height &&
                    selected.Rgba8.AsSpan().SequenceEqual(decoded.Rgba8),
                    $"{label}: renderer did not bind the decoded pixels of '{actualPath}'.");
                applied[row.Name] = actualPath;
            }
        }
        return applied;
    }
}
