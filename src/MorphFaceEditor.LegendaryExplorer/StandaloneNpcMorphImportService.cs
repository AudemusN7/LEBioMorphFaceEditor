using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// The routing information supplied by the RON provenance header and the
/// native installed NPC face selected for the destination workspace.
/// </summary>
public sealed record StandaloneNpcMorphImportRequest(
    MorphFaceGame SourceGame,
    MorphFaceGame TargetGame,
    string Archetype,
    string RonPath,
    string DonorPackagePath,
    string DonorFacePath,
    string ObjectName,
    StandalonePlayerAssetCatalog? AssetCatalog = null,
    bool IsVerifiedNativeNpcDonor = false,
    IReadOnlyList<TextureCatalogCandidate>? SourceTextureCatalog = null,
    IReadOnlyList<TextureCatalogCandidate>? TargetTextureCatalog = null);

/// <summary>Owns a detached, non-committable NPC RON workspace.</summary>
public sealed class StandaloneNpcMorphImportResult : IDisposable
{
    internal StandaloneNpcMorphImportResult(
        MorphFacePackageWorkspace workspace,
        MorphFaceSaveResult saveResult,
        StandaloneNpcMorphImportRequest request)
    {
        Workspace = workspace;
        SaveResult = saveResult;
        SourceGame = request.SourceGame;
        TargetGame = request.TargetGame;
        Archetype = request.Archetype;
    }

    public MorphFacePackageWorkspace Workspace { get; }
    public MorphFaceSaveResult SaveResult { get; }
    public string ImportedFacePath => SaveResult.FaceInstancedPath;
    public string ObjectName => ImportedFacePath.Split('.').Last();
    public MorphFaceGame SourceGame { get; }
    public MorphFaceGame TargetGame { get; }
    public string Archetype { get; }
    public bool CanCommit => Workspace.CanCommit;

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Builds an NPC RON destination from a read-only native installed NPC donor.
/// Provenance chooses the route; the donor's LOD0 vertex count is retained as
/// the inexpensive candidate sanity guard. The authored RON arrays, values,
/// bones, references and material state remain the source of truth.
/// </summary>
public sealed class StandaloneNpcMorphImportService
{
    public static bool AreGamesCompatible(MorphFaceGame source, MorphFaceGame target) =>
        source == target ||
        (source is MorphFaceGame.LE1 or MorphFaceGame.LE2) &&
        (target is MorphFaceGame.LE1 or MorphFaceGame.LE2);

    private static readonly IReadOnlySet<string> SupportedArchetypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ALN", "ASA", "BAT", "HMM", "HMF", "KRO", "SAL", "TUR", "TUF"
        };

    public StandaloneNpcMorphImportResult ImportNpcRon(
        StandaloneNpcMorphImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var donorPackagePath = Path.GetFullPath(request.DonorPackagePath);
        ValidateDonor(donorPackagePath, request.TargetGame, request.DonorFacePath);
        var ron = ReadRon(request.RonPath);
        var donorMorph = new MorphFacePackageContextService().CaptureMorphData(
            donorPackagePath,
            request.DonorFacePath);
        var staticLodWarnings = ValidateLodVertexCounts(
            ron.MorphData, donorMorph, request.DonorFacePath);
        var sourceFingerprint = PackageFingerprint.Capture(donorPackagePath);

        // The workspace copies the donor before the context service mutates it.
        // The installed package remains the source fingerprint and can never be
        // committed through this result.
        var workspace = new MorphFacePackageWorkspace(donorPackagePath, canCommit: false);
        try
        {
            var saveResult = new MorphFacePackageContextService().ImportStandaloneNpcRon(
                workspace.WorkingPath,
                request.DonorFacePath,
                request.ObjectName,
                Path.GetFullPath(request.RonPath),
                request.SourceGame,
                request.TargetGame,
                $"{request.SourceGame.ToString().ToLowerInvariant()}-{request.Archetype.ToLowerInvariant()}",
                donorPackagePath,
                request.SourceTextureCatalog ?? [],
                request.TargetTextureCatalog ?? [],
                request.AssetCatalog);
            saveResult = saveResult with
            {
                Warnings = saveResult.Warnings.Concat(staticLodWarnings).ToArray()
            };
            if (PackageFingerprint.Capture(donorPackagePath) != sourceFingerprint)
            {
                workspace.Dispose();
                throw new IOException(
                    "The installed NPC donor changed while the standalone import was being prepared.");
            }
            return new StandaloneNpcMorphImportResult(workspace, saveResult, request);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static void ValidateRequest(StandaloneNpcMorphImportRequest request)
    {
        if (request.SourceGame is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3) ||
            request.TargetGame is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
        {
            throw new InvalidDataException("Standalone NPC RON import supports LE1, LE2 and LE3 only.");
        }

        if (!AreGamesCompatible(request.SourceGame, request.TargetGame))
        {
            throw new InvalidDataException(
                "LE3 NPC morphs must be imported into LE3; cross-game LE3 conversion is a separate workflow.");
        }

        if (string.IsNullOrWhiteSpace(request.Archetype) ||
            !SupportedArchetypes.Contains(request.Archetype.Trim()))
        {
            throw new InvalidDataException(
                $"'{request.Archetype}' is not a supported standalone NPC archetype.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.RonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DonorPackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DonorFacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ObjectName);
        if (!request.IsVerifiedNativeNpcDonor)
        {
            throw new InvalidDataException(
                "The NPC donor must be a verified installed native NPC template; player, mod and unreviewed legacy donors are not accepted.");
        }
        if (request.AssetCatalog is not null && request.AssetCatalog.Game != request.TargetGame)
        {
            throw new InvalidDataException(
                $"The installed-asset catalogue is for {request.AssetCatalog.Game}, not the selected {request.TargetGame} game.");
        }
        if (request.SourceGame != request.TargetGame &&
            (request.SourceTextureCatalog is null || request.TargetTextureCatalog is null))
        {
            throw new InvalidDataException(
                "LE1/LE2 NPC material transfer requires both installed texture databases.");
        }
    }

    private static void ValidateDonor(
        string donorPackagePath,
        MorphFaceGame targetGame,
        string donorFacePath)
    {
        if (!File.Exists(donorPackagePath))
        {
            throw new FileNotFoundException("The installed NPC donor package was not found.", donorPackagePath);
        }

        LegendaryExplorerCoreRuntime.Initialize();
        using var donor = MEPackageHandler.OpenMEPackage(donorPackagePath, forceLoadFromDisk: true);
        var expectedGame = targetGame switch
        {
            MorphFaceGame.LE1 => MEGame.LE1,
            MorphFaceGame.LE2 => MEGame.LE2,
            MorphFaceGame.LE3 => MEGame.LE3,
            _ => throw new InvalidDataException($"Unsupported NPC destination game '{targetGame}'.")
        };
        if (donor.Game != expectedGame)
        {
            throw new InvalidDataException(
                $"The NPC donor is {donor.Game}, but the selected destination game is {targetGame}.");
        }

        if (donor.FindExport(donorFacePath, "BioMorphFace") is null)
        {
            throw new InvalidDataException(
                $"Native NPC donor face '{donorFacePath}' was not found in '{donorPackagePath}'.");
        }
    }

    private static TseHeadMorph ReadRon(string ronPath)
    {
        var path = Path.GetFullPath(ronPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The NPC RON file was not found.", path);
        }
        if (!Path.GetExtension(path).Equals(".ron", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Standalone NPC import accepts .ron files only.");
        }
        return TseHeadMorphRon.Read(path);
    }

    private static IReadOnlyList<string> ValidateLodVertexCounts(
        MorphFaceMorphData authored,
        MorphFaceMorphData donor,
        string donorFacePath)
    {
        if (authored.BakedLods.Count == 0 || donor.BakedLods.Count == 0)
        {
            throw new InvalidDataException(
                "The NPC RON and native donor must both have an LOD0 bake.");
        }
        if (authored.BakedLods[0].Length != donor.BakedLods[0].Length)
        {
            throw new InvalidDataException(
                $"NPC RON LOD 0 has {authored.BakedLods[0].Length} vertices, but donor '{donorFacePath}' has {donor.BakedLods[0].Length}.");
        }
        var warnings = new List<string>();
        for (var lod = 0; lod < authored.BakedLods.Count; lod++)
        {
            if (lod >= donor.BakedLods.Count ||
                authored.BakedLods[lod].Length != donor.BakedLods[lod].Length)
            {
                warnings.Add(
                    $"NPC RON LOD {lod} has no matching native destination bake and will be preserved unchanged; morph editing is unavailable at that LOD.");
            }
        }
        return warnings;
    }
}
