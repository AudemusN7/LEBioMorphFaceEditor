using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

internal static class TextureRegistryUnpacker
{
    private const int HeaderSize = sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(long);
    private const long MaximumJsonBytes = 256L * 1024 * 1024;

    public static void Run(string inputFile, string? outputFile)
    {
        var inputPath = Path.GetFullPath(inputFile);
        var outputPath = Path.GetFullPath(outputFile ?? Path.ChangeExtension(inputPath, ".json"));
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The JSON output must be a different file from the MFTR input.");

        using var input = File.OpenRead(inputPath);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        if (input.Length < HeaderSize || reader.ReadUInt32() != TextureRegistryStore.Magic)
            throw new InvalidDataException("The MFTR header is malformed.");

        var schemaVersion = reader.ReadInt32();
        var game = reader.ReadInt32();
        var jsonLength = reader.ReadInt64();
        if (schemaVersion <= 0 || game is < 0 or > 2 || jsonLength is <= 0 or > MaximumJsonBytes)
            throw new InvalidDataException("The MFTR header contains an invalid schema, game, or JSON size.");

        // Read the wire format directly so older registries remain inspectable after
        // the editor's strongly typed registry schema has advanced.
        var json = new byte[checked((int)jsonLength)];
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            brotli.ReadExactly(json);
            if (brotli.ReadByte() != -1)
                throw new InvalidDataException("The MFTR payload exceeds its declared JSON size.");
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The MFTR payload is not a JSON object.");
        var formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, formatted, new UTF8Encoding(false));
        Console.WriteLine($"Unpacked schema v{schemaVersion} {(MorphFaceGame)game} to {outputPath}");
    }
}
