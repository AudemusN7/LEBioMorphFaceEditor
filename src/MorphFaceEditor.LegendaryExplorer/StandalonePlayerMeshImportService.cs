using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record StandalonePlayerMeshRecognition(
    StandalonePlayerSex Sex,
    string TemplateFacePath,
    string CoordinateSystem,
    float BaseRootMeanSquareDistance,
    Vector3[] CanonicalOrderPositions,
    IReadOnlyList<BoneTranslation> FinalSkeleton);

/// <summary>Result of importing a recognised mesh into a detached player package.</summary>
public sealed class StandalonePlayerMeshImportResult : IDisposable
{
    internal StandalonePlayerMeshImportResult(
        MorphFacePackageWorkspace workspace,
        MorphFaceSaveResult saveResult,
        MorphFaceGame game,
        StandalonePlayerMeshRecognition recognition)
    {
        Workspace = workspace;
        SaveResult = saveResult;
        Game = game;
        Recognition = recognition;
    }

    public MorphFacePackageWorkspace Workspace { get; }
    public MorphFaceSaveResult SaveResult { get; }
    public string ImportedFacePath => SaveResult.FaceInstancedPath;
    public MorphFaceGame Game { get; }
    public StandalonePlayerMeshRecognition Recognition { get; }
    public bool CanCommit => Workspace.CanCommit;

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Recognises detached mesh topology against the selected game's installed HMM
/// and HMF player assets, then authors an immutable fixed-bake face only after a
/// unique correspondence proof.
/// </summary>
public sealed class StandalonePlayerMeshImportService
{
    private readonly MorphFaceInterchangeService _interchange;
    private readonly MorphFacePackageContextService _packageContext;

    public StandalonePlayerMeshImportService(
        MorphFaceInterchangeService? interchange = null,
        MorphFacePackageContextService? packageContext = null)
    {
        _interchange = interchange ?? new MorphFaceInterchangeService();
        _packageContext = packageContext ?? new MorphFacePackageContextService();
    }

    public StandalonePlayerMeshImportResult Import(
        MorphFaceGame game,
        string meshPath,
        string objectName)
    {
        var seedPath = StandalonePlayerMorphImportService.ResolveInstalledSeed(game);
        var recognition = Recognize(game, seedPath, meshPath);
        var workspace = new MorphFacePackageWorkspace(seedPath, canCommit: false);
        try
        {
            var saveResult = ImportRecognized(workspace, objectName, recognition);
            return new StandalonePlayerMeshImportResult(
                workspace,
                saveResult,
                game,
                recognition);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public (MorphFaceSaveResult SaveResult, StandalonePlayerMeshRecognition Recognition) ImportIntoWorkspace(
        MorphFaceGame game,
        string meshPath,
        string objectName,
        MorphFacePackageWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.CanCommit)
        {
            throw new InvalidOperationException(
                "Standalone player meshes can only be appended to a detached workspace.");
        }
        var expectedSeed = StandalonePlayerMorphImportService.ResolveInstalledSeed(game);
        if (!string.Equals(workspace.SourcePath, expectedSeed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The open standalone workspace uses different game assets. Start a {game} workspace before importing this mesh.");
        }

        var recognition = Recognize(game, expectedSeed, meshPath);
        return (ImportRecognized(workspace, objectName, recognition), recognition);
    }

    public StandalonePlayerMeshRecognition Recognize(MorphFaceGame game, string meshPath) =>
        Recognize(game, StandalonePlayerMorphImportService.ResolveInstalledSeed(game), meshPath);

    private StandalonePlayerMeshRecognition Recognize(
        MorphFaceGame game,
        string seedPath,
        string meshPath)
    {
        var imported = _interchange.ReadMesh(meshPath);
        using var reader = new MorphFacePackageReader();
        var matches = new List<StandalonePlayerMeshRecognition>();
        var candidateDiagnostics = new List<string>();
        foreach (var sex in Enum.GetValues<StandalonePlayerSex>())
        {
            var templatePath = StandalonePlayerMorphImportService.ResolvePlayerTemplate(game, sex);
            var loaded = reader.Load(seedPath, templatePath);
            var baseLod = loaded.BaseHead.FindLod(0);
            candidateDiagnostics.Add(
                $"{sex}: base {baseLod?.Positions.Length ?? 0} vertices/{baseLod?.RenderData.Indices.Count ?? 0} indices");
            var match = MorphMeshTopologyMatcher.Match(imported, loaded.BaseHead);
            var meshCandidates = _interchange.ReadMeshPositions(meshPath, loaded.BaseHead);
            var trustedPrior = meshCandidates
                .Select(candidate => candidate.Prior)
                .FirstOrDefault(prior => prior?.BaseHeadPath.Equals(
                                             loaded.BaseHead.Source.InstancedPath,
                                             StringComparison.OrdinalIgnoreCase) == true);
            var sidecarMapped = meshCandidates
                .FirstOrDefault(candidate => candidate.CoordinateSystem.EndsWith(
                                                   "MFE vertex map", StringComparison.Ordinal) &&
                                             candidate.Positions.Count == loaded.BaseHead.Positions.Length &&
                                             candidate.Prior?.BaseHeadPath.Equals(
                                                 loaded.BaseHead.Source.InstancedPath,
                                                 StringComparison.OrdinalIgnoreCase) == true);
            if (match is null && sidecarMapped is null)
            {
                continue;
            }

            var canonicalPositions = sidecarMapped?.Positions.ToArray() ??
                                     match!.CanonicalOrderPositions;
            var coordinateSystem = sidecarMapped?.CoordinateSystem ?? match!.CoordinateSystem;
            var baseRms = match?.BaseRootMeanSquareDistance ?? RootMeanSquareDistance(
                canonicalPositions,
                loaded.BaseHead.Positions);

            // A plain detached mesh has no BioMorphFace bone payload of its own.
            // Meshes exported by this app carry a trusted sidecar, however; keep
            // its final local pose so a baked RON is not displayed with the
            // canonical identity pose on the second import.
            var finalSkeleton = IsCompatibleSkeleton(trustedPrior, loaded.BaseHead)
                ? trustedPrior!.FinalSkeleton.ToArray()
                : loaded.BaseHead.Topology.ReferenceSkeleton
                    .Select(bone => new BoneTranslation(bone.Name, bone.Position))
                    .ToArray();
            matches.Add(new StandalonePlayerMeshRecognition(
                sex,
                templatePath,
                coordinateSystem,
                baseRms,
                canonicalPositions,
                finalSkeleton));
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException(
                $"The mesh does not have a trustworthy {game} HMM or HMF player topology. " +
                $"It decoded as {imported.Positions.Length} vertices/{imported.Indices.Length} indices " +
                $"with {(imported.HasCompleteTextureCoordinates ? "complete" : "incomplete")} UV0; " +
                $"{string.Join(", ", candidateDiagnostics)}. No player workspace was created; " +
                "unrecognised meshes will enter Custom Mode in the next checkpoint."),
            _ => throw new InvalidDataException(
                $"The mesh ambiguously matches more than one {game} player topology. No workspace was created.")
        };
    }

    private MorphFaceSaveResult ImportRecognized(
        MorphFacePackageWorkspace workspace,
        string objectName,
        StandalonePlayerMeshRecognition recognition)
    {
        var data = new MorphFaceMorphData(
            [],
            recognition.FinalSkeleton,
            [recognition.CanonicalOrderPositions]);
        // The verified template contributes materials and its canonical rig. An
        // MFE sidecar may retain the source pose; otherwise the rig stays at bind.
        // A mesh import authors only LOD0 and never inherits attachments.
        return _packageContext.CloneMorphWithData(
            workspace.WorkingPath,
            recognition.TemplateFacePath,
            objectName,
            data,
            requireMatchingAllLods: false,
            clearAttachmentReferences: true);
    }

    private static float RootMeanSquareDistance(
        IReadOnlyList<Vector3> left,
        IReadOnlyList<Vector3> right)
    {
        double squared = 0;
        for (var index = 0; index < left.Count; index++)
        {
            squared += Vector3.DistanceSquared(left[index], right[index]);
        }
        return (float)Math.Sqrt(squared / left.Count);
    }

    private static bool IsCompatibleSkeleton(
        MorphMeshFitPrior? prior,
        SkeletalMeshAsset baseHead)
    {
        var reference = baseHead.Topology.ReferenceSkeleton;
        var referenceNames = reference
            .Select(bone => bone.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return prior is { FinalSkeleton.Count: > 0 } &&
               prior.FinalSkeleton.Select(bone => bone.BoneName)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .Count() == prior.FinalSkeleton.Count &&
               prior.FinalSkeleton.All(bone =>
                   referenceNames.Contains(bone.BoneName) &&
                   float.IsFinite(bone.Translation.X) &&
                   float.IsFinite(bone.Translation.Y) &&
                   float.IsFinite(bone.Translation.Z));
    }
}
