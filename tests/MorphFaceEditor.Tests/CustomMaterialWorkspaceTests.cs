using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

public static class CustomMaterialWorkspaceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom workspace enumerates used slots and shared sections", EnumeratesUsedSlots),
        new("custom workspace enforces the four-slot boundary", EnforcesMaximumSlots),
        new("custom workspace rejects unknown assignment options", RejectsUnknown),
        new("custom workspace enforces role-specific material compatibility", EnforcesRoleSpecificCompatibility),
        new("custom material assignment undo is one semantic action", AssignmentUndoRedo),
        new("custom material defaults use the lowest assigned slot", LowestSlotWinsDefaults),
        new("custom material rebase exposes shared union and preserves overrides", RebasePreservesUnionAndOverrides),
        new("custom material rebase preserves edited values while hidden", RebasePreservesHiddenEdit),
        new("custom material rebase retains preview attachment materials", RebaseRetainsAttachments)
    ];

    private static void EnumeratesUsedSlots()
    {
        var source = CreateSource(
            new ImportedMeshSection(3, "Eyes A", 0, 3),
            new ImportedMeshSection(0, "Unused", 3, 0),
            new ImportedMeshSection(1, "Skin", 3, 3),
            new ImportedMeshSection(3, "Eyes B", 6, 3));
        var workspace = new CustomMaterialWorkspace(source);

        TestAssert.Equal(2, workspace.UsedSlots.Count);
        TestAssert.Equal(3, workspace.UsedSlots[0].MaterialIndex);
        TestAssert.Equal(1, workspace.UsedSlots[1].MaterialIndex);
        TestAssert.Equal(2, workspace.UsedSlots[0].Sections.Count);
        TestAssert.Equal("Eyes A", workspace.UsedSlots[0].Sections[0].MaterialName);
        TestAssert.True(ReferenceEquals(source, workspace.Source), "The detached source was copied or replaced.");
        TestAssert.Equal(
            workspace.GetPreviewMaterialIdentity(3),
            workspace.UsedSlots[0].CreatePreviewIdentity(source.SourcePath));
    }

    private static void EnforcesMaximumSlots()
    {
        var sections = Enumerable.Range(0, CustomMaterialWorkspace.MaximumSupportedSlots + 1)
            .Select(index => new ImportedMeshSection(index, $"Slot {index}", index * 3, 3)).ToArray();
        try
        {
            _ = new CustomMaterialWorkspace(CreateSource(sections));
            throw new InvalidOperationException("A five-slot custom workspace was accepted.");
        }
        catch (InvalidDataException)
        {
            // The imported source remains owned by the caller and is unchanged.
        }
    }

    private static void RejectsUnknown()
    {
        var workspace = new CustomMaterialWorkspace(CreateSource(
            new ImportedMeshSection(0, "Face", 0, 3)));
        try
        {
            workspace.Assign(0, Option("unknown", "Unknown", "appearance", "head", HeadMaterialFamily.Unknown));
            throw new InvalidOperationException("Unknown material option was accepted.");
        }
        catch (ArgumentException)
        {
            TestAssert.Equal(0, workspace.Assignments.Count);
        }
    }

    private static void EnforcesRoleSpecificCompatibility()
    {
        var workspace = new CustomMaterialWorkspace(CreateSource(
            new ImportedMeshSection(0, "First", 0, 3),
            new ImportedMeshSection(1, "Second", 3, 3)));

        AssertPairRejected(workspace,
            Option("hmm-eyes", "HMM Eyes", "human-male", "eyes", HeadMaterialFamily.Eyes),
            Option("hmf-eyes", "HMF Eyes", "human-female", "eyes", HeadMaterialFamily.Eyes),
            "HMM and HMF eye materials were accepted together.");
        AssertPairRejected(workspace,
            Option("hmm-lashes", "HMM Lashes", "human-male", "lashes", HeadMaterialFamily.Lashes),
            Option("hmf-lashes", "HMF Lashes", "human-female", "lashes", HeadMaterialFamily.Lashes),
            "HMM and HMF lash materials were accepted together.");

        workspace.Assign(0, Option("hmm-eyes", "HMM Eyes", "human-male", "eyes", HeadMaterialFamily.Eyes));
        workspace.Assign(1, Option("hmf-lashes", "HMF Lashes", "human-female", "lashes", HeadMaterialFamily.Lashes));
        TestAssert.Equal(2, workspace.Assignments.Count);
        workspace.Unassign(0);
        workspace.Unassign(1);
        workspace.Assign(0, Option("hmf-eyes", "HMF Eyes", "human-female", "eyes", HeadMaterialFamily.Eyes));
        workspace.Assign(1, Option("hmm-lashes", "HMM Lashes", "human-male", "lashes", HeadMaterialFamily.Lashes));
        TestAssert.Equal(2, workspace.Assignments.Count);
        workspace.Unassign(0);
        workspace.Unassign(1);

        var hair = Option("hair", "Human Hair", "human", "hair", HeadMaterialFamily.Hair);
        workspace.Assign(0, hair);
        workspace.Assign(1, hair);
        TestAssert.Equal(2, workspace.Assignments.Count);
        workspace.Unassign(1);
        try
        {
            workspace.Assign(1, Option("iconic-hair", "Human Iconic FemShep - Hair", "human-female", "hair",
                HeadMaterialFamily.Hair));
            throw new InvalidOperationException("Standard and iconic hair materials were accepted together.");
        }
        catch (ArgumentException)
        {
            TestAssert.Equal(1, workspace.Assignments.Count);
        }
    }

    private static void AssertPairRejected(
        CustomMaterialWorkspace workspace,
        CustomMaterialAssignmentOption first,
        CustomMaterialAssignmentOption second,
        string failureMessage)
    {
        workspace.Assign(0, first);
        try
        {
            workspace.Assign(1, second);
            throw new InvalidOperationException(failureMessage);
        }
        catch (ArgumentException)
        {
            TestAssert.Equal(1, workspace.Assignments.Count);
        }
        workspace.Unassign(0);
    }

    private static void AssignmentUndoRedo()
    {
        var workspace = new CustomMaterialWorkspace(CreateSource(
            new ImportedMeshSection(0, "Face", 0, 3)));
        var commits = 0;
        workspace.EditCommitted += (_, _) => commits++;
        workspace.Assign(0, Option("skin", "Skin", "human", "head", HeadMaterialFamily.Skin));
        TestAssert.Equal(1, commits);
        TestAssert.True(workspace.CanUndo, "Assignment did not create history.");
        TestAssert.Equal(1, workspace.ActiveMaterials.Materials.Count);
        workspace.Undo();
        TestAssert.Equal(0, workspace.Assignments.Count);
        TestAssert.Equal(0, workspace.ActiveMaterials.Materials.Count);
        workspace.Redo();
        TestAssert.Equal(1, workspace.Assignments.Count);
        TestAssert.Equal(1, workspace.ActiveMaterials.Materials.Count);
    }

    private static void LowestSlotWinsDefaults()
    {
        var workspace = new CustomMaterialWorkspace(CreateSource(
            new ImportedMeshSection(5, "High", 0, 3),
            new ImportedMeshSection(1, "Low", 3, 3)));
        workspace.Assign(5, Option("high", "High", "head", "head", HeadMaterialFamily.Skin, scalar: 5));
        workspace.Assign(1, Option("low", "Low", "head", "head", HeadMaterialFamily.Skin, scalar: 1));

        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
        session.RebaseMaterialSurface(workspace);
        TestAssert.Equal(1f, session.GetScalar("Shared"));
        TestAssert.Equal(1f, session.Materials.Materials.Values.First().Scalars["Shared"]);
        TestAssert.Equal(1, workspace.ActiveMaterials.Materials.Values.First().Source.UIndex * -1 - 1);
    }

    private static void RebasePreservesUnionAndOverrides()
    {
        var sourceOverride = new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("LegacyUnsupported", 0.75f)], [], []);
        var workspace = new CustomMaterialWorkspace(CreateSource(
            new ImportedMeshSection(0, "Skin", 0, 3),
            new ImportedMeshSection(1, "Eyes", 3, 3)));
        workspace.Assign(0, Option("skin", "Skin", "head", "head", HeadMaterialFamily.Skin, scalar: 0.2f, unique: true));
        workspace.Assign(1, Option("eyes", "Eyes", "head", "eye", HeadMaterialFamily.Eyes, scalar: 0.4f));
        var session = new MaterialEditingSession(sourceOverride, ResolvedHeadMaterialSet.Empty);
        var surfaceChanges = 0;
        session.MaterialsChanged += (_, args) => { if (args.Kind == MaterialChangeKind.Surface) surfaceChanges++; };

        session.RebaseMaterialSurface(workspace.ActiveMaterials);
        TestAssert.True(session.ScalarNames.Contains("Shared"), "Shared parameter was not exposed.");
        TestAssert.True(session.ScalarNames.Contains("SkinOnly"), "Union parameter was not exposed.");
        TestAssert.Near(0.75f,
            session.CreateOverrides().Scalars.Single(value => value.Name == "LegacyUnsupported").Value, 0);
        session.SetScalar("Shared", 0.9f);
        TestAssert.True(session.Materials.Materials.Values.All(material =>
            material.Scalars.TryGetValue("Shared", out var value) && Math.Abs(value - 0.9f) < 1e-6f),
            "One shared edit did not project to every applicable material.");
        TestAssert.Equal(1, surfaceChanges);
        TestAssert.Equal(0.75f, session.CreateOverrides().Scalars.Single(value => value.Name == "LegacyUnsupported").Value);
    }

    private static void RebasePreservesHiddenEdit()
    {
        var workspace = new CustomMaterialWorkspace(CreateSource(
            new ImportedMeshSection(0, "Skin", 0, 3)));
        var option = Option("skin", "Skin", "head", "head", HeadMaterialFamily.Skin, scalar: 0.2f);
        workspace.Assign(0, option);
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
        session.RebaseMaterialSurface(workspace);
        session.SetScalar("Shared", 0.9f);
        workspace.Unassign(0);
        session.RebaseMaterialSurface(workspace);
        TestAssert.Near(0.9f, session.GetScalar("Shared"), 0);
        TestAssert.Near(0.9f, session.CreateOverrides().Scalars.Single(value => value.Name == "Shared").Value, 0);
        workspace.Assign(0, option);
        session.RebaseMaterialSurface(workspace);
        TestAssert.Near(0.9f, session.Materials.Materials.Values.Single().Scalars["Shared"], 0);
    }

    private static void RebaseRetainsAttachments()
    {
        var workspace = new CustomMaterialWorkspace(CreateSource(
            new ImportedMeshSection(0, "Skin", 0, 3)));
        workspace.Assign(0, Option("skin", "Skin", "human", "head", HeadMaterialFamily.Skin));
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
        session.RebaseMaterialSurface(workspace);
        var hair = Option("hair", "Hair", "human", "hair", HeadMaterialFamily.Hair).Template;
        session.ReplaceAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [hair.Key] = hair }));

        workspace.Unassign(0);
        session.RebaseMaterialSurface(workspace);
        TestAssert.True(session.Materials.Materials.ContainsKey(hair.Key),
            "Unassigning a custom slot discarded preview attachment materials.");
        workspace.Assign(0, Option("skin", "Skin", "human", "head", HeadMaterialFamily.Skin));
        session.RebaseMaterialSurface(workspace);
        TestAssert.True(session.Materials.Materials.ContainsKey(hair.Key) &&
                        session.Materials.Materials.Count == 2,
            "Reassigning a custom slot did not retain the preview attachment material.");
    }

    private static ImportedMeshAsset CreateSource(params ImportedMeshSection[] sections) => new(
        "custom-fixture.psk",
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        null,
        null,
        null,
        [0, 1, 2],
        sections,
        [],
        null,
        null,
        [0, 1, 2]);

    private static CustomMaterialAssignmentOption Option(
        string id,
        string label,
        string compatibility,
        string role,
        HeadMaterialFamily family,
        float scalar = 0,
        bool unique = false)
    {
        var identity = new AssetIdentity("template.pcc", $"Template.{id}", 1, "MaterialInstanceConstant");
        var template = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "MASTER", family,
            HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["Shared"] = scalar,
                ["SkinOnly"] = unique ? scalar : 0
            },
            new Dictionary<string, Vector4>(),
            new Dictionary<string, MaterialTextureBinding>());
        return new CustomMaterialAssignmentOption(id, label, compatibility, role, family, template);
    }
}
