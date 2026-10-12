using System.IO.Compression;
using SharpDX.D3DCompiler;

if (args.Length != 2)
    throw new ArgumentException("Expected shader source and output bundle paths.");

var source = File.ReadAllText(args[0]);
var output = Path.GetFullPath(args[1]);
var entries = new[]
{
    ("VSMain", "vs_5_0"), ("PSMain", "ps_5_0"), ("PSShadow", "ps_5_0"),
    ("VSPostProcess", "vs_5_0"), ("PSPostProcess", "ps_5_0"),
    ("PSAoSurfaceMsaa", "ps_5_0"), ("PSAoSurfaceSingle", "ps_5_0"),
    ("PSAmbientOcclusion", "ps_5_0"), ("PSAoBlur", "ps_5_0"),
    ("PSApplyAmbientOcclusion", "ps_5_0"), ("PSBloomExtract", "ps_5_0"),
    ("PSBloomBlur", "ps_5_0")
};
using var bundle = new MemoryStream();
using (var archive = new ZipArchive(bundle, ZipArchiveMode.Create, leaveOpen: true))
{
    foreach (var (entryPoint, profile) in entries)
    {
        using var compiled = ShaderBytecode.Compile(source, entryPoint, profile,
            ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness, EffectFlags.None);
        if (compiled.HasErrors)
            throw new InvalidOperationException(compiled.Message);
        var entry = archive.CreateEntry($"{entryPoint}.{profile}.cso", CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var destination = entry.Open();
        destination.Write(compiled.Bytecode.Data);
    }
}
// Publish only after every entry has compiled successfully.
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllBytes(output, bundle.ToArray());
