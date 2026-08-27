using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Services;

public enum MorphFaceClipboardKind
{
    Morph,
    Material
}

/// <summary>
/// Versioned interchange envelope used for both the Windows clipboard and
/// deterministic codec tests. ProfileKey prevents cross-species/game pastes.
/// </summary>
public sealed record MorphFaceClipboardPayload(
    int FormatVersion,
    MorphFaceClipboardKind Kind,
    string ProfileKey,
    string SourceFacePath,
    MorphFaceMorphData? MorphData,
    MorphFaceMaterialData? MaterialData)
{
    public const int CurrentFormatVersion = 1;

    public static MorphFaceClipboardPayload Morph(
        string profileKey,
        string sourceFacePath,
        MorphFaceMorphData data) =>
        new(CurrentFormatVersion, MorphFaceClipboardKind.Morph, profileKey, sourceFacePath, data, null);

    public static MorphFaceClipboardPayload Material(
        string profileKey,
        string sourceFacePath,
        MorphFaceMaterialData data) =>
        new(CurrentFormatVersion, MorphFaceClipboardKind.Material, profileKey, sourceFacePath, null, data);
}

public interface IMorphFaceClipboardService
{
    void Set(MorphFaceClipboardPayload payload);
    MorphFaceClipboardPayload Get(MorphFaceClipboardKind expectedKind);
    bool Contains(MorphFaceClipboardKind expectedKind);
}

public static class MorphFaceClipboardCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(MorphFaceClipboardPayload payload)
    {
        Validate(payload, payload.Kind);
        return JsonSerializer.Serialize(payload, Options);
    }

    public static MorphFaceClipboardPayload Deserialize(string json, MorphFaceClipboardKind expectedKind)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The clipboard is empty.");
        }
        MorphFaceClipboardPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<MorphFaceClipboardPayload>(json, Options)
                ?? throw new InvalidDataException("The clipboard payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The clipboard does not contain valid BioMorphFace editor data.", exception);
        }
        Validate(payload, expectedKind);
        return payload;
    }

    private static void Validate(MorphFaceClipboardPayload payload, MorphFaceClipboardKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.FormatVersion != MorphFaceClipboardPayload.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Clipboard format {payload.FormatVersion} is not supported by this editor version.");
        }
        if (payload.Kind != expectedKind)
        {
            throw new InvalidDataException(
                $"The clipboard contains {payload.Kind.ToString().ToLowerInvariant()} data, not {expectedKind.ToString().ToLowerInvariant()} data.");
        }
        if (string.IsNullOrWhiteSpace(payload.ProfileKey) || string.IsNullOrWhiteSpace(payload.SourceFacePath))
        {
            throw new InvalidDataException("The clipboard payload has no source profile or face identity.");
        }
        if ((expectedKind == MorphFaceClipboardKind.Morph && payload.MorphData is null) ||
            (expectedKind == MorphFaceClipboardKind.Material && payload.MaterialData is null))
        {
            throw new InvalidDataException("The clipboard payload is missing its authored data.");
        }
    }
}

/// <summary>Persists versioned morph data to a private format plus readable JSON text.</summary>
public sealed class WpfMorphFaceClipboardService : IMorphFaceClipboardService
{
    private const string ClipboardFormat = "Audemus.MorphFaceEditor.Data.v1";

    public void Set(MorphFaceClipboardPayload payload)
    {
        var json = MorphFaceClipboardCodec.Serialize(payload);
        var data = new DataObject();
        data.SetData(ClipboardFormat, json);
        data.SetData(DataFormats.UnicodeText, json);
        Clipboard.SetDataObject(data, copy: true);
    }

    public MorphFaceClipboardPayload Get(MorphFaceClipboardKind expectedKind)
    {
        var json = Clipboard.GetData(ClipboardFormat) as string
                   ?? (Clipboard.ContainsText(TextDataFormat.UnicodeText)
                       ? Clipboard.GetText(TextDataFormat.UnicodeText)
                       : null);
        return MorphFaceClipboardCodec.Deserialize(json ?? string.Empty, expectedKind);
    }

    public bool Contains(MorphFaceClipboardKind expectedKind)
    {
        try
        {
            _ = Get(expectedKind);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
