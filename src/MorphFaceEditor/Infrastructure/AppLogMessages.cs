using System.Globalization;

namespace MorphFaceEditor.Infrastructure;

internal static class AppLogMessages
{
    public static string Randomisation(bool cursed, string identity, string game, int morphStrength,
        int materialStrength, int? morphSeed, int? materialSeed)
    {
        static string Seed(int? seed) => seed?.ToString(CultureInfo.InvariantCulture) ?? "-";
        // Cursed uses one shared seed. Normal rolls can have separate morph/material seeds;
        // preserve both, including the empty first slot for a material-only roll.
        var seeds = cursed || materialSeed is null ? Seed(morphSeed) : $"{Seed(morphSeed)}/{Seed(materialSeed)}";
        return $"RAND:{(cursed ? "CUR" : "NOR")} {identity} {game} STR{morphStrength}/{materialStrength} SEED{seeds}";
    }
}
