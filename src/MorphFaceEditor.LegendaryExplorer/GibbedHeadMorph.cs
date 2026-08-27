using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record LegacyHeadMorphImport(
    string HairMesh,
    IReadOnlyList<string> AccessoryMeshes,
    MorphFaceMorphData MorphData,
    MorphFaceMaterialData MaterialData);

/// <summary>Reads Gibbed's legacy ME2/ME3 Unreal-serialized head-morph files.</summary>
internal static class GibbedHeadMorph
{
    private const int HeaderLength = 31;
    private const int MaximumCollectionLength = 10_000_000;
    private static readonly byte[] Me2Magic = "GIBBEDMASSEFFECT2HEADMORPH"u8.ToArray();
    private static readonly byte[] Me3Magic = "GIBBEDMASSEFFECT3HEADMORPH"u8.ToArray();

    public static TseHeadMorph Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException("The Gibbed head morph is shorter than its 31-byte header.");
        }

        var expectedMagic = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".me2headmorph" => Me2Magic,
            ".me3headmorph" => Me3Magic,
            _ => throw new InvalidDataException("The file is not a supported Gibbed head morph.")
        };
        if (!bytes.AsSpan(0, expectedMagic.Length).SequenceEqual(expectedMagic))
        {
            throw new InvalidDataException("The Gibbed head morph signature does not match its file extension.");
        }
        if (bytes[26] is not (0 or 1))
        {
            throw new InvalidDataException($"Unsupported Gibbed head morph format version {bytes[26]}.");
        }

        var formatVersion = bytes[26];
        var reader = new Reader(bytes.AsSpan(formatVersion == 1 ? HeaderLength + 1 : HeaderLength));
        if (formatVersion == 1)
        {
            // The old modified ME2 editor's v1 files add a byte-order marker
            // before the save version and carry the character identity code
            // ahead of the ordinary MorphHead payload.
            _ = reader.ReadString();
        }
        var hair = reader.ReadString();
        var accessories = reader.ReadArray(static (ref Reader value) => value.ReadString());
        var features = reader.ReadMap(static (ref Reader value) => value.ReadSingle())
            .Select(value => new MorphFeatureValue(value.Key, value.Value)).ToArray();
        var bones = reader.ReadMap(static (ref Reader value) => value.ReadVector3())
            .Select(value => new BoneTranslation(value.Key, value.Value)).ToArray();
        var lods = new Vector3[4][];
        for (var index = 0; index < lods.Length; index++)
        {
            lods[index] = reader.ReadArray(static (ref Reader value) => value.ReadVector3());
        }
        var scalars = reader.ReadMap(static (ref Reader value) => value.ReadSingle())
            .Select(value => new ScalarMaterialOverride(value.Key, value.Value)).ToArray();
        var vectors = reader.ReadMap(static (ref Reader value) => value.ReadVector4())
            .Select(value => new VectorMaterialOverride(value.Key, value.Value)).ToArray();
        var textures = reader.ReadMap(static (ref Reader value) => value.ReadString())
            .Select(value => new TextureMaterialOverride(
                value.Key,
                string.Equals(value.Value, "None", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new AssetIdentity(string.Empty, value.Value, 0, "Texture2D"))).ToArray();
        reader.EnsureFinished();

        var lastLod = Array.FindLastIndex(lods, value => value.Length > 0);
        var baked = lastLod < 0 ? [] : lods.Take(lastLod + 1).ToArray();
        if (baked.Length == 0 || baked.Any(value => value.Length == 0))
        {
            throw new InvalidDataException("Gibbed head-morph LOD arrays must begin at LOD0 and remain contiguous.");
        }
        return new TseHeadMorph(
            hair,
            accessories,
            new MorphFaceMorphData(features, bones, baked),
            new MorphFaceMaterialData(scalars, vectors, textures));
    }

    private ref struct Reader
    {
        private ReadOnlySpan<byte> _remaining;

        public Reader(ReadOnlySpan<byte> bytes) => _remaining = bytes;

        public float ReadSingle()
        {
            var value = BitConverter.Int32BitsToSingle(ReadInt32());
            return float.IsFinite(value)
                ? value
                : throw new InvalidDataException("Gibbed head morph contains a non-finite number.");
        }

        public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
        public Vector4 ReadVector4() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

        public string ReadString()
        {
            var length = ReadInt32();
            if (length == 0)
            {
                return string.Empty;
            }
            if (length == int.MinValue)
            {
                throw new InvalidDataException("Invalid Unreal string length in Gibbed head morph.");
            }

            var unicode = length < 0;
            var characterCount = Math.Abs(length);
            var byteCount = checked(characterCount * (unicode ? 2 : 1));
            var bytes = Take(byteCount);
            if (unicode)
            {
                if (bytes[^1] != 0 || bytes[^2] != 0)
                {
                    throw new InvalidDataException("Unterminated Unicode string in Gibbed head morph.");
                }
                return Encoding.Unicode.GetString(bytes[..^2]);
            }
            if (bytes[^1] != 0)
            {
                throw new InvalidDataException("Unterminated string in Gibbed head morph.");
            }
            return Encoding.Latin1.GetString(bytes[..^1]);
        }

        public T[] ReadArray<T>(ReadValue<T> readValue)
        {
            var count = ReadCount();
            var result = new T[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = readValue(ref this);
            }
            return result;
        }

        public KeyValuePair<string, T>[] ReadMap<T>(ReadValue<T> readValue)
        {
            var count = ReadCount();
            var result = new KeyValuePair<string, T>[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = new KeyValuePair<string, T>(ReadString(), readValue(ref this));
            }
            return result;
        }

        public void EnsureFinished()
        {
            if (!_remaining.IsEmpty)
            {
                throw new InvalidDataException($"The Gibbed head morph has {_remaining.Length} unexpected trailing byte(s).");
            }
        }

        private int ReadCount()
        {
            var count = ReadInt32();
            return count is >= 0 and <= MaximumCollectionLength
                ? count
                : throw new InvalidDataException($"Invalid collection length {count} in Gibbed head morph.");
        }

        private int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length)
            {
                throw new EndOfStreamException("Unexpected end of Gibbed head morph.");
            }
            var value = _remaining[..count];
            _remaining = _remaining[count..];
            return value;
        }
    }

    private delegate T ReadValue<T>(ref Reader reader);
}
