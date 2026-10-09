using System.Security.Cryptography;
using System.Text;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

internal static class QaCoverageSelection
{
    internal static bool MatchesCaseFilter(string id, string group, string filter) =>
        id.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
        group.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
        id.Split('/').Any(segment => segment.EndsWith("-" + filter, StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith(filter + ".", StringComparison.OrdinalIgnoreCase));

    // Retained in GlobalMorphs, but unused and malformed in the shipped corpus.
    // Keep this exact exclusion in the harness; geometry validation stays strict.
    internal static IReadOnlySet<string> KnownUnusedCorpusFacePaths { get; } =
        new HashSet<string>(["HMF.Asari"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> NoConversionExclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> Le1HmmConversionExclusions =
        new HashSet<string>(["HMM.hench_humanmale"], StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlySet<string> ConversionSourceExclusions(MorphFaceGame sourceGame, string family) =>
        sourceGame == MorphFaceGame.LE1 && family.Equals("HMM", StringComparison.OrdinalIgnoreCase)
            ? Le1HmmConversionExclusions
            : NoConversionExclusions;

    internal static IReadOnlyList<T> ExcludePaths<T>(
        IEnumerable<T> candidates,
        Func<T, string> getPath,
        IReadOnlySet<string> excludedPaths) =>
        candidates.Where(candidate => !KnownUnusedCorpusFacePaths.Contains(getPath(candidate)) &&
            !excludedPaths.Contains(getPath(candidate))).ToArray();

    // Stable per-case streams mean filtering or reordering the matrix does not
    // change a recorded case's donor or operation seeds.
    internal static Random Stream(int seed, string caseId, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}/{caseId}/{purpose}"));
        return new Random(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }

    internal static T Pick<T>(IReadOnlyList<T> candidates, Random random) => candidates.Count > 0
        ? candidates[random.Next(candidates.Count)]
        : throw new InvalidDataException("No donor candidates match this archetype.");
}
