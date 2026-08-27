using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Initialises the pinned Legendary Explorer Core runtime once per process.</summary>
public static class LegendaryExplorerCoreRuntime
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static event Action<string>? PackageSaveFailed;

    public static void Initialize(TaskScheduler? scheduler = null)
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            LegendaryExplorerCoreLib.InitLib(
                scheduler ?? TaskScheduler.Default,
                message => PackageSaveFailed?.Invoke(message),
                objectDBsToLoad: [MEGame.LE1, MEGame.LE2, MEGame.LE3],
                usePropertyDBLazyLoad: true);
            LE1Directory.ReloadDefaultGamePath();
            LE2Directory.ReloadDefaultGamePath();
            LE3Directory.ReloadDefaultGamePath();
            _initialized = true;
        }
    }

    public static string? DefaultLe1Root
    {
        get
        {
            Initialize();
            return LE1Directory.DefaultGamePath;
        }
    }

    public static string? DefaultLe1CookedPath
    {
        get
        {
            Initialize();
            return LE1Directory.CookedPCPath;
        }
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
