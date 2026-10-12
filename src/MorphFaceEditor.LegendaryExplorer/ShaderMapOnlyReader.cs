using System.IO;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Collections;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Reads one compiled material map while skipping unused embedded shader bytecode.</summary>
internal static class ShaderMapOnlyReader
{
    internal static MaterialShaderMap? Read(ExportEntry cacheExport, StaticParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(cacheExport);
        ArgumentNullException.ThrowIfNull(parameters);

        var game = cacheExport.Game;
        if (game is not (MEGame.ME1 or MEGame.ME2 or MEGame.ME3 or MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
            throw new NotSupportedException($"Shader-cache map reading is not supported for {game}.");

        var dataOffset = cacheExport.DataOffset + cacheExport.propsEnd();
        using var stream = cacheExport.GetReadOnlyBinaryStream();
        var sc = new SerializingContainer(stream, cacheExport.FileRef, true, dataOffset);
        return Read(sc, game, dataOffset, parameters);
    }

    internal static MaterialShaderMap? ReadReferenceCache(string path, MEGame game, StaticParameterSet parameters)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 100, FileOptions.SequentialScan);
        using var package = MEPackageHandler.OpenMEPackageFromStream(stream, path, quickLoad: true);
        RestoreNames(stream, package);

        // The reference cache is a package export. Its data offset is stored
        // in the export table; reading the quick-loaded header avoids loading
        // its very large shader-cache export into memory.
        stream.Position = package.ExportOffset + 36L;
        var binaryOffset = stream.ReadInt32() + 12L;
        if (binaryOffset < 0 || binaryOffset >= stream.Length)
            throw new InvalidDataException("The reference shader cache has an invalid binary offset.");

        var sc = new SerializingContainer(stream, package, true);
        sc.ms.JumpTo(binaryOffset);
        return Read(sc, game, 0, parameters);
    }

    private static MaterialShaderMap? Read(
        SerializingContainer sc,
        MEGame game,
        long offsetBasis,
        StaticParameterSet parameters)
    {

        byte platform = 0;
        sc.Serialize(ref platform);

        UMultiMap<NameReference, uint> shaderTypeCrcMap = [];
        sc.Serialize(ref shaderTypeCrcMap, sc.Serialize, sc.Serialize);

        if (game is MEGame.ME3 or MEGame.LE1 or MEGame.LE2 or MEGame.LE3)
            SkipNameMap(sc);

        if (game == MEGame.ME1)
        {
            UMultiMap<NameReference, uint> vertexFactoryTypeCrcMap = [];
            sc.Serialize(ref vertexFactoryTypeCrcMap, sc.Serialize, sc.Serialize);
        }

        var shaderCount = ReadCount(sc, "embedded shaders");
        for (var i = 0; i < shaderCount; i++)
        {
            // ShaderSerializer stores a NameReference and GUID before the
            // shader's absolute end offset. Jump over bytecode and parameter data.
            sc.ms.Skip(24);
            var endOffset = sc.ms.ReadInt32();
            var endPosition = endOffset - offsetBasis;
            if (endPosition <= sc.ms.Position || endPosition > sc.ms.BaseStream.Length)
                throw new InvalidDataException("The shader cache contains an invalid embedded shader boundary.");
            sc.ms.JumpTo(endPosition);
        }

        if (game != MEGame.ME1)
        {
            UMultiMap<NameReference, uint> vertexFactoryTypeCrcMap = [];
            sc.Serialize(ref vertexFactoryTypeCrcMap, sc.Serialize, sc.Serialize);
        }

        var materialMapCount = ReadCount(sc, "material shader maps");
        for (var i = 0; i < materialMapCount; i++)
        {
            StaticParameterSet key = null!;
            sc.Serialize(ref key);
            if (key == parameters)
            {
                MaterialShaderMap map = null!;
                sc.Serialize(ref map);
                return map;
            }

            // The map record's trailing offset lets us skip its expressions
            // without constructing map objects that this caller never uses.
            if (game >= MEGame.ME3)
                sc.ms.Skip(8); // Unreal and licensee versions.
            var endOffset = sc.ms.ReadInt32();
            var endPosition = endOffset - offsetBasis;
            if (endPosition <= sc.ms.Position || endPosition > sc.ms.BaseStream.Length)
                throw new InvalidDataException("The shader cache contains an invalid material-map boundary.");
            sc.ms.JumpTo(endPosition);
        }

        return null;
    }

    private static void RestoreNames(FileStream stream, IMEPackage package)
    {
        stream.Position = package.NameOffset;
        var names = new List<string>(package.NameCount);
        for (var i = 0; i < package.NameCount; i++)
        {
            names.Add(stream.ReadUnrealString());
            if (package.Game == MEGame.ME1 && package.Platform != MEPackage.GamePlatform.PS3)
                stream.Skip(8);
            else if (package.Game == MEGame.ME2 && package.Platform != MEPackage.GamePlatform.PS3)
                stream.Skip(4);
        }
        package.restoreNames(names);
    }

    private static int ReadCount(SerializingContainer sc, string description)
    {
        var count = sc.ms.ReadInt32();
        if (count < 0 || count > 5_000_000)
            throw new InvalidDataException($"The shader cache has an invalid {description} count.");
        return count;
    }

    private static void SkipNameMap(SerializingContainer sc)
    {
        var count = ReadCount(sc, "name map");
        var byteCount = (long)count * 12;
        var nextPosition = sc.ms.Position + byteCount;
        if (nextPosition > sc.ms.BaseStream.Length)
            throw new InvalidDataException("The shader cache name map extends beyond its binary data.");
        sc.ms.JumpTo(nextPosition);
    }
}
