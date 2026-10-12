using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Unreal's shared PC HeadMorph wire layout, also used by TSE and Gibbed.</summary>
internal static class UnrealHeadMorphBinaryCodec
{
    internal static TseHeadMorph Read(ref Reader reader)
    {
        var hair = reader.String();
        var accessories = reader.Array(static (ref Reader r) => r.String());
        var features = reader.Map(static (ref Reader r) => r.Single())
            .Select(v => new MorphFeatureValue(v.Key, v.Value)).ToArray();
        var bones = reader.Map(static (ref Reader r) => r.Vector3())
            .Select(v => new BoneTranslation(v.Key, v.Value)).ToArray();
        var lods = new Vector3[4][];
        for (var i = 0; i < 4; i++) lods[i] = reader.Array(static (ref Reader r) => r.Vector3(), 12);
        var scalars = reader.Map(static (ref Reader r) => r.Single())
            .Select(v => new ScalarMaterialOverride(v.Key, v.Value)).ToArray();
        var vectors = reader.Map(static (ref Reader r) => r.Vector4())
            .Select(v => new VectorMaterialOverride(v.Key, v.Value)).ToArray();
        var textures = reader.Map(static (ref Reader r) => r.String()).Select(v => new TextureMaterialOverride(
            v.Key, v.Value.Equals("None", StringComparison.OrdinalIgnoreCase) ? null :
                new AssetIdentity(string.Empty, v.Value, 0, "Texture2D"))).ToArray();
        var last = System.Array.FindLastIndex(lods, v => v.Length > 0);
        var baked = lods.Take(last + 1).ToArray();
        if (baked.Length == 0 || baked.Any(v => v.Length == 0))
            throw new InvalidDataException("HeadMorph LOD arrays must begin at LOD0 and remain contiguous.");
        return new(hair, accessories, new(features, bones, baked), new(scalars, vectors, textures));
    }

    internal static byte[] Write(TseHeadMorph head)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void String(string value)
        {
            if (value.Length == 0) { writer.Write(0); return; }
            // ASCII and UTF-16 cover newly authored asset names without lossy conversion.
            if (value.All(c => c < 128))
            {
                writer.Write(value.Length + 1); writer.Write(Encoding.ASCII.GetBytes(value)); writer.Write((byte)0);
            }
            else
            {
                writer.Write(-(value.Length + 1)); writer.Write(Encoding.Unicode.GetBytes(value)); writer.Write((ushort)0);
            }
        }
        void Vector(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        void Array<T>(IReadOnlyList<T> values, Action<T> write)
        {
            writer.Write(values.Count); foreach (var value in values) write(value);
        }
        String(head.HairMesh);
        Array(head.AccessoryMeshes, String);
        Array(head.MorphData.MorphFeatures, v => { String(v.Name); writer.Write(v.Offset); });
        Array(head.MorphData.FinalSkeleton, v => { String(v.BoneName); Vector(v.Translation); });
        for (var lod = 0; lod < 4; lod++) Array(head.MorphData.BakedLods.ElementAtOrDefault(lod) ?? [], Vector);
        Array(head.MaterialData.Scalars, v => { String(v.Name); writer.Write(v.Value); });
        Array(head.MaterialData.Vectors, v =>
        {
            String(v.Name); writer.Write(v.Value.X); writer.Write(v.Value.Y); writer.Write(v.Value.Z); writer.Write(v.Value.W);
        });
        Array(head.MaterialData.Textures, v => { String(v.Name); String(v.TextureReference?.InstancedPath ?? "None"); });
        return stream.ToArray();
    }

    internal ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        internal int Position { get; private set; }
        internal int Remaining => _bytes.Length - Position;
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining) throw new EndOfStreamException("Unexpected end of Unreal save data.");
            var result = _bytes.Slice(Position, count); Position += count; return result;
        }
        internal void Skip(int count) => _ = Take(count);
        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal bool Bool() => Int32() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid Unreal boolean.") };
        internal int Count(int minimumBytes = 1)
        {
            var value = Int32();
            if (value < 0 || value > Remaining / minimumBytes) throw new InvalidDataException("Invalid Unreal collection length.");
            return value;
        }
        internal string String()
        {
            var length = Int32();
            if (length == 0) return string.Empty;
            if (length == int.MinValue) throw new InvalidDataException("Invalid Unreal string length.");
            var unicode = length < 0;
            var size = checked(Math.Abs(length) * (unicode ? 2 : 1));
            var data = Take(size);
            if (data[^1] != 0 || unicode && data[^2] != 0) throw new InvalidDataException("Unterminated Unreal string.");
            if (unicode) return new UnicodeEncoding(false, false, true).GetString(data[..^2]);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(data[..^1]);
        }
        internal float Single()
        {
            var value = BitConverter.Int32BitsToSingle(Int32());
            return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite HeadMorph value.");
        }
        internal Vector3 Vector3() => new(Single(), Single(), Single());
        internal Vector4 Vector4() => new(Single(), Single(), Single(), Single());
        internal T[] Array<T>(ReadValue<T> read, int minimumBytes = 4)
        {
            var result = new T[Count(minimumBytes)];
            for (var i = 0; i < result.Length; i++) result[i] = read(ref this);
            return result;
        }
        internal KeyValuePair<string, T>[] Map<T>(ReadValue<T> read)
        {
            var result = new KeyValuePair<string, T>[Count(8)];
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < result.Length; i++)
            {
                var name = String();
                if (!names.Add(name)) throw new InvalidDataException("Duplicate HeadMorph map key.");
                result[i] = new(name, read(ref this));
            }
            return result;
        }
    }
    internal delegate T ReadValue<T>(ref Reader reader);
}
