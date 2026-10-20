using System.IO;
using System.Security.Cryptography;
using MorphFaceEditor.Core.Services;

namespace MorphFaceEditor.Services;

/// <summary>One captured file supplies routing, import data and source-change detection.</summary>
internal sealed class RonImportFile
{
    private readonly Lazy<RonInputSnapshot> _input;
    private RonImportFile(string text, string fingerprint)
    {
        Fingerprint = fingerprint;
        _input = new(() => RonInputSnapshot.Parse(text, "imported"));
    }

    // Callers evaluate syntax and schemas on a worker thread, inside their format-specific error handler.
    public RonInputSnapshot Input => _input.Value;
    public string Fingerprint { get; }

    public static async Task<RonImportFile> ReadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        return await Task.Run(() => new RonImportFile(text,
            Convert.ToHexString(SHA256.HashData(bytes))));
    }
}
