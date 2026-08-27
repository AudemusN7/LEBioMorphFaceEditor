using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace MorphFaceEditor.Core.Randomisation;

/// <summary>Reads and writes the compact deterministic donor resource embedded by the application.</summary>
public static class MorphRandomisationBundleSerializer
{
    private static readonly byte[] Magic = "MFR1"u8.ToArray();

    public static void Write(Stream output, MorphRandomisationCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(corpus);
        using var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        using var writer = new BinaryWriter(brotli, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(corpus.FormatVersion);
        var pools = corpus.Pools.OrderBy(value => value.Key).ToArray();
        writer.Write(pools.Length);
        foreach (var (key, donorsValue) in pools)
        {
            writer.Write((int)key);
            var donors = donorsValue.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
            writer.Write(donors.Length);
            foreach (var donor in donors)
            {
                writer.Write(donor.Id);
                writer.Write(donor.SourceProfileKey);
                var available = donor.AvailableFeatures.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                writer.Write(available.Length);
                foreach (var name in available)
                {
                    writer.Write(name);
                }
                var values = donor.NonZeroValues.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
                writer.Write(values.Length);
                foreach (var (name, value) in values)
                {
                    writer.Write(name);
                    writer.Write(value);
                }
                WriteScalars(writer, donor.MaterialScalars);
                WriteVectors(writer, donor.MaterialVectors);
                var families = donor.MaterialTextureFamilies
                    .OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
                writer.Write(families.Length);
                foreach (var family in families)
                {
                    writer.Write(family.Key);
                    var textures = family.Value.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
                    writer.Write(textures.Length);
                    foreach (var texture in textures)
                    {
                        writer.Write(texture.Key);
                        writer.Write(texture.Value);
                    }
                }
            }
        }
        var profiles = corpus.MaterialProfiles.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
        writer.Write(profiles.Length);
        foreach (var pair in profiles)
        {
            writer.Write(pair.Key);
            var profile = pair.Value;
            var scalars = profile.Scalars.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
            writer.Write(scalars.Length);
            foreach (var scalar in scalars)
            {
                writer.Write(scalar.Key);
                writer.Write(scalar.Value.Minimum);
                writer.Write(scalar.Value.Maximum);
                writer.Write(scalar.Value.P10);
                writer.Write(scalar.Value.P90);
            }
            var vectors = profile.Vectors.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
            writer.Write(vectors.Length);
            foreach (var vector in vectors)
            {
                writer.Write(vector.Key);
                writer.Write((int)vector.Value.Kind);
                WriteVector(writer, vector.Value.Minimum);
                WriteVector(writer, vector.Value.Maximum);
                writer.Write(vector.Value.SelectorStates.Count);
                foreach (var state in vector.Value.SelectorStates
                             .OrderByDescending(value => value.Count)
                             .ThenBy(value => value.Value.X)
                             .ThenBy(value => value.Value.Y)
                             .ThenBy(value => value.Value.Z)
                             .ThenBy(value => value.Value.W))
                {
                    WriteVector(writer, state.Value);
                    writer.Write(state.Count);
                }
            }
            var families = profile.TextureFamilies.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            writer.Write(families.Length);
            foreach (var family in families) writer.Write(family);
        }
    }

    public static MorphRandomisationCorpus Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(brotli, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Morph-randomisation bundle header is invalid.");
        }
        var formatVersion = reader.ReadInt32();
        if (formatVersion != MorphRandomisationCorpus.CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported morph-randomisation bundle version {formatVersion}.");
        }
        var pools = new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>();
        foreach (var _ in Enumerable.Range(0, ReadCount(reader, "pool")))
        {
            var key = (MorphRandomisationPoolKey)reader.ReadInt32();
            if (!Enum.IsDefined(key) || pools.ContainsKey(key))
            {
                throw new InvalidDataException($"Morph-randomisation bundle contains invalid pool '{key}'.");
            }
            var donors = new List<MorphRandomisationDonor>();
            foreach (var __ in Enumerable.Range(0, ReadCount(reader, "donor")))
            {
                var id = reader.ReadString();
                var profile = reader.ReadString();
                var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ___ in Enumerable.Range(0, ReadCount(reader, "available feature")))
                {
                    if (!available.Add(reader.ReadString()))
                    {
                        throw new InvalidDataException($"Donor '{id}' contains duplicate available features.");
                    }
                }
                var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var ___ in Enumerable.Range(0, ReadCount(reader, "non-zero feature")))
                {
                    var name = reader.ReadString();
                    var value = reader.ReadSingle();
                    if (!float.IsFinite(value) || value == 0 || !values.TryAdd(name, value))
                    {
                        throw new InvalidDataException($"Donor '{id}' contains invalid non-zero feature data.");
                    }
                }
                var scalars = ReadScalars(reader, id);
                var vectors = ReadVectors(reader, id);
                var families = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var ___ in Enumerable.Range(0, ReadCount(reader, "texture family")))
                {
                    var family = reader.ReadString();
                    var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ____ in Enumerable.Range(0, ReadCount(reader, "texture family member")))
                    {
                        if (!textures.TryAdd(reader.ReadString(), reader.ReadString()))
                        {
                            throw new InvalidDataException($"Donor '{id}' contains a duplicate texture-family member.");
                        }
                    }
                    if (!families.TryAdd(family, textures))
                    {
                        throw new InvalidDataException($"Donor '{id}' contains a duplicate texture family.");
                    }
                }
                donors.Add(new MorphRandomisationDonor(id, profile, available, values)
                {
                    MaterialScalars = scalars,
                    MaterialVectors = vectors,
                    MaterialTextureFamilies = families
                });
            }
            pools.Add(key, donors);
        }
        var profiles = new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var _ in Enumerable.Range(0, ReadCount(reader, "material profile")))
        {
            var key = reader.ReadString();
            var scalars = new Dictionary<string, MaterialScalarStatistics>(StringComparer.OrdinalIgnoreCase);
            foreach (var __ in Enumerable.Range(0, ReadCount(reader, "material scalar statistic")))
            {
                var name = reader.ReadString();
                if (!scalars.TryAdd(name, new MaterialScalarStatistics(
                        name, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())))
                {
                    throw new InvalidDataException($"Material profile '{key}' contains a duplicate scalar.");
                }
            }
            var vectors = new Dictionary<string, MaterialVectorStatistics>(StringComparer.OrdinalIgnoreCase);
            foreach (var __ in Enumerable.Range(0, ReadCount(reader, "material vector statistic")))
            {
                var name = reader.ReadString();
                var kind = (MaterialVectorRandomisationKind)reader.ReadInt32();
                if (!Enum.IsDefined(kind))
                {
                    throw new InvalidDataException($"Material vector '{name}' has an invalid kind.");
                }
                var minimum = ReadVector(reader);
                var maximum = ReadVector(reader);
                var states = Enumerable.Range(0, ReadCount(reader, "selector state"))
                    .Select(___ => new MaterialSelectorState(ReadVector(reader), reader.ReadInt32())).ToArray();
                if (!vectors.TryAdd(name, new MaterialVectorStatistics(name, kind, minimum, maximum, states)))
                {
                    throw new InvalidDataException($"Material profile '{key}' contains a duplicate vector.");
                }
            }
            var families = Enumerable.Range(0, ReadCount(reader, "material texture-family name"))
                .Select(__ => reader.ReadString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!profiles.TryAdd(key, new MaterialRandomisationProfile(key, scalars, vectors, families)))
            {
                throw new InvalidDataException($"Morph-randomisation bundle contains duplicate material profile '{key}'.");
            }
        }
        return new MorphRandomisationCorpus(formatVersion, pools) { MaterialProfiles = profiles };
    }

    private static void WriteScalars(BinaryWriter writer, IReadOnlyDictionary<string, float> values)
    {
        var stable = values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
        writer.Write(stable.Length);
        foreach (var value in stable)
        {
            writer.Write(value.Key);
            writer.Write(value.Value);
        }
    }

    private static void WriteVectors(BinaryWriter writer, IReadOnlyDictionary<string, Vector4> values)
    {
        var stable = values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
        writer.Write(stable.Length);
        foreach (var value in stable)
        {
            writer.Write(value.Key);
            WriteVector(writer, value.Value);
        }
    }

    private static Dictionary<string, float> ReadScalars(BinaryReader reader, string donor)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var _ in Enumerable.Range(0, ReadCount(reader, "material scalar")))
        {
            var name = reader.ReadString();
            var value = reader.ReadSingle();
            if (!float.IsFinite(value) || !result.TryAdd(name, value))
            {
                throw new InvalidDataException($"Donor '{donor}' contains invalid material scalar data.");
            }
        }
        return result;
    }

    private static Dictionary<string, Vector4> ReadVectors(BinaryReader reader, string donor)
    {
        var result = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        foreach (var _ in Enumerable.Range(0, ReadCount(reader, "material vector")))
        {
            var name = reader.ReadString();
            var value = ReadVector(reader);
            if (!IsFinite(value) || !result.TryAdd(name, value))
            {
                throw new InvalidDataException($"Donor '{donor}' contains invalid material vector data.");
            }
        }
        return result;
    }

    private static void WriteVector(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W);
    }

    private static Vector4 ReadVector(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static int ReadCount(BinaryReader reader, string label)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 1_000_000)
        {
            throw new InvalidDataException($"Morph-randomisation {label} count {count} is invalid.");
        }
        return count;
    }
}
