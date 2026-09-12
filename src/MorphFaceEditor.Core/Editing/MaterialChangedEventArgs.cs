namespace MorphFaceEditor.Core.Editing;

public enum MaterialChangeKind
{
    Full,
    Surface,
    Scalar,
    Vector,
    Texture
}

public sealed class MaterialChangedEventArgs(MaterialChangeKind kind, string? parameterName = null) : EventArgs
{
    public MaterialChangeKind Kind { get; } = kind;
    public string? ParameterName { get; } = parameterName;
}
