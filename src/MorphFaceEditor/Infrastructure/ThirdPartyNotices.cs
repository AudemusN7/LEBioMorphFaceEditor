using System.IO;
using System.Windows;

namespace MorphFaceEditor.Infrastructure;

/// <summary>Credits for the libraries actually included with the desktop application.</summary>
internal static class ThirdPartyNotices
{
    private const string LecSource = "https://github.com/ME3Tweaks/LegendaryExplorer";

    public static IReadOnlyList<ThirdPartyCredit> All { get; } =
    [
        new("LegendaryExplorerCore", "6.5.0.0", "GNU GPL v3 and component notices",
            "Package reading, writing and Mass Effect data support. Created by the LegendaryExplorer / ME3Explorer community. " +
            "Additional notices below cover the included texture codecs and Psychonauts Explorer audio parsing code.",
            LecSource, "LegendaryExplorer.txt", "TextureCodecs.txt", "PsychonautsExplorer.txt"),
        new("SharpDX", "4.2.0", "MIT",
            "Direct3D 11 preview and shader support. Includes SharpDX, Direct3D11, DXGI, D3DCompiler and Mathematics. Alexandre Mutel and contributors.",
            "https://github.com/sharpdx/SharpDX", "SharpDX.txt"),
        new("SharpGLTF", "1.0.6", "MIT",
            "glTF import and export. Includes Core, Runtime and Toolkit. Vicente Penades and contributors.",
            "https://github.com/vpenades/SharpGLTF", "SharpGLTF.txt"),
        new("BCnEncoder.NET", "2.2.1 / 1.1.2", "MIT (dual-licensed upstream)",
            "Block-compressed texture encoding and decoding. Includes BCnEncoder.Net 2.2.1 and BCnEncoder.Net.ImageSharp 1.1.2. Nominom and contributors. " +
            "Upstream offers MIT or Unlicense; the MIT notice is reproduced here.",
            "https://github.com/Nominom/BCnEncoder.NET", "BCnEncoder.txt"),
        new("ImageSharp", "3.2.0", "Six Labors licence",
            "Image processing. Six Labors and contributors. MFE uses an assembly-scoped Six Labors Community licence. " +
            "The package's licence terms and the referenced Apache 2.0 terms are reproduced below.",
            "https://github.com/SixLabors/ImageSharp", "ImageSharp.txt", "Apache-2.0.txt"),
        new(".NET Community Toolkit", "8.4.0", "MIT",
            "Includes CommunityToolkit.Diagnostics and CommunityToolkit.HighPerformance. Copyright .NET Foundation and Contributors. " +
            "The package's upstream third-party notice is retained in full, including notices for other Toolkit components.",
            "https://github.com/CommunityToolkit/dotnet", "CommunityToolkit.txt", "CommunityToolkit-ThirdPartyNotices.txt"),
        new("RecyclableMemoryStream", "3.0.1", "MIT",
            "Pooled memory streams. Microsoft Corporation and contributors.",
            "https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream", "RecyclableMemoryStream.txt"),
        new("Json.NET", "13.0.4", "MIT",
            "JSON serialization. James Newton-King and contributors.",
            "https://github.com/JamesNK/Newtonsoft.Json", "NewtonsoftJson.txt"),
        new("Serilog", "4.3.0", "Apache 2.0",
            "Structured logging support for LegendaryExplorerCore. Copyright Serilog Contributors.",
            "https://github.com/serilog/serilog", "Serilog.txt"),
        new("TexConverter / DirectXTex", "Bundled native library", "GNU GPL v3 / MIT",
            "Native texture conversion from the pinned LegendaryExplorer source snapshot. Includes Microsoft's DirectXTex texture library (MIT). " +
            "The LegendaryExplorer source and licence are available in the first entry.",
            LecSource, "LegendaryExplorer.txt", "DirectXTex.txt"),
        new("CompressionWrappers", "Bundled native library", "GNU GPL v3 and component notices",
            "Native compression support from MassEffectModder, distributed with the pinned LegendaryExplorer snapshot. " +
            "Includes the upstream notices for MassEffectModder, LZMA SDK, zlib, MiniZip and FastZlib.",
            "https://github.com/MassEffectModder/MassEffectModder", "MassEffectModder.txt", "LzmaSdk.txt", "Zlib.txt", "MiniZip.txt", "FastZlib.txt")
    ];

    internal static string ReadNotice(string filename)
    {
        var uri = new Uri($"pack://application:,,,/MorphFaceEditor;component/Assets/Licenses/{filename}", UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Missing bundled third-party notice: {filename}");
        using var reader = new StreamReader(resource.Stream);
        return reader.ReadToEnd();
    }
}

internal sealed class ThirdPartyCredit(string name, string version, string licence, string description, string projectUrl, params string[] noticeFiles)
{
    public string Name { get; } = name;
    public string Version { get; } = version;
    public string Licence { get; } = licence;
    public string Description { get; } = description;
    public Uri ProjectUri { get; } = new(projectUrl, UriKind.Absolute);

    public string Notice => string.Join("\n\n", noticeFiles.Select(file =>
        $"── {file} ──\n\n{ThirdPartyNotices.ReadNotice(file)}"));
}
