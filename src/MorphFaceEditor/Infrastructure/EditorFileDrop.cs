using System.IO;

namespace MorphFaceEditor.Infrastructure;

internal enum EditorFileDropKind
{
    Unsupported,
    Package,
    MorphImport
}

internal static class EditorFileDrop
{
    internal const string MorphImportFilter =
        "Supported morph files|*.ron;*.psk;*.pskx;*.gltf;*.glb;*.md5;*.md5mesh;*.me2headmorph;*.me3headmorph|" +
        "RON head morph (*.ron)|*.ron|" +
        "Mesh files (*.psk;*.pskx;*.gltf;*.glb;*.md5;*.md5mesh)|*.psk;*.pskx;*.gltf;*.glb;*.md5;*.md5mesh|" +
        "All files (*.*)|*.*";

    private static readonly HashSet<string> MorphImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ron",
        ".psk",
        ".pskx",
        ".gltf",
        ".glb",
        ".md5",
        ".md5mesh",
        ".me2headmorph",
        ".me3headmorph"
    };

    internal static EditorFileDropKind Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pcc", StringComparison.OrdinalIgnoreCase))
        {
            return EditorFileDropKind.Package;
        }
        return MorphImportExtensions.Contains(extension)
            ? EditorFileDropKind.MorphImport
            : EditorFileDropKind.Unsupported;
    }
}
