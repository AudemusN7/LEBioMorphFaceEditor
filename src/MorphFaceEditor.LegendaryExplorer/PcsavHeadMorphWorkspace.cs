using System.Buffers.Binary;
using System.IO.Compression;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public enum PcsavError { InvalidFile, UnsupportedFormat, MissingHeadMorph, SourceChanged }
public sealed class PcsavException(PcsavError reason, Exception? inner = null)
    : IOException(reason.ToString(), inner)
{
    public PcsavError Reason { get; } = reason;
}

/// <summary>
/// C# implementation of the PC save framing and positional schemas documented by TSE.
/// Only HeadMorph is decoded for editing. Every other uncompressed byte is retained.
/// Reference: KarlitosVII/trilogy-save-editor src/unreal and src/save_data.
/// </summary>
public sealed class PcsavHeadMorphWorkspace : IUndoableEditSource
{
    private const uint Le1Magic = 0x9E2A83C1;
    private const int MaximumFileSize = 64 * 1024 * 1024;
    private byte[] _sourceBytes;
    private readonly byte[] _originalBytes;
    private readonly byte[] _payload;
    private readonly int _headStart;
    private readonly int _headEnd;
    private readonly int _blockSize;
    private readonly HeadMorphPayload? _head;
    private HeadMorphPayload? _editingBaseline;
    private HeadMorphPayload? _importedHead;
    private HeadMorphPayload? _savedImportedHead;
    private HeadMorphPayload? _savedEditingBaseline;
    private readonly Stack<(ImportState Before, ImportState After)> _undoImports = new(), _redoImports = new();
    private sealed record ImportState(HeadMorphPayload? Head, HeadMorphPayload? Baseline);
    public event EventHandler? EditCommitted;
    public event EventHandler? ImportStateChanged;
    public bool HasPendingHeadImport => !ReferenceEquals(_importedHead, _savedImportedHead);
    public bool CanUndo => _undoImports.Count > 0;
    public bool CanRedo => _redoImports.Count > 0;
    public void ClearRedo() => _redoImports.Clear();
    public void Undo()
    {
        var edit = _undoImports.Pop();
        _redoImports.Push(edit);
        (_importedHead, _editingBaseline) = (edit.Before.Head, edit.Before.Baseline);
        ImportStateChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Redo()
    {
        var edit = _redoImports.Pop();
        _undoImports.Push(edit);
        (_importedHead, _editingBaseline) = (edit.After.Head, edit.After.Baseline);
        ImportStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private PcsavHeadMorphWorkspace(string path, byte[] bytes)
    {
        SourcePath = Path.GetFullPath(path);
        _sourceBytes = bytes;
        _originalBytes = bytes;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        Game = version switch
        {
            Le1Magic => MorphFaceGame.LE1, 30 => MorphFaceGame.LE2, 59 => MorphFaceGame.LE3,
            _ => throw new PcsavException(PcsavError.UnsupportedFormat)
        };
        var checksumOffset = bytes.Length - (Game == MorphFaceGame.LE1 ? 12 : 4);
        if (checksumOffset < 4 || Crc32(bytes.AsSpan(0, checksumOffset)) !=
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checksumOffset)))
            throw new PcsavException(PcsavError.InvalidFile);
        if (Game == MorphFaceGame.LE1)
            (_payload, _blockSize) = Decompress(bytes);
        else
            _payload = bytes[..checksumOffset];
        var reader = new UnrealHeadMorphBinaryCodec.Reader(_payload);
        if (Game == MorphFaceGame.LE1) ReadLe1Prefix(ref reader);
        else ReadLe2Or3Prefix(ref reader, Game == MorphFaceGame.LE3);
        Sex = reader.Bool() ? StandalonePlayerSex.Female : StandalonePlayerSex.Male;
        if (Game == MorphFaceGame.LE1)
        {
            reader.Skip(13); // localised class, byte class enum, level, XP
            FirstName = reader.String();
            reader.Skip(10); // last name, byte origin/notoriety enums, specialisation
            reader.Skip(1);  // spectre rank
            reader.Skip(8);  // talent points and pool
            _ = reader.String(); // mapped talent
        }
        else
        {
            _ = reader.String(); // class name
            if (Game == MorphFaceGame.LE3) { _ = reader.Bool(); _ = reader.Bool(); _ = reader.Bool(); }
            reader.Skip(8); // level, XP
            FirstName = reader.String();
            reader.Skip(10); // last name, byte origin/notoriety enums, talent points
            for (var i = 0; i < 3; i++) _ = reader.String(); // mapped powers
            reader.Skip(1 + 13 * 4); // byte appearance type and thirteen outfit/colour IDs
        }
        _headStart = reader.Position;
        if (reader.Bool()) _head = UnrealHeadMorphBinaryCodec.Read(ref reader);
        _headEnd = reader.Position;
    }

    public string SourcePath { get; private set; }
    public MorphFaceGame Game { get; }
    public StandalonePlayerSex Sex { get; }
    public string FirstName { get; }
    public bool HasHeadMorph => _head is not null;
    public HeadMorphPayload Head => _head ?? throw new PcsavException(PcsavError.MissingHeadMorph);
    internal ReadOnlySpan<byte> UnrelatedPrefix => _payload.AsSpan(0, _headStart);
    internal ReadOnlySpan<byte> UnrelatedSuffix => _payload.AsSpan(_headEnd);

    public static PcsavHeadMorphWorkspace Open(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is < 16 or > MaximumFileSize) throw new PcsavException(PcsavError.InvalidFile);
            return new(path, File.ReadAllBytes(path));
        }
        catch (PcsavException) { throw; }
        catch (Exception e) when (e is IOException or ArgumentException or OverflowException)
        { throw new PcsavException(PcsavError.InvalidFile, e); }
    }

    public HeadMorphPayload PrepareEditingHead(StandalonePlayerAssetCatalog assets,
        out IReadOnlyList<UserFacingMessage> unavailableAttachments)
    {
        var head = Head;
        var missing = new List<UserFacingMessage>();
        string? PreviewMesh(string path, string messageKey)
        {
            if (path.Equals("None", StringComparison.OrdinalIgnoreCase) || assets.SkeletalMeshes.Any(v =>
                v.InstancedPath.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(v.PackagePath))) return path;
            missing.Add(UserFacingMessage.Create(messageKey, path));
            return null;
        }
        var hair = PreviewMesh(head.HairMesh, "loc_Pcsav_UnavailableHair") ?? "None";
        var accessories = head.AccessoryMeshes.Select(path => PreviewMesh(path, "loc_Pcsav_UnavailableAccessory"))
            .OfType<string>().ToArray();
        unavailableAttachments = missing;
        return head with { HairMesh = hair, AccessoryMeshes = accessories, MaterialData = head.MaterialData with
            { Textures = head.MaterialData.Textures.Where(v => !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(v.Name)).ToArray() } };
    }

    /// <summary>Capture MFE's projection once so unavailable assets/controls do not erase native data on save.</summary>
    public void SetEditingBaseline(HeadMorphPayload head)
    {
        _editingBaseline = head;
        _savedEditingBaseline ??= head;
    }

    public void DiscardHeadImport()
    {
        (_importedHead, _editingBaseline) = (_savedImportedHead, _savedEditingBaseline);
        _undoImports.Clear();
        _redoImports.Clear();
        ImportStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetImportedHead(HeadMorphPayload head, MorphFaceDocument draft, bool recordHistory = false)
    {
        var before = new ImportState(_importedHead, _editingBaseline);
        if (!string.Equals(head.HairMesh, (_importedHead ?? _head)?.HairMesh, StringComparison.OrdinalIgnoreCase))
            head = head with { MaterialData = head.MaterialData with { Textures = head.MaterialData.Textures
                .Where(v => !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(v.Name)).ToArray() } };
        _importedHead = head;
        SetEditingBaseline(draft);
        if (recordHistory)
        {
            _undoImports.Push((before, new(_importedHead, _editingBaseline)));
            _redoImports.Clear();
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _undoImports.Clear();
            _redoImports.Clear();
        }
        ImportStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetEditingBaseline(MorphFaceDocument draft) => SetEditingBaseline(EditingHead(draft));

    public void SaveEditedHead(MorphFaceDocument draft, string? destination = null) =>
        SaveEditedHead(EditingHead(draft), destination);

    private static HeadMorphPayload EditingHead(MorphFaceDocument draft) => new(
        draft.HairMeshReference?.InstancedPath ?? "None",
        draft.OtherMeshReferences.OfType<AssetIdentity>().Select(v => v.InstancedPath).ToArray(),
        new(draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods),
        new(draft.MaterialOverrides.Scalars, draft.MaterialOverrides.Vectors,
            draft.MaterialOverrides.Textures.Where(v => !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(v.Name)).ToArray()));

    public void SaveEditedHead(HeadMorphPayload edited, string? destination = null)
    {
        var baseline = _editingBaseline ?? throw new InvalidOperationException("No editing baseline was captured.");
        var merged = PcsavHeadMorphMerge.ApplyChanges(_importedHead ?? _head ?? baseline, baseline, edited);
        // Native references omitted from the editing projection remain authoritative.
        // A full replacement or newly created head must prove every runtime reference.
        new PlayerRuntimeAssetValidator().Verify(Game, merged, _importedHead is null ? _head : null);
        Commit(ReplaceHead(merged), merged, destination);
        _savedImportedHead = _importedHead;
        _savedEditingBaseline = _editingBaseline;
    }

    public void DeleteHeadMorph() => Commit(ReplaceHead(null), null);

    private void Commit(byte[] bytes, HeadMorphPayload? head, string? destination = null)
    {
        var target = Path.GetFullPath(destination ?? SourcePath);
        var savingSource = target.Equals(SourcePath, StringComparison.OrdinalIgnoreCase);
        var targetBytes = savingSource ? _sourceBytes : File.Exists(target) ? File.ReadAllBytes(target) : null;
        using var save = FileSaveTransaction.ForContent(target, targetBytes);
        try
        {
            File.WriteAllBytes(save.CandidatePath, bytes);
            var candidate = Open(save.CandidatePath);
            if (!UnrelatedPrefix.SequenceEqual(candidate.UnrelatedPrefix) ||
                !UnrelatedSuffix.SequenceEqual(candidate.UnrelatedSuffix) ||
                candidate.HasHeadMorph != (head is not null) ||
                head is not null && !UnrealHeadMorphBinaryCodec.Write(head).AsSpan().SequenceEqual(UnrealHeadMorphBinaryCodec.Write(candidate.Head)))
                throw new PcsavException(PcsavError.InvalidFile);
            save.Commit(nameof(PcsavError.SourceChanged));
            SourcePath = target;
            _sourceBytes = bytes;
            // Keep the immutable native/baseline pair: semantic deltas include all edits since opening.
        }
        catch (FileSaveConflictException exception) { throw new PcsavException(PcsavError.SourceChanged, exception); }
    }

    internal byte[] ReplaceHead(HeadMorphPayload? head)
    {
        if (head is null && _head is null || head is not null && _head is not null &&
            UnrealHeadMorphBinaryCodec.Write(head).AsSpan().SequenceEqual(UnrealHeadMorphBinaryCodec.Write(_head)))
        {
            // Preserve the original compression, map order and string encodings for an untouched save.
            return _originalBytes.ToArray();
        }
        var encoded = head is null ? [] : UnrealHeadMorphBinaryCodec.Write(head);
        var body = new byte[encoded.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(body, head is null ? 0 : 1);
        encoded.CopyTo(body, 4);
        var payload = new byte[checked(_headStart + body.Length + _payload.Length - _headEnd)];
        UnrelatedPrefix.CopyTo(payload);
        body.CopyTo(payload, _headStart);
        UnrelatedSuffix.CopyTo(payload.AsSpan(_headStart + body.Length));
        if (Game != MorphFaceGame.LE1)
        {
            var output = new byte[payload.Length + 4]; payload.CopyTo(output, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(payload.Length), Crc32(payload));
            return output;
        }
        return Compress(payload, _blockSize);
    }

    private static void ReadLe1Prefix(ref UnrealHeadMorphBinaryCodec.Reader r)
    {
        if (r.Int32() != 50) throw new PcsavException(PcsavError.UnsupportedFormat);
        _ = r.String(); r.Skip(16); // character ID, created date
        for (var i = 0; i < 3; i++) SkipArray(ref r, 4); // plot bitfields, integers, floats
        r.Skip(4); // journal progress counter
        var quests = r.Count(12);
        for (var i = 0; i < quests; i++) { r.Skip(4); _ = r.Bool(); SkipArray(ref r, 4); }
        SkipArray(ref r, 4); // quest IDs
        var entries = r.Count(4);
        for (var i = 0; i < entries; i++) SkipArray(ref r, 8); // codex pages + bools
        SkipArray(ref r, 4); // codex IDs
        r.Skip(20); // timestamp, seconds played
    }

    private static void ReadLe2Or3Prefix(ref UnrealHeadMorphBinaryCodec.Reader r, bool le3)
    {
        _ = r.Int32(); _ = r.String(); r.Skip(8); _ = r.String();
        if (le3) _ = r.String(); // level display override
        r.Skip(49); // byte difficulty, u32 end game, timestamp, location, rotation, loading tip
        var levels = r.Count(12);
        for (var i = 0; i < levels; i++) { _ = r.String(); _ = r.Bool(); _ = r.Bool(); }
        var streaming = r.Count(8);
        for (var i = 0; i < streaming; i++) { _ = r.String(); _ = r.Bool(); }
        SkipArray(ref r, 20); // kismet GUID + bool
        SkipArray(ref r, 18); // doors: GUID + two bytes
        if (le3) SkipArray(ref r, 18); // placeables: GUID + two byte enum values
        SkipArray(ref r, 16); // pawn GUIDs
    }
    private static void SkipArray(ref UnrealHeadMorphBinaryCodec.Reader r, int stride) => r.Skip(checked(r.Count(stride) * stride));

    private static (byte[] Payload, int BlockSize) Decompress(byte[] bytes)
    {
        var r = new UnrealHeadMorphBinaryCodec.Reader(bytes);
        r.Skip(4); var block = r.Int32(); var compressed = r.Int32(); var size = r.Int32();
        if (block is <= 0 or > MaximumFileSize || size is <= 0 or > MaximumFileSize || compressed <= 0)
            throw new PcsavException(PcsavError.InvalidFile);
        var count = checked((size + block - 1) / block);
        var headers = new (int Compressed, int Size)[count];
        var sum = 0;
        for (var i = 0; i < count; i++)
        {
            headers[i] = (r.Int32(), r.Int32());
            if (headers[i].Compressed <= 0 || headers[i].Size != Math.Min(block, size - i * block))
                throw new PcsavException(PcsavError.InvalidFile);
            sum = checked(sum + headers[i].Compressed);
        }
        if (sum != compressed || r.Remaining != compressed + 12) throw new PcsavException(PcsavError.InvalidFile);
        var output = new byte[size]; var offset = 0;
        foreach (var header in headers)
        {
            using var source = new MemoryStream(r.Take(header.Compressed).ToArray());
            using var z = new ZLibStream(source, CompressionMode.Decompress);
            z.ReadExactly(output.AsSpan(offset, header.Size));
            if (z.ReadByte() != -1) throw new PcsavException(PcsavError.InvalidFile);
            offset += header.Size;
        }
        r.Skip(4); // checksum already checked
        if (r.Int32() != 1 || r.Int32() != size) throw new PcsavException(PcsavError.InvalidFile);
        return (output, block);
    }

    private static byte[] Compress(byte[] payload, int block)
    {
        var chunks = new List<(byte[] Data, int Size)>();
        for (var offset = 0; offset < payload.Length; offset += block)
        {
            var size = Math.Min(block, payload.Length - offset);
            using var stream = new MemoryStream();
            using (var z = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true)) z.Write(payload.AsSpan(offset, size));
            chunks.Add((stream.ToArray(), size));
        }
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(Le1Magic); writer.Write(block); writer.Write(chunks.Sum(v => v.Data.Length)); writer.Write(payload.Length);
        foreach (var chunk in chunks) { writer.Write(chunk.Data.Length); writer.Write(chunk.Size); }
        foreach (var chunk in chunks) writer.Write(chunk.Data);
        writer.Write(Crc32(output.ToArray())); writer.Write(1); writer.Write(payload.Length);
        return output.ToArray();
    }

    // CRC-32/BZIP2: polynomial 0x04C11DB7, initial/final XOR 0xFFFFFFFF, no reflection.
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++) crc = crc << 1 ^ ((crc & 0x80000000) != 0 ? 0x04C11DB7u : 0);
        }
        return ~crc;
    }
}
