using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record PackageEntrySummary(
    int UIndex,
    string InstancedPath,
    string ObjectName,
    string ClassName,
    bool IsDefaultObject);

public sealed record PackageInventory(
    string PackagePath,
    string Game,
    PackageFingerprint Fingerprint,
    IReadOnlyList<PackageEntrySummary> Entries);

public sealed record AssetDiscoveryResult(
    string RootPath,
    int PackagesScanned,
    int PackagesFailed,
    IReadOnlyList<PackageInventory> MatchingPackages,
    IReadOnlyList<string> Failures);
