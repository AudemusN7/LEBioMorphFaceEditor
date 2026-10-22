using MorphFaceEditor.Core.Deformation;
using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiProfileTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("unsupported profiles keep unsafe geometry controls unavailable", UnsupportedProfilesKeepGeometryClosed),
        new("malformed LE3 HMF hair and inert HMM eye metadata remain hidden", MalformedAndInertTargetsStayHidden)
    ];

    private static void UnsupportedProfilesKeepGeometryClosed()
    {
        using var reader = new MorphFacePackageReader();
        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles;
        foreach (var (key, bones) in new[]
                 { ("le2-vorcha", true), ("le2-female-turian", false), ("le2-female-batarian", false) })
        {
            var profile = profiles.Single(value => value.Key == key);
            using var editor = CreateEditor(reader, profile.GetUiProfile(), key, ignoresAuthoredGeometry: !bones);
            TestAssert.Equal(0, editor.Features.Count);
            TestAssert.True(bones ? editor.Bones.Count > 0 : editor.Bones.Count == 0,
                $"{key} exposed unsupported bones or hid its valid rig.");
            TestAssert.True(!editor.AllowsMorphRandomisation && !editor.RandomiseMorphs,
                $"{key} exposed unsafe morph randomisation.");
            editor.RandomiseMorphs = true;
            TestAssert.True(!editor.RandomiseMorphs, $"{key} accepted unsafe morph randomisation.");
            TestAssert.True(editor.AllowsMaterialRandomisation, $"{key} lost material editing.");
            if (!bones)
                TestAssert.True(!editor.CanEditMorphFeatures && !editor.CanEditAttachments,
                    $"{key} exposed authored geometry or attachment edits.");
        }
    }

    private static void MalformedAndInertTargetsStayHidden()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles;
        var male = profiles.Single(value => value.Key == "le3-human-male").GetUiProfile();
        foreach (var name in new[] { "eyes_Large", "eyeShape_droop", "eyeShape_sleepy" })
            TestAssert.True(!ProfileFeature(male, name, metadataOnly: true).IsVisible,
                $"Inert metadata {name} became editable.");

        var female = profiles.Single(value => value.Key == "le3-human-female").GetUiProfile();
        var target = TestFixtures.CreateTarget();
        var malformedHair = new MorphTargetAsset(target.Source,
            [new MorphTargetLod(0, 3, []),
             new MorphTargetLod(1, 3, [new MorphVertexDelta(0, Vector3.UnitX * 0.02f, Vector3.Zero)])], []);
        foreach (var name in new[] { "HAIR_centerPart", "HAIR_pulledBackBig", "HAIR_pulledBackSlick",
                     "HAIR_sidePart", "HAIR_slickWidowsPeak", "HAIR_splitSide" })
            TestAssert.True(!ProfileFeature(female, name, target: malformedHair).IsVisible,
                $"Malformed LE3 hair target {name} became editable.");
    }

    private static MorphFeatureMetadata ProfileFeature(IHeadEditorUiProfile profile, string name,
        bool metadataOnly = false, MorphTargetAsset? target = null) =>
        profile.Describe(new ResolvedMorphFeature(new MorphFeatureValue(name, 0),
            metadataOnly ? null : target ?? TestFixtures.CreateTarget(),
            metadataOnly ? MorphFeatureResolutionKind.MetadataOnly : MorphFeatureResolutionKind.DirectTarget, null), true);
}
