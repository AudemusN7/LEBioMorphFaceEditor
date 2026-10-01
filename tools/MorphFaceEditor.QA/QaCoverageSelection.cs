using System.Security.Cryptography;
using System.Text;

namespace MorphFaceEditor.QA;

internal static class QaCoverageSelection
{
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
