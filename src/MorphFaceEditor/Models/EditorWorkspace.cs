using System.IO;
using System.Security.Cryptography;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Models;

internal enum EditorWorkspaceKind { Pcc, Player, Npc, DetachedMesh }
internal enum WorkspaceFileFormat { MorphRon, MorphFeaturesRon, MfeMaterialsRon, TseMaterialsRon, Mesh, LegacyMorph }
internal sealed record ImportedFaceFile(string Path, string FacePath, WorkspaceFileFormat Format, string? Fingerprint);

/// <summary>Owns the active source, disposable PCC projection and staged edits; never owns sidebar selection.</summary>
internal sealed class EditorWorkspace : IDisposable
{
    private readonly HashSet<string> _stagedFaces = new(StringComparer.OrdinalIgnoreCase);
    private string? CareerFacePath { get; init; }

    private EditorWorkspace(EditorWorkspaceKind kind, MorphFaceGame game, MorphFacePackageWorkspace? package = null)
    {
        Kind = kind;
        Game = game;
        Package = package;
    }

    public static EditorWorkspace ForPcc(MorphFacePackageWorkspace package) => new(EditorWorkspaceKind.Pcc, MorphFaceGame.Unsupported, package);
    public static EditorWorkspace ForPlayer(MorphFacePackageWorkspace package, MorphFaceGame game, bool importedFacesOnly = false) =>
        new(EditorWorkspaceKind.Player, game, package) { ImportedFacesOnly = importedFacesOnly };
    public static EditorWorkspace ForNpc(MorphFacePackageWorkspace package, MorphFaceGame game, bool importedFacesOnly = false) =>
        new(EditorWorkspaceKind.Npc, game, package) { ImportedFacesOnly = importedFacesOnly };
    public static EditorWorkspace ForCareer(MorphFacePackageWorkspace package, PcsavHeadMorphWorkspace career, string facePath) =>
        new(EditorWorkspaceKind.Player, career.Game, package) { Career = career, CareerFacePath = facePath };
    public static EditorWorkspace ForMesh(ImportedMeshAsset mesh, MorphFaceGame game) =>
        new(EditorWorkspaceKind.DetachedMesh, game) { Mesh = mesh };

    public EditorWorkspaceKind Kind { get; }
    public MorphFaceGame Game { get; set; }
    public MorphFacePackageWorkspace? Package { get; }
    public PcsavHeadMorphWorkspace? Career { get; private init; }
    public ImportedMeshAsset? Mesh { get; private init; }
    public ImportedFaceFile? ImportedFile { get; set; }
    public bool ImportedFacesOnly { get; private init; }
    public Dictionary<string, MorphFaceGeometryMode> GeometryModes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // A native Save can be current while its disposable PCC still holds an older editing projection.
    public bool CareerDraftPendingInPackage { get; set; }
    public MorphFaceDocument? SavedCareerDraft { get; set; }

    public string PackagePath => Package?.SourcePath ?? Path.GetFullPath(Mesh!.SourcePath);
    public string? SourceFilePath => Career?.SourcePath ?? ImportedFile?.Path ?? (Mesh is null ? null : PackagePath);
    public string BrowsePath => SourceFilePath ?? PackagePath;
    public string? SourceFacePath => CareerFacePath ?? ImportedFile?.FacePath;
    public bool IsStandalone => Kind != EditorWorkspaceKind.Pcc;
    public bool CanCommit => Package?.CanCommit == true;
    public bool HasMultipleImports => IsStandalone && GeometryModes.Count > 1;
    public bool CanSaveRon => !HasMultipleImports && ImportedFile?.Format is
        WorkspaceFileFormat.MorphRon or WorkspaceFileFormat.MorphFeaturesRon or
        WorkspaceFileFormat.MfeMaterialsRon or WorkspaceFileFormat.TseMaterialsRon;
    public bool NeedsMeshSaveChoice => Mesh is not null || ImportedFile?.Format == WorkspaceFileFormat.Mesh;
    public string? SavePath => CanCommit ? Package!.SourcePath : Career?.SourcePath ?? (CanSaveRon ? ImportedFile?.Path : null);
    public bool HasStagedChanges => _stagedFaces.Count > 0;
    public bool HasPendingChanges => HasStagedChanges || Career?.HasPendingHeadImport == true;

    public void TrackImportedFile(string path, string facePath, WorkspaceFileFormat format, string? fingerprint = null)
    {
        var fullPath = Path.GetFullPath(path);
        ImportedFile = new(fullPath, facePath, format,
            format is WorkspaceFileFormat.Mesh or WorkspaceFileFormat.LegacyMorph ? null : fingerprint ?? FileFingerprint(fullPath));
    }

    public void MarkChanged(string facePath) => _stagedFaces.Add(facePath);
    public void AcceptSavedFace(string facePath) => _stagedFaces.Remove(facePath);
    public void AcceptSavedChanges() => _stagedFaces.Clear();
    public void Dispose() => Package?.Dispose();

    public static string FileFingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
