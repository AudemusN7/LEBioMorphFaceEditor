using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.QA;

/// <summary>Small in-memory regressions for the QA trash-import exemption.</summary>
internal static class QaTrashPolicyCheck
{
    /// <summary>Called by the parent runner self-check; returns the checks that passed.</summary>
    internal static IReadOnlyList<string> RunSelfChecks()
    {
        UnreferencedSubtreeIsIgnored();
        LookalikePrefixIsNotIgnored();
        ExportHeaderReferenceIntoTrashIsRejected();
        ExportPropertyReferenceIntoTrashIsRejected();
        ExportBinaryReferencesIntoTrashAreRejected();
        return [
            "unreferenced trash imports and exports, including internal trash references, are eligible for the audit exemption",
            "trash-prefix lookalike remains outside the exemption",
            "retained export header references to trash imports and exports fail",
            "retained export property references to trash imports fail",
            "retained export binary references to trash imports and exports fail"
        ];
    }

    private static void UnreferencedSubtreeIsIgnored()
    {
        using var package = Empty("TrashUnreferenced");
        AddTrashSubtree(package);
        var result = QaTrashPolicy.ClassifyUnreferencedTrashImports(package);
        QaContext.Require(result.IgnoredImportIndices.Count == 3 && result.Evidence.Count == 3,
            "Unreferenced trash imports were not all classified as ignored audit evidence.");
    }

    private static void LookalikePrefixIsNotIgnored()
    {
        using var package = Empty("TrashLookalike");
        package.CreatePackageImport("ME3ExplorerTrashPackageBackup");
        var result = QaTrashPolicy.ClassifyUnreferencedTrashImports(package);
        QaContext.Require(result.IgnoredImportIndices.Count == 0,
            "A package whose name only shares the trash-root prefix was exempted.");
    }

    private static void ExportHeaderReferenceIntoTrashIsRejected()
    {
        using var package = Empty("TrashHeaderReference");
        var (_, trashLeaf, trashExport) = AddTrashSubtree(package);
        var retained = package.CreateExport("Retained", "Object", indexed: false);
        var originalClass = retained.Class;
        retained.Class = trashLeaf;
        ExpectRejected(package, "header.class");
        using var exportTargetPackage = Empty("TrashHeaderExportReference");
        var (_, _, exportTarget) = AddTrashSubtree(exportTargetPackage);
        var exportTargetRetained = exportTargetPackage.CreateExport("Retained", "Object", indexed: false);
        exportTargetRetained.Class = exportTarget;
        ExpectRejected(exportTargetPackage, "header.class");

        retained.Class = originalClass;
        retained.Archetype = trashExport;
        ExpectRejected(package, "header.archetype");
    }

    private static void ExportPropertyReferenceIntoTrashIsRejected()
    {
        using var package = Empty("TrashPropertyReference");
        var (_, trashLeaf, _) = AddTrashSubtree(package);
        var retained = package.CreateExport("Retained", "Object", indexed: false);
        retained.WriteProperty(new ObjectProperty(trashLeaf, "TrashReference"));
        ExpectRejected(package, "property");
    }

    private static void ExportBinaryReferencesIntoTrashAreRejected()
    {
        using var importTargetPackage = Empty("TrashBinaryImportReference");
        var (_, trashImport, _) = AddTrashSubtree(importTargetPackage);
        var importTargetRetained = importTargetPackage.CreateExport("Retained", "SkeletalMesh", indexed: false);
        var binary = SkeletalMesh.Create();
        binary.Materials = [trashImport.UIndex];
        importTargetRetained.WriteBinary(binary);
        ExpectRejected(importTargetPackage, "binary");

        using var exportTargetPackage = Empty("TrashBinaryExportReference");
        var (_, _, trashExport) = AddTrashSubtree(exportTargetPackage);
        var exportTargetRetained = exportTargetPackage.CreateExport("Retained", "SkeletalMesh", indexed: false);
        var exportTargetBinary = SkeletalMesh.Create();
        exportTargetBinary.Materials = [trashExport.UIndex];
        exportTargetRetained.WriteBinary(exportTargetBinary);
        ExpectRejected(exportTargetPackage, "binary");
    }

    private static (IEntry Root, IEntry Leaf, ExportEntry Export) AddTrashSubtree(IMEPackage package)
    {
        var root = package.CreatePackageImport("ME3ExplorerTrashPackage");
        var child = package.CreateImport("Package", "Trash", root);
        var leaf = package.CreateImport("Texture2D", "Marker", child);
        var trashExport = package.CreateExport("TrashObject", "Object", child, indexed: false);
        trashExport.WriteProperty(new ObjectProperty(leaf, "InternalTrashReference"));
        return (root, leaf, trashExport);
    }

    private static void ExpectRejected(IMEPackage package, string expectedSite)
    {
        try
        {
            QaTrashPolicy.ClassifyUnreferencedTrashImports(package);
        }
        catch (InvalidDataException error) when (error.Message.Contains(expectedSite, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidDataException($"Trash-policy synthetic case did not reject its {expectedSite} edge.");
    }

    private static IMEPackage Empty(string name) =>
        MEPackageHandler.CreateMemoryEmptyPackage(name + ".pcc", MEGame.LE3);
}
