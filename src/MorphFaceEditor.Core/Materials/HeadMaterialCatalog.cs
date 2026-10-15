using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Materials;

public enum HumanFaceMaterialKind { None, MaleNpc, MalePlayer, FemaleNpc, FemalePlayer }

public sealed record HeadMaterialDefinition(
    HeadMaterialFamily Family,
    HumanMaterialPreviewModel PreviewModel = HumanMaterialPreviewModel.Default,
    HumanFaceMaterialKind HumanFace = HumanFaceMaterialKind.None,
    bool LayeredHairHighlights = false,
    string? ProfileSuffix = null)
{
    public bool IsSharedHeadRoot => Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes or
        HeadMaterialFamily.Scalp or HeadMaterialFamily.Teeth or HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair;
    public bool IsAttachmentPreview => PreviewModel is >= HumanMaterialPreviewModel.Hat and <= HumanMaterialPreviewModel.VisorLens;
}

/// <summary>Known terminal Material names, scoped to their game. Parent paths and MIC names are not recognition evidence.</summary>
public static class HeadMaterialCatalog
{
    private static readonly IReadOnlyDictionary<MorphFaceGame, Dictionary<string, HeadMaterialDefinition>> Masters = CreateMasters();

    public static HeadMaterialDefinition? Find(MorphFaceGame game, string? exactMasterName) =>
        exactMasterName is not null && Masters.TryGetValue(game, out var names) && names.TryGetValue(exactMasterName, out var definition)
            ? definition : null;

    public static bool IsAssignableHeadFamily(HeadMaterialFamily family) =>
        family is not (HeadMaterialFamily.Unknown or HeadMaterialFamily.Accessory);

    private static IReadOnlyDictionary<MorphFaceGame, Dictionary<string, HeadMaterialDefinition>> CreateMasters()
    {
        MorphFaceGame[] all = [MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3];
        MorphFaceGame[] le12 = [MorphFaceGame.LE1, MorphFaceGame.LE2];
        MorphFaceGame[] le23 = [MorphFaceGame.LE2, MorphFaceGame.LE3];
        MorphFaceGame[] le3 = [MorphFaceGame.LE3];
        var result = all.ToDictionary(game => game, _ => new Dictionary<string, HeadMaterialDefinition>(StringComparer.OrdinalIgnoreCase));

        Add(all, "HMM_HED_PRONPC_MASTER_FACE_MAT", new(HeadMaterialFamily.Skin, HumanFace: HumanFaceMaterialKind.MaleNpc, ProfileSuffix: "human-male"));
        Add(all, "HMM_HED_PROCustom_MASTER_FACE_MAT", new(HeadMaterialFamily.Skin, HumanFace: HumanFaceMaterialKind.MalePlayer, ProfileSuffix: "human-male"));
        Add(all, "HMF_HED_PRO_MASTER_FACE_MAT", new(HeadMaterialFamily.Skin, HumanFace: HumanFaceMaterialKind.FemaleNpc, ProfileSuffix: "human-female"));
        Add(all, "HMF_HED_PROCustom_MASTER_FACE_MAT", new(HeadMaterialFamily.Skin, HumanFace: HumanFaceMaterialKind.FemalePlayer, ProfileSuffix: "human-female"));
        Add(all, "ASA_HED_MASTER_FACE_MAT_1a", new(HeadMaterialFamily.AsariSkin, ProfileSuffix: "asari"));
        Add(all, "SAL_HED_PRO_MASTER_MAT", new(HeadMaterialFamily.SalarianSkin, ProfileSuffix: "salarian"));
        Add(all, "SAL_HED_EYE_MASTER_MAT", new(HeadMaterialFamily.SalarianEyes, ProfileSuffix: "salarian"));
        Add(all, "TUR_HED_PRO_MASTER_MAT", new(HeadMaterialFamily.TurianSkin, ProfileSuffix: "turian"));
        Add(all, "TUR_HED_EYE_MASTER_MAT", new(HeadMaterialFamily.TurianEyes, ProfileSuffix: "turian"));
        Add(all, "BAT_HED_PRO_MASTER_MAT", new(HeadMaterialFamily.BatarianSkin, ProfileSuffix: "batarian"));
        Add(all, "KRO_HED_PRO_MASTER_MAT", new(HeadMaterialFamily.KroganSkin, ProfileSuffix: "krogan"));
        Add(all, "KRO_HED_EYE_MASTER_MAT", new(HeadMaterialFamily.KroganEyes, ProfileSuffix: "krogan"));
        Add(le23, "ALN_HED_PRO_MASTER_MAT", new(HeadMaterialFamily.VorchaSkin, ProfileSuffix: "vorcha"));
        Add([MorphFaceGame.LE2], "ALN_EYE_MASTER_MAT", new(HeadMaterialFamily.VorchaEyes, ProfileSuffix: "vorcha"));
        Add(all, "HMN_HED_LASH_Unlit_MASTER_MAT", new(HeadMaterialFamily.Lashes));
        Add(all, "HMM_HED_PRO_MASTER_SCALP_MAT", new(HeadMaterialFamily.Scalp));
        Add(le12, "HMM_HIR_PROShort01_MAT_1a", new(HeadMaterialFamily.MaskedHair));
        Add(le12, "HMN_HED_PRO_MASTER_HAIR_MAT", new(HeadMaterialFamily.Hair));
        Add(le12, "HMN_HED_PRO_ME3_MASTER_Hair_MAT", new(HeadMaterialFamily.Hair, LayeredHairHighlights: true));
        Add(le3, "HMN_HED_PRO_MASTER_HAIR_MAT", new(HeadMaterialFamily.Hair, LayeredHairHighlights: true));
        Add(le3, "HMN_HED_PRO_MASTER_ADDN_HAIR_MAT", new(HeadMaterialFamily.Hair, LayeredHairHighlights: true));
        Add(le3, "HMN_HED_PRO_MASTER_HAIR_MAT_CC", new(HeadMaterialFamily.Hair, HumanMaterialPreviewModel.CcHair));
        Add(le3, "HMM_HED_PRO_MASTER_SCALP_MAT_CC", new(HeadMaterialFamily.Scalp, HumanMaterialPreviewModel.CcScalp));
        Add(le3, "BIOG_HMM_HED_Kaiden_MASTER", new(HeadMaterialFamily.Skin, HumanMaterialPreviewModel.Kaiden, HumanFaceMaterialKind.MaleNpc, ProfileSuffix: "human-male"));
        Add(le3, "Human_Hair_MASTER", new(HeadMaterialFamily.Hair, HumanMaterialPreviewModel.HumanHair));
        Add(all, "HMM_EYE_MASTER_OVRD_MAT", new(HeadMaterialFamily.Eyes));
        Add([MorphFaceGame.LE1], "HMF_EYE_MASTER_OVRD_MAT", new(HeadMaterialFamily.Eyes));
        Add(le23, "HMM_EYE_MASTER_MAT", new(HeadMaterialFamily.Eyes));
        Add(all, "HMN_HAT_MASTER_MAT", new(HeadMaterialFamily.Accessory, HumanMaterialPreviewModel.Hat));
        Add(le12, "HMF_VSR_MASTER_MAT", new(HeadMaterialFamily.Accessory, HumanMaterialPreviewModel.Visor));
        Add(all, "HMM_HAT_Cap_Decal_MASTER_MAT", new(HeadMaterialFamily.Accessory, HumanMaterialPreviewModel.CapDecal));
        Add([MorphFaceGame.LE2], "HMM_HAT_Cap_MAT_2a", new(HeadMaterialFamily.Accessory, HumanMaterialPreviewModel.CapLe2));
        Add(le3, "Andersonb_MASTER_MAT", new(HeadMaterialFamily.Accessory, HumanMaterialPreviewModel.AndersonHat));
        Add(le3, "HMM_HAT_Mtr_MASTER", new(HeadMaterialFamily.Accessory, HumanMaterialPreviewModel.MotoHat));
        Add(le12, "HMM_HAT_VSR_MAT_Visor", new(HeadMaterialFamily.Accessory, HumanMaterialPreviewModel.VisorLens));
        return result;

        void Add(IEnumerable<MorphFaceGame> games, string name, HeadMaterialDefinition definition)
        {
            foreach (var game in games) result[game].Add(name, definition);
        }
    }
}
