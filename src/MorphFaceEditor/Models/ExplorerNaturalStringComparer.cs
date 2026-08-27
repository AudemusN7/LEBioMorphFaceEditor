using System.Runtime.InteropServices;

namespace MorphFaceEditor.Models;

/// <summary>Uses the same logical string comparison as Windows Explorer.</summary>
public sealed class ExplorerNaturalStringComparer : IComparer<string>
{
    public static ExplorerNaturalStringComparer Instance { get; } = new();

    private ExplorerNaturalStringComparer()
    {
    }

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        return StrCmpLogicalW(left, right);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string left, string right);
}
