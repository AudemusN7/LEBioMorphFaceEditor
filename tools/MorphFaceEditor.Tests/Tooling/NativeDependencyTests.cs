using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Textures;

namespace MorphFaceEditor.Tests;

internal static class NativeDependencyTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("bundled TexConverter is x64 without Visual C++ debug runtime imports", DistributableTexConverter),
        new("bundled TexConverter converts a known DXT1 block through the pinned LEC wrapper", NativeTextureConversion),
    ];

    private static void DistributableTexConverter()
    {
        // Inspect the copied output, even on developer machines where debug runtimes hide the failure.
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TexConverter.dll"));
        using var pe = new PEReader(stream);
        TestAssert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        var directory = pe.PEHeaders.PEHeader!.ImportTableDirectory;
        TestAssert.True(directory.Size >= 20, "TexConverter has no native import table.");
        var descriptors = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).AsSpan();
        for (var offset = 0; offset + 20 <= descriptors.Length; offset += 20)
        {
            var nameRva = BinaryPrimitives.ReadInt32LittleEndian(descriptors.Slice(offset + 12, 4));
            if (nameRva == 0) break;
            var bytes = pe.GetSectionData(nameRva).GetContent().AsSpan();
            var name = Encoding.ASCII.GetString(bytes[..bytes.IndexOf((byte)0)]);
            TestAssert.True(!Regex.IsMatch(name,
                    @"^(?:msvcp\d+|vcruntime\d+(?:_\d+)?|vcomp\d+|ucrtbase)d\.dll$", RegexOptions.IgnoreCase),
                $"TexConverter imports {name}; Visual C++ debug runtimes are unavailable on end-user machines.");
        }
    }

    private static void NativeTextureConversion()
    {
        // One 4x4 DXT1 block: opaque red endpoint, with every pixel selecting that endpoint.
        byte[] block = [0x00, 0xf8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var pixels = TexConverter.ConvertTexture(block, 4, 4, PixelFormat.DXT1, PixelFormat.ARGB);
        TestAssert.Equal(64, pixels.Length);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            TestAssert.True(pixels.AsSpan(offset, 4).SequenceEqual(new byte[] { 0, 0, 255, 255 }),
                $"TexConverter returned incorrect BGRA pixels at byte {offset}.");
        }
    }
}
