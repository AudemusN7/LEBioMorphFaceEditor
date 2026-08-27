using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

internal static class ExportSelector
{
    public static ExportEntry Find(IMEPackage package, string selector, string expectedClass)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedClass);

        if (int.TryParse(selector, out var uIndex))
        {
            var indexed = package.Exports.FirstOrDefault(export => export.UIndex == uIndex);
            return Validate(indexed, selector, expectedClass);
        }

        var exactPath = package.Exports
            .Where(export => string.Equals(export.InstancedFullPath, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactPath.Length == 1)
        {
            return Validate(exactPath[0], selector, expectedClass);
        }

        var byName = package.Exports
            .Where(export => string.Equals(export.ObjectName.Instanced, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (byName.Length == 1)
        {
            return Validate(byName[0], selector, expectedClass);
        }

        if (exactPath.Length + byName.Length == 0)
        {
            throw new KeyNotFoundException($"Export '{selector}' was not found in {package.FilePath}.");
        }

        throw new InvalidDataException($"Export selector '{selector}' is ambiguous in {package.FilePath}; use the instanced path or UIndex.");
    }

    private static ExportEntry Validate(ExportEntry? export, string selector, string expectedClass)
    {
        if (export is null)
        {
            throw new KeyNotFoundException($"Export '{selector}' was not found.");
        }

        if (!string.Equals(export.ClassName, expectedClass, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Export '{export.InstancedFullPath}' is {export.ClassName}, not expected class {expectedClass}.");
        }

        if (export.IsDefaultObject)
        {
            throw new InvalidDataException($"Export '{export.InstancedFullPath}' is a class default object, not an asset instance.");
        }

        return export;
    }
}
