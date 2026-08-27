namespace MorphFaceEditor.ViewModels;

public interface IContinuousEditViewModel
{
    float Value { get; set; }
    float Step { get; }
    void BeginEdit();
    void EndEdit();
}
