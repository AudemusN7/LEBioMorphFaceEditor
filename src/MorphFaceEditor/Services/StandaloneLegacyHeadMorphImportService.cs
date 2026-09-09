using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

/// <summary>Owns a detached conversion of one legacy Gibbed player head morph.</summary>
public sealed class StandaloneLegacyHeadMorphImportResult : IDisposable
{
    internal StandaloneLegacyHeadMorphImportResult(
        MorphFacePackageWorkspace workspace,
        MorphFaceSaveResult saveResult,
        MorphFaceGame game,
        StandalonePlayerSex sex)
    {
        Workspace = workspace;
        SaveResult = saveResult;
        Game = game;
        Sex = sex;
    }

    public MorphFacePackageWorkspace Workspace { get; }
    public MorphFaceSaveResult SaveResult { get; }
    public string ImportedFacePath => SaveResult.FaceInstancedPath;
    public MorphFaceGame Game { get; }
    public StandalonePlayerSex Sex { get; }
    public bool CanCommit => Workspace.CanCommit;

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Converts a Gibbed ME2/ME3 head morph through the selected game's player
/// profile, then imports it into a non-committable copy of the installed
/// player package. Package conversion stays here rather than in the WPF layer;
/// the LegendaryExplorer project remains independent of app services.
/// </summary>
public sealed class StandaloneLegacyHeadMorphImportService
{
    private readonly MorphFaceProfileRegistry _profiles;
    private readonly MorphTargetCatalog _targets;
    private readonly MorphFacePackageContextService _packageContext;

    public StandaloneLegacyHeadMorphImportService(
        MorphFaceProfileRegistry? profiles = null,
        MorphTargetCatalog? targets = null,
        MorphFacePackageContextService? packageContext = null)
    {
        _profiles = profiles ?? MorphFaceProfileRegistry.CreateDefault();
        _targets = targets ?? new MorphTargetCatalog();
        _packageContext = packageContext ?? new MorphFacePackageContextService();
    }

    public StandaloneLegacyHeadMorphImportResult Import(
        MorphFaceGame targetGame,
        string sourcePath,
        string objectName)
    {
        ValidateSourceGame(targetGame, sourcePath);
        var legacy = _packageContext.ReadLegacyHeadMorph(sourcePath);
        var sex = StandalonePlayerMorphImportService.IdentifySex(
            targetGame,
            legacy.MorphData.BakedLods[0].Length);
        var seedPath = StandalonePlayerMorphImportService.ResolveInstalledSeed(targetGame);
        var workspace = new MorphFacePackageWorkspace(seedPath, canCommit: false);
        try
        {
            var saveResult = ImportIntoWorkspace(
                targetGame,
                sourcePath,
                objectName,
                workspace,
                sex,
                legacy);
            return new StandaloneLegacyHeadMorphImportResult(workspace, saveResult, targetGame, sex);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends to an existing detached workspace. Failure leaves the workspace
    /// alive so the caller can continue using the previous imported faces.
    /// </summary>
    public MorphFaceSaveResult ImportIntoWorkspace(
        MorphFaceGame targetGame,
        string sourcePath,
        string objectName,
        MorphFacePackageWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateSourceGame(targetGame, sourcePath);
        if (workspace.CanCommit)
        {
            throw new InvalidOperationException(
                "Legacy player head morphs can only be appended to a detached standalone workspace.");
        }

        var expectedSeed = StandalonePlayerMorphImportService.ResolveInstalledSeed(targetGame);
        if (!string.Equals(workspace.SourcePath, expectedSeed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The open standalone workspace uses different game assets. Start a {targetGame} workspace before importing this head morph.");
        }

        var legacy = _packageContext.ReadLegacyHeadMorph(sourcePath);
        var sex = StandalonePlayerMorphImportService.IdentifySex(
            targetGame,
            legacy.MorphData.BakedLods[0].Length);
        return ImportIntoWorkspace(targetGame, sourcePath, objectName, workspace, sex, legacy);
    }

    public static MorphFaceGame GetSourceGame(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".me2headmorph" => MorphFaceGame.LE2,
            ".me3headmorph" => MorphFaceGame.LE3,
            _ => throw new InvalidDataException(
                "Standalone legacy import accepts .me2headmorph and .me3headmorph files only.")
        };
    }

    private MorphFaceSaveResult ImportIntoWorkspace(
        MorphFaceGame targetGame,
        string sourcePath,
        string objectName,
        MorphFacePackageWorkspace workspace,
        StandalonePlayerSex sex,
        LegacyHeadMorphImport legacy)
    {
        var templatePath = StandalonePlayerMorphImportService.ResolvePlayerTemplate(targetGame, sex);
        using var reader = new MorphFacePackageReader();
        var loaded = reader.Load(workspace.WorkingPath, templatePath);
        if (loaded.BaseHead.Topology.VertexCount != legacy.MorphData.BakedLods[0].Length)
        {
            throw new InvalidDataException(
                $"The {targetGame} player template '{templatePath}' does not match the legacy LOD0 topology " +
                $"({loaded.BaseHead.Topology.VertexCount} template vertices versus {legacy.MorphData.BakedLods[0].Length} source vertices).");
        }
        var resolution = _profiles.Resolve(
            loaded.Game,
            loaded.Document.Source.InstancedPath,
            loaded.Document.BaseHeadReference?.InstancedPath,
            loaded.Document.MaterialOverrides,
            loaded.Materials) ?? throw new NotSupportedException(
                $"The installed {targetGame} player template '{templatePath}' has no supported morph profile.");
        if (resolution.UsesCustomMesh || resolution.Profile.IgnoresAuthoredGeometry)
        {
            throw new InvalidDataException(
                $"The installed {targetGame} player template '{templatePath}' is not an editable player profile.");
        }

        var targets = _targets.Load(resolution.Profile, targetGame, workspace.WorkingPath);
        var session = new MorphFaceEditingSession(
            loaded.Document,
            loaded.BaseHead,
            targets,
            resolution.Profile.MetadataOnlyFeatures,
            resolution.Profile.DisplayName,
            resolution.Profile.FeatureAliases,
            resolution.Profile.RecognizesBaseVariant,
            resolution.Profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));
        session.ApplyMorphData(legacy.MorphData);
        var draft = session.CreateDraft(
            loaded.Document.HairMeshReference,
            loaded.Document.OtherMeshReferences,
            loaded.Document.MaterialOverrides);
        var converted = new MorphFaceMorphData(
            draft.MorphFeatures,
            draft.FinalSkeleton,
            draft.BakedLods);
        return _packageContext.ImportConvertedLegacyHeadMorph(
            workspace.WorkingPath,
            templatePath,
            objectName,
            sourcePath,
            converted);
    }

    public static void ValidateSourceGame(MorphFaceGame targetGame, string sourcePath)
    {
        var sourceGame = GetSourceGame(sourcePath);
        if (sourceGame != targetGame)
        {
            throw new InvalidDataException(
                $"This is an {sourceGame} Gibbed head morph. Select {sourceGame} as the destination game; legacy head morphs cannot be converted to {targetGame}.");
        }
    }
}
