using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

public enum StandalonePlayerSex
{
    Male,
    Female
}

/// <summary>Owns a detached player-head RON import and its non-committable PCC workspace.</summary>
public sealed class StandalonePlayerMorphImportResult : IDisposable
{
    internal StandalonePlayerMorphImportResult(
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
    public string ObjectName => ImportedFacePath.Split('.').Last();
    public MorphFaceGame Game { get; }
    public StandalonePlayerSex Sex { get; }
    public bool CanCommit => Workspace.CanCommit;

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Imports a TSE player RON against the selected game's verified player face
/// template. The installed package is copied first and can never be committed
/// through the returned detached workspace.
/// </summary>
public sealed class StandalonePlayerMorphImportService
{
    private static readonly IReadOnlyDictionary<MorphFaceGame, (int Male, int Female, string MaleTemplate, string FemaleTemplate, string Seed)> Profiles =
        new Dictionary<MorphFaceGame, (int, int, string, string, string)>
        {
            [MorphFaceGame.LE1] = (2294, 2232,
                "BIOG_MORPH_FACE.Player_Base_Male",
                "BIOG_MORPH_FACE.Player_Base_Female",
                "EntryMenu.pcc"),
            [MorphFaceGame.LE2] = (2294, 2232,
                "BIOG_MORPH_FACE.CharacterCreation_Base_Male",
                "BIOG_MORPH_FACE.CharacterCreation_Base_Female",
                "BioP_Char.pcc"),
            [MorphFaceGame.LE3] = (2392, 2390,
                "biog_morph_face.CharacterCreation_Base_Male",
                "biog_morph_face.CharacterCreation_Base_Female",
                "BioP_Char.pcc")
        };

    public StandalonePlayerMorphImportResult ImportPlayerRon(
        MorphFaceGame targetGame,
        string ronPath) => ImportPlayerRon(targetGame, ronPath, CreateObjectName(ronPath));

    public StandalonePlayerMorphImportResult ImportPlayerRon(
        MorphFaceGame targetGame,
        string ronPath,
        string objectName)
    {
        var (sourcePath, _, profile, sex) = ReadAndClassify(targetGame, ronPath);
        var seedPath = ResolveInstalledSeed(targetGame, profile.Seed);
        var workspace = new MorphFacePackageWorkspace(seedPath, canCommit: false);
        try
        {
            var templatePath = sex == StandalonePlayerSex.Male
                ? profile.MaleTemplate
                : profile.FemaleTemplate;
            var saveResult = new MorphFacePackageContextService().ImportStandalonePlayerRon(
                workspace.WorkingPath,
                templatePath,
                objectName,
                sourcePath);
            return new StandalonePlayerMorphImportResult(workspace, saveResult, targetGame, sex);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public MorphFaceSaveResult ImportPlayerRonIntoWorkspace(
        MorphFaceGame targetGame,
        string ronPath,
        string objectName,
        MorphFacePackageWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.CanCommit)
        {
            throw new InvalidOperationException("Player RONs can only be appended to a detached standalone workspace.");
        }
        var (sourcePath, _, profile, sex) = ReadAndClassify(targetGame, ronPath);
        var expectedSeed = ResolveInstalledSeed(targetGame, profile.Seed);
        if (!string.Equals(workspace.SourcePath, expectedSeed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The open standalone workspace uses different game assets. Start a {targetGame} workspace before importing this RON.");
        }
        var templatePath = sex == StandalonePlayerSex.Male
            ? profile.MaleTemplate
            : profile.FemaleTemplate;
        return new MorphFacePackageContextService().ImportStandalonePlayerRon(
            workspace.WorkingPath,
            templatePath,
            objectName,
            sourcePath);
    }

    public StandalonePlayerMorphImportResult ImportPlayerRon(
        string ronPath,
        MorphFaceGame targetGame) => ImportPlayerRon(targetGame, ronPath);

    private static (string SourcePath, TseHeadMorph Ron,
        (int Male, int Female, string MaleTemplate, string FemaleTemplate, string Seed) Profile,
        StandalonePlayerSex Sex) ReadAndClassify(MorphFaceGame targetGame, string ronPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ronPath);
        if (!Profiles.TryGetValue(targetGame, out var profile))
        {
            throw new InvalidDataException($"Standalone player RON import does not support '{targetGame}'.");
        }
        var sourcePath = Path.GetFullPath(ronPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The player RON file was not found.", sourcePath);
        }
        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".ron", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Standalone player import accepts .ron only; '{extension}' is not a TSE RON file. " +
                "Gibbed head morphs and mesh files require their dedicated conversion workflows.");
        }
        var ron = TseHeadMorphRon.Read(sourcePath);
        return (sourcePath, ron, profile, IdentifySex(targetGame, ron.MorphData.BakedLods[0].Length));
    }

    internal static StandalonePlayerSex IdentifySex(MorphFaceGame targetGame, int lod0VertexCount)
    {
        if (!Profiles.TryGetValue(targetGame, out var profile))
        {
            throw new InvalidDataException($"Standalone player RON import does not support '{targetGame}'.");
        }
        if (lod0VertexCount == profile.Male)
        {
            return StandalonePlayerSex.Male;
        }
        if (lod0VertexCount == profile.Female)
        {
            return StandalonePlayerSex.Female;
        }
        throw new InvalidDataException(
            $"The selected {targetGame} player RON has {lod0VertexCount} LOD0 vertices; expected " +
            $"{profile.Male} (male HMM) or {profile.Female} (female HMF).");
    }

    private static string CreateObjectName(string sourcePath)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var sanitized = new string(stem.Select(character =>
            char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "PlayerMorph";
        }
        return $"MFE_{sanitized}_{Guid.NewGuid():N}";
    }

    private static string ResolveInstalledSeed(MorphFaceGame game, string fileName)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game)
                         ?? throw new DirectoryNotFoundException(
                             $"The installed {game} CookedPCConsole directory was not discovered.");
        var path = Path.Combine(cookedPath, fileName);
        return File.Exists(path)
            ? Path.GetFullPath(path)
            : throw new FileNotFoundException(
                $"The installed {game} player template package '{fileName}' was not found.", path);
    }
}
