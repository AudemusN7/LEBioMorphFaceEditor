using MorphFaceEditor.Core.Domain;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Compression;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Initialises the pinned Legendary Explorer Core runtime once per process.</summary>
public static class LegendaryExplorerCoreRuntime
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static event Action<string>? PackageSaveFailed;

    public static void Initialize(TaskScheduler? scheduler = null, string? legendaryEditionDirectory = null)
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(legendaryEditionDirectory))
                ConfigureDirectory(legendaryEditionDirectory);
            else
                ReloadGamePaths();
            // LEC also locates the game's decompression DLL during initialization.
            // Apply the saved directory before that one-time lookup.
            LegendaryExplorerCoreLib.InitLib(
                scheduler ?? TaskScheduler.Default,
                message => PackageSaveFailed?.Invoke(message),
                objectDBsToLoad: [MEGame.LE1, MEGame.LE2, MEGame.LE3],
                usePropertyDBLazyLoad: true);
            _initialized = true;
        }
    }

    public static string? LegendaryEditionDirectory
    {
        get
        {
            Initialize();
            return LegendaryExplorerCoreLibSettings.Instance.LEDirectory;
        }
    }

    public static bool IsValidLegendaryEditionDirectory(string path) => LEDirectory.IsValidGameDir(path);

    public static bool SetLegendaryEditionDirectory(string? directory)
    {
        lock (Sync)
        {
            Initialize();
            ConfigureDirectory(directory);
            MELoadedFiles.InvalidateCaches();
            // Retry if startup could not find Oodle; an already loaded DLL is reused.
            return OodleHelper.EnsureOodleDll();
        }
    }

    private static void ConfigureDirectory(string? directory)
    {
        LegendaryExplorerCoreLibSettings.Instance.LEDirectory = directory;
        if (string.IsNullOrWhiteSpace(directory)) LEDirectory.LookupDefaultPath();
        // LEC caches these separately from its shared installation root.
        LE1Directory.DefaultGamePath = null;
        LE2Directory.DefaultGamePath = null;
        LE3Directory.DefaultGamePath = null;
        ReloadGamePaths();
    }

    private static void ReloadGamePaths()
    {
        LE1Directory.ReloadDefaultGamePath();
        LE2Directory.ReloadDefaultGamePath();
        LE3Directory.ReloadDefaultGamePath();
    }

    public static string? DefaultLe2CookedPath
    {
        get
        {
            Initialize();
            return LE2Directory.CookedPCPath;
        }
    }

    public static string? DefaultLe3CookedPath
    {
        get
        {
            Initialize();
            return LE3Directory.CookedPCPath;
        }
    }

    internal static string? GetCookedPath(MEGame game)
    {
        Initialize();
        return game switch
        {
            MEGame.LE1 => LE1Directory.CookedPCPath,
            MEGame.LE2 => LE2Directory.CookedPCPath,
            MEGame.LE3 => LE3Directory.CookedPCPath,
            _ => null
        };
    }

    public static string? GetCookedPath(MorphFaceGame game)
    {
        Initialize();
        return game switch
        {
            MorphFaceGame.LE1 => LE1Directory.CookedPCPath,
            MorphFaceGame.LE2 => LE2Directory.CookedPCPath,
            MorphFaceGame.LE3 => LE3Directory.CookedPCPath,
            _ => null
        };
    }
}
