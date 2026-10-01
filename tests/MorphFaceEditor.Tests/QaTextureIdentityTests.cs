using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.QA;

namespace MorphFaceEditor.Tests;

internal static class QaTextureIdentityTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [new("QA Custom texture readback compares full native identities", NativeTextureIdentity)];

    private static void NativeTextureIdentity()
    {
        var donor = new AssetIdentity("BIOG_HMM_HED_PROMorph.pcc", "Eye.EYE_Iris_Norm", 1, "Texture2D");
        var saved = new AssetIdentity("CustomHead.pcc", "BIOG_HMM_HED_PROMorph.Eye.EYE_Iris_Norm", 42, "Texture2D");
        TestAssert.True(QaTextureIdentity.SameTexture(donor, saved), "Seek-free and qualified paths falsely differ.");
        TestAssert.True(!QaTextureIdentity.SameTexture(donor, saved with
            { InstancedPath = "OtherPackage.Eye.EYE_Iris_Norm" }), "Same leaf in another package was accepted.");
        TestAssert.True(!QaTextureIdentity.SameTexture(donor, saved with
            { InstancedPath = "BIOG_HMM_HED_PROMorph.Other.EYE_Iris_Norm" }), "Same leaf in another group was accepted.");
        TestAssert.True(!QaTextureIdentity.SameTexture(donor, saved with
            { ClassName = "TextureCube" }), "A different texture class was accepted.");
    }
}
