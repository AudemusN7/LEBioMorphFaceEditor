using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed class HumanMaleMorphTargetCatalog
{
    private readonly object _sync = new();
    private IReadOnlyList<MorphTargetAsset>? _targets;

    public IReadOnlyList<MorphTargetAsset> Load()
    {
        lock (_sync)
        {
            if (_targets is not null)
            {
                return _targets;
            }
            var cookedPath = LegendaryExplorerCoreRuntime.DefaultLe1CookedPath
                ?? throw new DirectoryNotFoundException("The LE1 CookedPCConsole directory was not discovered.");
            _targets = MorphTargetPackageReader.LoadAll(
                Path.Combine(cookedPath, "BIOG_HMM_HED_PROMorph.pcc"));
            return _targets;
        }
    }
}
