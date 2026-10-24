using System.IO;

namespace MorphFaceEditor.Infrastructure;

internal enum EditorFileDropKind
{
    Unsupported,
    Package,
    CareerSave,
    MorphImport
}

internal static class EditorFileDrop
{
    internal static string WorkspaceOpenFilter =>
        $"{Loc.Text("loc_EditorFileDrop_SupportedPackageMorphFiles")}|*.pcc;*.pcsav;*.ron;*.psk;*.pskx;*.gltf;*.glb;*.me2headmorph;*.me3headmorph|" +
        $"{PackageFilter}|" +
        $"{Loc.Text("loc_Pcsav_FileFilter")} (*.pcsav)|*.pcsav|" +
        $"{Loc.Text("loc_EditorFileDrop_SupportedMorphFiles")}|*.ron;*.psk;*.pskx;*.gltf;*.glb;*.me2headmorph;*.me3headmorph|" +
        $"{AllFilesFilter}";

    internal static string MorphImportFilter =>
        $"{Loc.Text("loc_EditorFileDrop_SupportedMorphFiles")}|*.ron;*.psk;*.pskx;*.gltf;*.glb;*.me2headmorph;*.me3headmorph|" +
        $"{RonMorphFilter}|" +
        $"{Loc.Text("loc_EditorFileDrop_MeshFilesLabel")} (*.psk;*.pskx;*.gltf;*.glb)|*.psk;*.pskx;*.gltf;*.glb|" +
        $"{AllFilesFilter}";

    internal static string PackageFilter =>
        $"{Loc.Text("loc_EditorFileDrop_MassEffectPackagesLabel")} (*.pcc)|*.pcc";

    internal static string RonMorphFilter =>
        $"{Loc.Text("loc_EditorFileDrop_RonHeadMorphLabel")} (*.ron)|*.ron";

    internal static string RonMaterialFilter =>
        $"{Loc.Text("loc_EditorFileDrop_RonMaterialFilesLabel")} (*.ron)|*.ron";

    internal static string TseRonFilter =>
        $"{Loc.Text("loc_EditorFileDrop_TseRonLabel")} (*.ron)|*.ron";

    private static string AllFilesFilter =>
        $"{Loc.Text("loc_EditorFileDrop_AllFilesLabel")} (*.*)|*.*";

    private static readonly HashSet<string> MorphImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ron",
        ".psk",
        ".pskx",
        ".gltf",
        ".glb",
        ".me2headmorph",
        ".me3headmorph"
    };

    internal static EditorFileDropKind Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pcsav", StringComparison.OrdinalIgnoreCase)) return EditorFileDropKind.CareerSave;
        if (extension.Equals(".pcc", StringComparison.OrdinalIgnoreCase))
        {
            return EditorFileDropKind.Package;
        }
        return MorphImportExtensions.Contains(extension)
            ? EditorFileDropKind.MorphImport
            : EditorFileDropKind.Unsupported;
    }
}
