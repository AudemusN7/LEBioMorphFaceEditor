using System.IO;
using System.IO.Compression;
using SharpDX.D3DCompiler;

namespace MorphFaceEditor.Rendering;

/// <summary>Loads build-compiled shaders without running the HLSL compiler in the preview.</summary>
internal static class HeadPreviewShaders
{
    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Bytecode = new(ReadBundle);

    internal static ShaderBytecode Load(string entryPoint, string profile) =>
        new(Bytecode.Value[$"{entryPoint}.{profile}.cso"]);

    private static IReadOnlyDictionary<string, byte[]> ReadBundle()
    {
        const string name = "MorphFaceEditor.Rendering.Shaders.HeadPreviewShaders.zip";
        using var source = typeof(HeadPreviewShaders).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded shader bundle '{name}' was not found.");
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }, StringComparer.Ordinal);
    }
}
