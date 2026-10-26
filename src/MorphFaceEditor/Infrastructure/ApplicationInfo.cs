using System.Globalization;
using System.Reflection;

namespace MorphFaceEditor.Infrastructure;

/// <summary>Release identity comes from the built application, including its build date.</summary>
internal static class ApplicationInfo
{
    internal static Version AssemblyVersion { get; } = typeof(ApplicationInfo).Assembly.GetName().Version!;
    public static string Version => AssemblyVersion.ToString(4);

    public static string BuildDate { get; } = ReadBuildDate();

    private static string ReadBuildDate()
    {
        var value = typeof(ApplicationInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildDateUtc")?.Value;
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + " (UTC)"
            : "Unknown";
    }
}
