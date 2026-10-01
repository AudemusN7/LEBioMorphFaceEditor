using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static Task EditMaterial(QaContext c, FaceEditorViewModel editor, float delta)
    {
        var random = c.RandomFor("material-edit/" + c.Current.Stages.Count);
        delta *= 0.5f + random.NextSingle();
        var scalar = editor.Material.Scalars.Count > 0
            ? QaCoverageSelection.Pick(editor.Material.Scalars, random) : null;
        if (scalar is not null)
            return editor.MergeMaterialDataAsync(new MorphFaceMaterialData([new(scalar.Name, scalar.Value + delta)], [], []));
        var vector = QaCoverageSelection.Pick(editor.Material.Vectors, random);
        return editor.MergeMaterialDataAsync(new MorphFaceMaterialData([], [new(vector.Name, vector.Value + new Vector4(delta, 0, 0, 0))], []));
    }

    private static void ApplyFirstHistoryEdit(QaContext c, FaceEditorViewModel editor)
    {
        var random = c.RandomFor("history-edit");
        var editable = editor.Features.Where(value => value.IsEditable).ToArray();
        var feature = editable.Length > 0 ? QaCoverageSelection.Pick(editable, random) : null;
        if (feature is not null)
        {
            feature.BeginEdit(); feature.Value += 0.005f + random.NextSingle() * 0.02f; feature.EndEdit();
            c.Current.Evidence["firstHistoryAction"] = "semantic morph slider: " + feature.Name;
            return;
        }
        var scalar = QaCoverageSelection.Pick(editor.Material.Scalars, random);
        scalar.BeginEdit(); scalar.Value += 0.005f + random.NextSingle() * 0.02f; scalar.EndEdit();
        c.Current.Evidence["firstHistoryAction"] = "material-only semantic scalar: " + scalar.Name;
        c.Current.Evidence["geometryScope"] = "Morph controls unavailable; original geometry must remain preserved.";
    }
}
