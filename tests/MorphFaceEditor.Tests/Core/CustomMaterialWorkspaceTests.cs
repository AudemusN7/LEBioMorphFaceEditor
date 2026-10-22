using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

public static class CustomMaterialWorkspaceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom workspace supports eight sparse used slots and reports overflow", EnforcesMaximumSlots),
        new("custom workspace enforces role-specific material compatibility", EnforcesRoleSpecificCompatibility),
        new("custom material rebase uses the lowest slot and preserves shared overrides", RebasePreservesUnionAndOverrides),
        new("custom material parameters remain independent across racial scopes", RacialScopesRemainIndependent),
        new("custom material rebase retains hidden edits and preview attachments", RebaseRetainsAttachments),
    ];

    private static void EnforcesMaximumSlots()
    {
        var sections = Enumerable.Range(0, 8)
            .Select(index => new ImportedMeshSection(index * 2, $"Slot {index}", index * 3, 3)).ToArray();
        var source = CreateSource([.. sections, new(0, "Shared", 24, 3), new(99, "Unused", 27, 0)]);
        var accepted = CustomMaterialWorkspace.Create(source);
        TestAssert.Equal(8, accepted.UsedSlotCount);
        var workspace = accepted.Workspace ?? throw new InvalidOperationException("Eight used slots were rejected.");
        foreach (var slot in workspace.UsedSlots)
            workspace.Assign(slot, Option($"slot-{slot.MaterialIndex}", "Skin", "human", "head",
                HeadMaterialFamily.Skin));
        TestAssert.Equal(8, workspace.Assignments.Count);
        TestAssert.True(ReferenceEquals(source, workspace.Source), "Creation replaced the caller's source.");

        var overflowSource = CreateSource([.. sections, new(16, "Ninth", 24, 3)]);
        var rejected = CustomMaterialWorkspace.Create(overflowSource);
        TestAssert.Equal(9, rejected.UsedSlotCount);
        TestAssert.True(rejected.Workspace is null, "Nine used slots were accepted.");
        TestAssert.Equal(9, overflowSource.Sections.Count);
    }

    private static void EnforcesRoleSpecificCompatibility()
    {
        var workspace = CustomMaterialWorkspace.Create(CreateSource(
            new ImportedMeshSection(0, "First", 0, 3),
            new ImportedMeshSection(1, "Second", 3, 3))).Workspace!;

        AssertPairRejected(workspace,
            Option("hmm-eyes", "HMM Eyes", "human-male", "eyes", HeadMaterialFamily.Eyes),
            Option("hmf-eyes", "HMF Eyes", "human-female", "eyes", HeadMaterialFamily.Eyes),
            "HMM and HMF eye materials were accepted together.");
        AssertPairRejected(workspace,
            Option("hmm-lashes", "HMM Lashes", "human-male", "lashes", HeadMaterialFamily.Lashes),
            Option("hmf-lashes", "HMF Lashes", "human-female", "lashes", HeadMaterialFamily.Lashes),
            "HMM and HMF lash materials were accepted together.");

        workspace.Assign(0, Option("hmm-eyes", "HMM Eyes", "human-male", "eyes", HeadMaterialFamily.Eyes));
        workspace.Assign(1, Option("turian-eyes", "Turian Eyes", "turian", "eyes", HeadMaterialFamily.TurianEyes));
        TestAssert.Equal(2, workspace.Assignments.Count);
        workspace.Unassign(0);
        workspace.Unassign(1);
        workspace.Assign(0, Option("salarian-eyes", "Salarian Eyes", "salarian", "eyes",
            HeadMaterialFamily.SalarianEyes));
        workspace.Assign(1, Option("krogan-eyes", "Krogan Eyes", "krogan", "eyes",
            HeadMaterialFamily.KroganEyes));
        TestAssert.Equal(2, workspace.Assignments.Count);
        workspace.Unassign(0);
        workspace.Unassign(1);

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

    private static void RebasePreservesUnionAndOverrides()
    {
        var sourceOverride = new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("LegacyUnsupported", 0.75f)], [], []);
        var workspace = CustomMaterialWorkspace.Create(CreateSource(
            new ImportedMeshSection(5, "Skin", 0, 3),
            new ImportedMeshSection(1, "Eyes", 3, 3))).Workspace!;
        workspace.Assign(5, Option("skin", "Skin", "head", "head", HeadMaterialFamily.Skin, scalar: 0.2f, unique: true));
        workspace.Assign(1, Option("eyes", "Eyes", "head", "eye", HeadMaterialFamily.Eyes, scalar: 0.4f));
        var session = new MaterialEditingSession(sourceOverride, ResolvedHeadMaterialSet.Empty);
        var surfaceChanges = 0;
        session.MaterialsChanged += (_, args) => { if (args.Kind == MaterialChangeKind.Surface) surfaceChanges++; };

        session.RebaseMaterialSurface(workspace.ActiveMaterials);
        var shared = MaterialParameterControlKey.Create("head", "Shared");
        TestAssert.Near(0.4f, session.GetScalar(shared), 0);
        TestAssert.True(session.ScalarNames.Contains(shared), "Shared parameter was not exposed.");
        TestAssert.True(session.ScalarNames.Contains(MaterialParameterControlKey.Create("head", "SkinOnly")),
            "Union parameter was not exposed.");
        TestAssert.Near(0.75f,
            session.CreateOverrides().Scalars.Single(value => value.Name == "LegacyUnsupported").Value, 0);
        session.SetScalar(shared, 0.9f);
        TestAssert.True(session.Materials.Materials.Values.All(material =>
            material.Scalars.TryGetValue("Shared", out var value) && Math.Abs(value - 0.9f) < 1e-6f),
            "One shared edit did not project to every applicable material.");
        TestAssert.Equal(1, surfaceChanges);
        TestAssert.Equal(0.75f, session.CreateOverrides().Scalars.Single(value => value.Name == "LegacyUnsupported").Value);
    }

    private static void RacialScopesRemainIndependent()
    {
        var workspace = CustomMaterialWorkspace.Create(CreateSource(
            new ImportedMeshSection(0, "Human", 0, 3),
            new ImportedMeshSection(1, "Krogan", 3, 3))).Workspace!;
        workspace.Assign(0, Option("human", "Human", "human-male", "head", HeadMaterialFamily.Skin, 0.2f));
        workspace.Assign(1, Option("krogan", "Krogan", "krogan", "head", HeadMaterialFamily.KroganSkin, 0.7f));
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, workspace.ActiveMaterials);
        var human = MaterialParameterControlKey.Create("human", "Shared");
        var krogan = MaterialParameterControlKey.Create("krogan", "Shared");

        TestAssert.True(session.ScalarNames.Contains(human) && session.ScalarNames.Contains(krogan),
            "Two racial materials collapsed their same-named parameter into one control.");
        try
        {
            session.ApplyMaterialData(
                new MorphFaceMaterialData([new ScalarMaterialOverride("Shared", 0.5f)], [], []),
                new Dictionary<string, DecodedTextureAsset?>());
            throw new InvalidOperationException("An ambiguous unscoped material payload was silently applied.");
        }
        catch (InvalidDataException exception)
        {
            TestAssert.True(exception.Message.Contains("more than one MESH racial scope",
                    StringComparison.OrdinalIgnoreCase),
                "The ambiguous material payload failed for the wrong reason.");
        }
        session.SetScalar(krogan, 0.9f);

        var materials = session.Materials.Materials.Values.ToArray();
        TestAssert.Near(0.2f, materials.Single(value => value.Family == HeadMaterialFamily.Skin).Scalars["Shared"], 0);
        TestAssert.Near(0.9f, materials.Single(value => value.Family == HeadMaterialFamily.KroganSkin).Scalars["Shared"], 0);
    }

    private static void RebaseRetainsAttachments()
    {
        var workspace = CustomMaterialWorkspace.Create(CreateSource(
            new ImportedMeshSection(0, "Skin", 0, 3))).Workspace!;
        workspace.Assign(0, Option("skin", "Skin", "human", "head", HeadMaterialFamily.Skin));
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
        session.RebaseMaterialSurface(workspace.ActiveMaterials);
        var shared = MaterialParameterControlKey.Create("human", "Shared");
        session.SetScalar(shared, 0.9f);
        var hair = Option("hair", "Hair", "human", "hair", HeadMaterialFamily.Hair).Template with
            { Scalars = new Dictionary<string, float>() };
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [hair.Key] = hair })));

        workspace.Unassign(0);
        session.RebaseMaterialSurface(workspace.ActiveMaterials);
        TestAssert.True(session.Materials.Materials.ContainsKey(hair.Key),
            "Unassigning a custom slot discarded preview attachment materials.");
        TestAssert.Near(0.9f, session.CreateOverrides().Scalars.Single(value => value.Name == shared).Value, 0);
        workspace.Assign(0, Option("skin", "Skin", "human", "head", HeadMaterialFamily.Skin));
        session.RebaseMaterialSurface(workspace.ActiveMaterials);
        TestAssert.True(session.Materials.Materials.ContainsKey(hair.Key) &&
                        session.Materials.Materials.Count == 2,
            "Reassigning a custom slot did not retain the preview attachment material.");
        TestAssert.Near(0.9f, session.Materials.Materials.Values.Single(value =>
            value.Family == HeadMaterialFamily.Skin).Scalars["Shared"], 0);
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
        return new CustomMaterialAssignmentOption(id, label, compatibility, role, family, template)
        {
            ParameterScopeKey = compatibility.StartsWith("human", StringComparison.OrdinalIgnoreCase)
                ? "human"
                : compatibility,
            ParameterScopeLabel = compatibility.StartsWith("human", StringComparison.OrdinalIgnoreCase)
                ? "Human"
                : label
        };
    }
}
