using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Detached material values and decoded assets, ready for one grouped edit.</summary>
public sealed record PreparedMaterialImport(
    MorphFaceMaterialData Parameters,
    IReadOnlyDictionary<string, DecodedTextureAsset?> Textures,
    IReadOnlyList<string> Warnings,
    TsePreviewAttachmentImport? PreviewAttachments = null,
    bool IsTse = false);

public sealed record TsePreviewAttachmentImport(
    bool ApplyHair,
    AssetIdentity? Hair,
    bool ApplyAccessory,
    AssetIdentity? Accessory);
