cbuffer SceneConstants : register(b0)
{
    row_major float4x4 WorldViewProjection;
    row_major float4x4 World;
    row_major float4x4 View;
    float4 CameraPosition;
    float4 KeyLightDirection;
    float4 KeyLightColor;
    float4 FillLightDirection;
    float4 FillLightColor;
    float4 RimLightDirection;
    float4 RimLightColor;
    float4 AmbientColor;
    float4 LightingParameters;
    float4 SkyColor;
    float4 GroundColor;
    float4 HorizonColor;
    float4 EnvironmentParameters;
};

cbuffer MaterialConstants : register(b1)
{
    float4 BaseColor;
    float4 SecondaryColor;
    float4 TertiaryColor;
    float4 QuaternaryColor;
    float4 SpecularColor;
    float4 ScatterColor;
    float4 FreckleRedColor;
    float4 FreckleGreenColor;
    float4 FreckleBlueColor;
    float4 BlondeColor;
    float4 TransmissionColor;
    float4 SurfaceParameters;
    float4 TextureFlags0;
    float4 TextureFlags1;
    float4 TextureFlags2;
    float4 CustomSkinParameters;
    float4 CustomScarColor;
    float4 GeneralParameters;
    float4 SkinParameters0;
    float4 SkinParameters1;
    float4 SkinParameters2;
    float4 SkinParameters3;
    float4 ScalpParameters0;
    float4 ScalpParameters1;
    float4 ScalpParameters2;
    float4 EyeParameters;
    float4 EyeParameters1;
    float4 EyeParameters2;
    float4 EyeEmissiveColor;
    float4 BlushColor;
    float4 HumanParameters;
};

Texture2D DiffuseTexture : register(t0);
Texture2D NormalTexture : register(t1);
Texture2D MaskTexture : register(t2);
Texture2D DetailTexture : register(t3);
Texture2D AuxiliaryTexture1 : register(t4);
Texture2D AuxiliaryTexture2 : register(t5);
Texture2D AuxiliaryTexture3 : register(t6);
Texture2D AuxiliaryTexture4 : register(t7);
TextureCube FixedCubeTexture : register(t8);
TextureCube SecondaryFixedCubeTexture : register(t9);
Texture2D CustomBrowTexture : register(t15);
Texture2D CustomScarTexture : register(t16);
SamplerState MaterialSampler : register(s0);

cbuffer ShadowConstants : register(b5)
{
    row_major float4x4 LightViewProjection;
    // Enabled, texel size, comparison-depth bias, world-space normal bias.
    float4 ShadowParameters;
};
Texture2D<float> KeyShadowTexture : register(t17);
SamplerComparisonState ShadowSampler : register(s2);

float KeyShadowVisibility(float3 worldPosition, float3 geometricNormal)
{
    if (ShadowParameters.x < 0.5) return 1;
    float facing = dot(geometricNormal, normalize(KeyLightDirection.xyz));
    float3 offset = geometricNormal * (facing >= 0 ? 1 : -1)
        * ShadowParameters.w * (1 - saturate(abs(facing)));
    float4 projected = mul(float4(worldPosition + offset, 1), LightViewProjection);
    float3 lightPosition = projected.xyz / projected.w;
    float2 uv = lightPosition.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0) || any(uv > 1) || lightPosition.z < 0 || lightPosition.z > 1)
        return 1;
    float visibility = 0;
    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
            visibility += KeyShadowTexture.SampleCmpLevelZero(ShadowSampler,
                uv + float2(x, y) * ShadowParameters.y,
                lightPosition.z - ShadowParameters.z);
    }
    return visibility / 9;
}

cbuffer PostProcessConstants : register(b4)
{
    row_major float4x4 InverseProjection;
    float4 PixelSizeAndSamples;
    float4 DepthParameters;
    float4 BloomParameters;
    float4 DisplayParameters;
    float4 DisplayBackground;
    float4 BloomTint;
    // Normalized UV direction for the separable bloom blur.
    float4 BloomPassParameters;
};

Texture2D SceneColorTexture : register(t10);
Texture2DMS<float> SceneDepthMsaaTexture : register(t11);
Texture2D SceneDepthSingleTexture : register(t12);
Texture2DMS<float4> SceneNormalMsaaTexture : register(t13);
Texture2D SceneNormalSingleTexture : register(t14);
Texture2D SceneAmbientDiffuseTexture : register(t18);
Texture2D PostBloomTexture : register(t19);
Texture2D GradingLutTexture : register(t20);
SamplerState PostSampler : register(s1);

cbuffer SkinningConstants : register(b2)
{
    row_major float4x4 BoneMatrices[128];
};

cbuffer MeshConstants : register(b3)
{
    float4 SkinningParameters;
};

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 TexCoord : TEXCOORD0;
    uint4 BoneIndices : BLENDINDICES0;
    float4 BoneWeights : BLENDWEIGHT0;
};

struct VertexOutput
{
    float4 Position : SV_POSITION;
    float3 WorldPosition : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float2 TexCoord : TEXCOORD2;
    float4 Tangent : TEXCOORD3;
};

VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    float4 localPosition = float4(input.Position, 1);
    float3 localNormal = input.Normal;
    float3 localTangent = input.Tangent.xyz;
    if (SkinningParameters.x > 0.5)
    {
        float4 skinnedPosition = 0;
        float3 skinnedNormal = 0;
        float3 skinnedTangent = 0;
        float totalWeight = 0;
        float4 effectiveWeights = input.BoneWeights;
        if (dot(effectiveWeights, float4(1, 1, 1, 1)) <= 0.000001)
        {
            effectiveWeights.x = 1;
        }
        [unroll]
        for (int influence = 0; influence < 4; ++influence)
        {
            float weight = effectiveWeights[influence];
            if (weight > 0)
            {
                uint boneIndex = input.BoneIndices[influence];
                skinnedPosition += mul(localPosition, BoneMatrices[boneIndex]) * weight;
                skinnedNormal += mul(float4(localNormal, 0), BoneMatrices[boneIndex]).xyz * weight;
                skinnedTangent += mul(float4(localTangent, 0), BoneMatrices[boneIndex]).xyz * weight;
                totalWeight += weight;
            }
        }
        if (totalWeight > 0.000001)
        {
            localPosition = skinnedPosition / totalWeight;
            localNormal = normalize(skinnedNormal / totalWeight);
            localTangent = normalize(skinnedTangent / totalWeight);
        }
    }
    output.Position = mul(localPosition, WorldViewProjection);
    output.WorldPosition = mul(localPosition, World).xyz;
    output.Normal = normalize(mul(float4(localNormal, 0), World).xyz);
    output.Tangent = float4(normalize(mul(float4(localTangent, 0), World).xyz), input.Tangent.w);
    output.TexCoord = input.TexCoord;
    return output;
}

float3 DecodeNormal(float4 packedNormal)
{
    float2 xy = packedNormal.xy * 2 - 1;
    return normalize(float3(xy, sqrt(saturate(1 - dot(xy, xy)))));
}

float Luminance(float3 color)
{
    return dot(color, float3(0.299, 0.587, 0.114));
}

bool IsCcScalpPreview()
{
    // Preview model 2 is selected only for the exact LE3 CC scalp master.
    return TextureFlags2.z > 1.5 && TextureFlags2.z < 2.5;
}

struct CcScalpMasks
{
    float3 Mask;
    float3 Overlay;
    float3 Buzzcut;
};

CcScalpMasks EvaluateCcScalpMasks(float4 scalpSpec)
{
    // These terms follow the recovered CC scalp FXC register use:
    //   Mask     = HED_Scalp_Spec.rgb * HED_Scalp_Mask_Scalar
    //   Overlay  = 1 - HED_Scalp_Mask_OverlayKill_Scalar * Mask
    //   Buzzcut  = Mask * HED_Scalp_Buzzcut_Alpha_Scalar
    // HAIR_Mask_Alpha_Scalar occupies the next uniform slot, but is not read
    // by the inspected base or directional-light permutations.
    CcScalpMasks masks;
    masks.Mask = scalpSpec.rgb * ScalpParameters0.x;
    masks.Overlay = 1 - ScalpParameters0.z * masks.Mask;
    masks.Buzzcut = masks.Mask * ScalpParameters0.y;
    return masks;
}

bool IsCcHairPreview()
{
    return TextureFlags2.z > 0.5 && TextureFlags2.z < 1.5;
}

bool IsKaidenPreview()
{
    return TextureFlags2.z > 2.5 && TextureFlags2.z < 3.5;
}

bool IsHumanHairPreview()
{
    return TextureFlags2.z > 3.5 && TextureFlags2.z < 4.5;
}

void CcScalpFibreDirections(float3 normal, float3 tangent, float3 bitangent,
    float3 packedTangent, float2 uv, out float3 firstFibre, out float3 secondFibre)
{
    float shift = TextureFlags1.z > 0.5
        ? AuxiliaryTexture3.Sample(MaterialSampler, uv * 2).g - 0.5 : 0;
    float3 first = packedTangent * 2 - 1 + shift * 0.5;
    float3 second = packedTangent * 2 - 1 + shift * 0.3;
    firstFibre = normalize(tangent * first.x + bitangent * first.y + normal * first.z);
    secondFibre = normalize(tangent * second.x + bitangent * second.y + normal * second.z);
}

float FibreLobe(float3 fibre, float3 halfDirection, float power)
{
    float tangentDot = dot(fibre, halfDirection);
    return pow(max(sqrt(saturate(1 - tangentDot * tangentDot)), 0.0001), power);
}

struct LegacyScalpLobes
{
    float3 First;
    float3 Second;
    float PrimaryPower;
    float SecondaryPower;
    float PrimaryGain;
    float SecondaryGain;
    float DiffuseGain;
    float LobeScale;
};

LegacyScalpLobes LegacyScalpDirections(float3 geometricNormal, float3 tangent,
    float3 bitangent, float2 uv)
{
    // LE1/LE2 reconstruct HED_Tang.Z from RG, then shift all three components
    // with SpecShift2.G sampled at twice the material UV. This is independent
    // of the scalp surface normal used for the ordinary Phong reflection.
    float3 fibre = TextureFlags0.w > 0.5
        ? DecodeNormal(DetailTexture.Sample(MaterialSampler, uv)) : float3(0, 0, 1);
    float shift = TextureFlags1.z > 0.5
        ? AuxiliaryTexture3.Sample(MaterialSampler, uv * 2).g - 0.5 : 0;
    float3 first = normalize(fibre + shift * 0.5);
    float3 second = normalize(fibre + shift * 0.3);
    LegacyScalpLobes lobes;
    lobes.First = normalize(tangent * first.x + bitangent * first.y + geometricNormal * first.z);
    lobes.Second = normalize(tangent * second.x + bitangent * second.y + geometricNormal * second.z);
    float level = SecondaryColor.r;
    lobes.PrimaryPower = level <= 0.09 ? 400 : (level < 0.4 ? 200 : 100);
    lobes.SecondaryPower = level <= 0.09 ? 30 : 15;
    lobes.PrimaryGain = level <= 0.09 ? 2.5 : (level < 0.4 ? 2 : 1.25);
    lobes.SecondaryGain = level <= 0.09 ? 1 : (level < 0.4 ? 1.5 : 1);
    if (HumanParameters.y > 0.5 && level < 0.0125)
    {
        lobes.PrimaryGain = 20;
        lobes.SecondaryGain = 8;
    }
    lobes.DiffuseGain = level <= 0.09 ? 0.5 : 0.25;
    lobes.LobeScale = level <= 0.09 ? 1 : 0.5;
    return lobes;
}

float3 LegacyScalpAlbedo(float3 diffuse, float3 packedMask, float teeth,
    LegacyScalpLobes lobes, float3 halfDirection, out float3 diffuseOnly)
{
    // Recovered LE1/LE2 base and directional passes share this composition.
    // Colour magnitude remains in the raw HairColour term; threshold bands
    // select lobe powers/gains, rather than replacing the diffuse brightness.
    float3 mask = packedMask * ScalpParameters0.x;
    float3 overlay = 1 - ScalpParameters0.z * mask;
    float3 maskedDiffuse = diffuse * mask;
    float3 hair = maskedDiffuse * (
        FibreLobe(lobes.First, halfDirection, lobes.PrimaryPower) * saturate(SecondaryColor.rgb * lobes.PrimaryGain)
        + FibreLobe(lobes.Second, halfDirection, lobes.SecondaryPower) * saturate(SecondaryColor.rgb * lobes.SecondaryGain))
        * lobes.LobeScale;
    float3 rawHair = sqrt(max(maskedDiffuse * maskedDiffuse, 0.0001))
        * lobes.DiffuseGain * SecondaryColor.rgb;
    float3 diffuseHair = rawHair * (HumanParameters.y > 0.5 ? 1.5 : 1);
    hair += diffuseHair;
    float3 maskedHair = packedMask * hair;
    float3 teethColour = HumanParameters.y > 0.5
        ? float3(0.723, 0.311, 0.184) : TertiaryColor.rgb;
    float3 skin = diffuse * BaseColor.rgb * overlay * (1 - teeth);
    float3 composed = skin + diffuse * teeth * teethColour + maskedHair;
    float3 maskedDiffuseHair = packedMask * diffuseHair;
    diffuseOnly = lerp(skin + diffuse * teeth * teethColour + maskedDiffuseHair,
        maskedDiffuseHair, mask * ScalpParameters0.y);
    return lerp(composed, maskedHair, mask * ScalpParameters0.y);
}

float3 LegacyScalpDirectLight(float3 normal, float3 geometricNormal, float3 tangent,
    float3 bitangent, float3 view, float3 light, float4 diffuse, float3 packedMask,
    float teeth, float2 uv)
{
    LegacyScalpLobes lobes = LegacyScalpDirections(geometricNormal, tangent, bitangent, uv);
    float3 halfDirection = normalize(view + light);
    float3 unusedAmbientDiffuse;
    float3 albedo = LegacyScalpAlbedo(diffuse.rgb, packedMask, teeth, lobes, halfDirection, unusedAmbientDiffuse);
    float3 mask = packedMask * ScalpParameters0.x;
    float3 overlay = 1 - ScalpParameters0.z * mask;
    float3 hairColour = abs(SecondaryColor.rgb);
    float3 poweredTint = pow(max(hairColour / max(length(hairColour), 0.0001), 0.0001),
        max(ScalpParameters1.w, 0.1));
    float3 desaturatedTint = lerp(poweredTint,
        dot(poweredTint, float3(0.3, 0.59, 0.11)), ScalpParameters2.x);
    float shift = TextureFlags1.y > 0.5
        ? AuxiliaryTexture2.Sample(MaterialSampler, uv).r : 1;
    // Native direct specular crosses the powers of the two shifted lobes.
    float3 hairSpecular = (
        FibreLobe(lobes.First, halfDirection, lobes.SecondaryPower) * desaturatedTint
        + FibreLobe(lobes.Second, halfDirection, lobes.PrimaryPower) * poweredTint)
        * shift * diffuse.a * mask * ScalpParameters1.z;
    float3 skinSpecular = diffuse.a * (1 - teeth * ScalpParameters1.y)
        * overlay * SpecularColor.rgb;
    float phong = pow(max(saturate(dot(reflect(-view, normal), light)), 0.0001),
        max(GeneralParameters.y, 0.1));
    float normalLight = dot(normal, light);
    float3 wrap = (1 - diffuse.a) * (1 - teeth) * overlay * ScatterColor.rgb * 0.5;
    float3 diffuseResponse = saturate((normalLight + wrap) / max(1 + wrap, 0.0001));
    if (HumanParameters.y > 0.5)
    {
        // LE2 scalp uses Diff.B * TMis * TClr as both target and blend weight,
        // exactly like the face. N.L belongs only to the wrapped-light input.
        // LE1's scalp permutation has no live transmission input.
        float3 transmission = diffuse.b * GeneralParameters.x * TransmissionColor.rgb;
        diffuseResponse = lerp(diffuseResponse, transmission, transmission);
    }
    return albedo * diffuseResponse + max(skinSpecular + hairSpecular, 0) * phong;
}

float3 EvaluateHumanHairStack(float4 stack, float3 tangent, float3 halfDirection)
{
    float tangentDot = dot(tangent, halfDirection);
    float lobe = sqrt(saturate(1 - tangentDot * tangentDot));
    float primary = pow(max(lobe, 0.0001), max(ScalpParameters2.y, 0.1));
    float secondary = pow(max(lobe, 0.0001), max(ScalpParameters2.z, 0.1));
    // Native vectors 0..3 are SelectionColor, Hair_Color, Aniso_Color_1/2.
    // The (1 - vector0) term is editor selection tint, not inverse Hair_Color.
    // SelectionColor is zero in MFE. Stack A belongs to blending, not lighting.
    return stack.r * (primary * max(SecondaryColor.rgb, 0)
        + secondary * max(TertiaryColor.rgb, 0))
        + stack.g * max(BaseColor.rgb * BaseColor.rgb, 0);
}

float3 EvaluateSpecular(
    float family,
    float3 normal,
    float3 lensNormal,
    float3 outerLensNormal,
    float3 tangent,
    float3 bitangent,
    float3 view,
    float3 light,
    float4 diffuseSample,
    float4 maskSample,
    float4 detailSample,
    float faceSelector,
    float3 albedo,
    float2 materialUv)
{
    float3 halfDirection = normalize(light + view);
    bool le3 = EyeParameters2.w > 0.5;
    float normalHalf = saturate(dot(normal, halfDirection));
    // LE1's direct-light permutations apply HED_SPwr to the Phong
    // reflection-vector term, not the broader Blinn half-vector term.
    float reflectedViewLight = saturate(dot(reflect(-view, normal), light));
    float specular = 0;
    float3 specularTint = max(SpecularColor.rgb, 0);

    if (family == 15)
    {
        // ALN_HED_PRO_MASTER_MAT directional-light FXC. Tint.R selects the
        // skin exponent and its inverse selects the muzzle exponent; diffuse
        // alpha supplies the spatial coloured-specular mask.
        float materialPower = 100 * max(
            lerp(GeneralParameters.y, GeneralParameters.x, saturate(maskSample.r)),
            0.001);
        return pow(max(reflectedViewLight, 0.0001), materialPower)
            * saturate(diffuseSample.a)
            * max(SpecularColor.rgb, 0);
    }
    if (family == 16)
    {
        // The ALN eye graph folds EYE_Spec_Power through a x100 expression
        // before the reflection-vector Phong operation.
        float materialPower = max(GeneralParameters.y * 100, 0.1);
        return pow(max(reflectedViewLight, 0.0001), materialPower)
            * max(GeneralParameters.x, 0);
    }
    if (family == 1)
    {
        if (IsKaidenPreview())
        {
            // Kaiden's scalp graph supplies its own Spec map, HED_Spec_Scalar,
            // and HED_Spwr. The directional FXC uses Spec.r as the power
            // selector and Spec.g as the intensity selector; multiplying all
            // RGB channels into the lobe produced an overly broad, bright
            // preview.
            float kaidenPower = max(maskSample.r * GeneralParameters.y, 0.1);
            float kaidenIntensity = max(maskSample.g * GeneralParameters.z, 0);
            return pow(max(reflectedViewLight, 0.0001), kaidenPower)
                * kaidenIntensity;
        }
        float femaleFace = SkinParameters3.w;
        if (femaleFace > 0.5)
        {
            float4 makeupSample = TextureFlags1.z > 0.5
                ? AuxiliaryTexture3.Sample(MaterialSampler, materialUv)
                : 0;
            float lipMask = saturate(
                (1 - makeupSample.b) * makeupSample.a * GeneralParameters.z);
            // LE2's shine controls use independent makeup selectors: green
            // for intensity and alpha for power. Lip colour strength is not
            // part of either compiled specular selector.
            float lipSpecularMask = HumanParameters.y > 0.5
                ? (1 - makeupSample.b) * makeupSample.g : lipMask;
            float lipPowerMask = HumanParameters.y > 0.5
                ? (1 - makeupSample.b) * makeupSample.a : lipMask;
            float additionAlpha = TextureFlags0.w > 0.5 ? detailSample.a : 0;
            float baseCoverage = saturate(diffuseSample.a);
            float additionSuppression = lerp(
                1,
                1 - saturate(additionAlpha),
                saturate(SkinParameters1.z));
            float3 femaleTint = SpecularColor.rgb * baseCoverage * additionSuppression
                + lipSpecularMask * max(SkinParameters1.w, 0);
            // HMF direct-light FXC: base HED_SPwr remains active outside the
            // addition mask. The mask replaces/suppresses that base exponent
            // with the addition exponent; lips then add their own exponent.
            float femalePower = max(
                GeneralParameters.y
                + additionAlpha * (SkinParameters2.z - SkinParameters1.z * GeneralParameters.y)
                + lipPowerMask * SkinParameters2.w,
                0.1);
            return pow(max(reflectedViewLight, 0.0001), femalePower) * max(femaleTint, 0);
        }

        if (le3)
        {
            // LE3 replaced the LE1/LE2 packed addition-spec equation. The
            // addition alpha suppresses the base HED_Diff-alpha response,
            // while inverse packed blue selects both the secondary tint and
            // its independent Phong power.
            float inverseAddition = 1 - saturate(faceSelector * detailSample.a);
            float inversePackedBlue = saturate(2 - 2 * detailSample.b);
            float materialPower = max(
                (1 - inversePackedBlue) * GeneralParameters.y
                + inversePackedBlue * SkinParameters2.w,
                0.1);
            float3 le3Tint = SpecularColor.rgb * diffuseSample.a * inverseAddition
                + inversePackedBlue * SkinParameters1.w;
            return pow(max(reflectedViewLight, 0.0001), materialPower)
                * max(le3Tint, 0);
        }

        // Recovered from the LE1 directional-light permutation. Face specular
        // comes from HED_Diff alpha and the addition texture; HED_Mask is a
        // selector and must never be sampled directly as a specular map.
        float additionBlue = TextureFlags0.w > 0.5
            ? clamp(detailSample.b * 2 - 1, 0.1, 1)
            : 1;
        float baseSpecularMask = saturate(
            diffuseSample.a * (1 - faceSelector * detailSample.a));
        float materialPower = lerp(
            max(SkinParameters2.w, 0.1),
            max(GeneralParameters.y, 0.1),
            additionBlue);
        specular = pow(max(reflectedViewLight, 0.0001), materialPower);
        specularTint = max(
            SpecularColor.rgb * baseSpecularMask
            + (1 - additionBlue) * SkinParameters1.w,
            0);
    }
    else if (family == 6)
    {
        float4 makeup = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : 0;
        // LE3 removes the two fixed LE2 multiplier textures and the associated
        // post-shaped specular mask. Its direct-light permutation uses the
        // authored diffuse alpha/specular colour and lip-gloss term directly.
        if (le3)
        {
            float power = max(GeneralParameters.y + makeup.r * SkinParameters1.w, 0.1);
            float3 directSpecular = saturate(diffuseSample.a)
                * max(SpecularColor.rgb * SpecularColor.rgb, 0)
                + makeup.r * max(SkinParameters0.w, 0);
            return pow(max(reflectedViewLight, 0.0001), power)
                * max(directSpecular, 0);
        }
        float3 specMultiplier = TextureFlags1.w > 0.5
            ? AuxiliaryTexture4.Sample(MaterialSampler, materialUv).rgb
            : 1;
        float tiledMultiplier = TextureFlags1.w > 0.5
            ? AuxiliaryTexture4.Sample(MaterialSampler, materialUv * 2).r
            : 0;
        // The Asari master uses the makeup red channel to localize both lip
        // gloss and the additive exponent. SPwr_Multiplier is the base
        // exponent; neither scalar is a packed-mask multiplier.
        float power = max(GeneralParameters.y + makeup.r * SkinParameters1.w, 0.1);
        // LE1 compiles a linear grazing term here, not Schlick pow(5). It is
        // folded into the ordinary masked specular chain; LE2 compiles the
        // parameter out and supplies zero through the material constants.
        float fresnel = (1 - saturate(dot(normal, view))) * max(GeneralParameters.w, 0);
        float3 specularBase = (
            saturate(diffuseSample.a) * max(SpecularColor.rgb * SpecularColor.rgb, 0)
            + fresnel * max(SpecularColor.rgb, 0)
            + makeup.r * max(SkinParameters0.w, 0)
            + 4 * tiledMultiplier * tiledMultiplier)
            * saturate(diffuseSample.a);
        float3 shapedSpecular = lerp(
            1.5 * specularBase,
            saturate(diffuseSample.a) - 1.5 * specularBase,
            saturate(specMultiplier.b))
            * saturate(specMultiplier.r);
        specular = pow(max(reflectedViewLight, 0.0001), power);
        specularTint = max(shapedSpecular, 0);
    }
    else if (family == 7)
    {
        float4 tint = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : float4(1, 1, 1, 1);
        float additionMask = TextureFlags0.z > 0.5
            ? saturate(dot(maskSample.rgb, FreckleRedColor.rgb)
                + maskSample.a * GeneralParameters.x)
            : 1;
        float additionCoverage = additionMask * detailSample.a;
        float additionSpecular = saturate(
            additionCoverage * SkinParameters0.z * saturate(tint.a * 1.85));
        float3 localSpecular = lerp(
            tint.rrr,
            max(FreckleGreenColor.rgb, 0),
            additionSpecular);
        // This linear 1-N.V term is literal SAL directional-light bytecode.
        // It remains inside the coloured Phong-mask chain; it is not an
        // independently added global rim/fresnel light.
        float fresnel = 1 - saturate(dot(normal, view));
        float3 baseSpecular = (
            max(1 - tint.g, 0) * max(albedo, 0) + fresnel.xxx)
            * max(SpecularColor.rgb, 0);
        specularTint = localSpecular + baseSpecular;
        if (TextureFlags1.z > 0.5)
        {
            // SAL_HED_SpecMap is present in the LE1 compiled direct-light
            // permutation and absent from LE2. Binding it only when the
            // compiled material exposes the parameter preserves that split.
            specularTint *= AuxiliaryTexture3.Sample(MaterialSampler, materialUv).rgb;
        }
        float materialPower = max(diffuseSample.r * GeneralParameters.w * 100, 0.1);
        specular = pow(max(reflectedViewLight, 0.0001), materialPower);
    }
    else if (family == 8)
    {
        // SAL_HED_EYE_Spec green drives the compiled 0..100 direct-light
        // exponent. Red is reserved for emissive; blue/alpha select iris and
        // pupil colour in the base pass.
        float materialPower = max(maskSample.g * 100, 0.1);
        specular = pow(max(reflectedViewLight, 0.0001), materialPower);
        specularTint = 1;
    }
    else if (family == 9)
    {
        float4 tint = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : 0;
        float additionMask = TextureFlags0.z > 0.5
            ? saturate(dot(maskSample.rgb, FreckleRedColor.rgb))
            : 1;
        float additionCoverage = saturate(additionMask * detailSample.a);
        float tattooCoverage = 0;
        if (TextureFlags1.y > 0.5 && TextureFlags0.z > 0.5)
        {
            float3 tattooPattern = AuxiliaryTexture2.Sample(MaterialSampler, materialUv).rgb;
            float primaryCoverage = dot(maskSample.rgb, SkinParameters1.rgb)
                + maskSample.a * SkinParameters0.x;
            float secondaryCoverage = dot(maskSample.rgb, SkinParameters2.rgb)
                + maskSample.a * SkinParameters0.y;
            tattooCoverage = saturate(
                primaryCoverage * dot(tattooPattern, SkinParameters3.rgb)
                + secondaryCoverage * dot(tattooPattern, ScalpParameters0.rgb));
        }
        // The compiled directional-light shader converts the normalized
        // authored controls to reflection-vector powers in the 0..100 range.
        float power = max(100 * lerp(GeneralParameters.w, GeneralParameters.z, saturate(tint.r)), 0.1);
        specular = pow(max(reflectedViewLight, 0.0001), power);
        // TUR_HED_Spec_Colour is gated by the raw diffuse alpha. Tint alpha is
        // reserved for the secondary-diffuse blend and never enters this lobe.
        specularTint = max(diffuseSample.a * SpecularColor.rgb, 0);
        specularTint = lerp(specularTint, max(FreckleGreenColor.rgb, 0), additionCoverage);
        specularTint = lerp(specularTint, max(FreckleBlueColor.rgb, 0), tattooCoverage);
    }
    else if (family == 10)
    {
        if (le3)
        {
            // LE3 replaces the packed three-lobe Turian eye with one white
            // reflection-vector Phong response. The uniform is an authored
            // exponent already; unlike Turian skin it is not multiplied by 100.
            float eyePhong = pow(
                max(reflectedViewLight, 0.0001),
                max(GeneralParameters.z, 0.1));
            return eyePhong * max(GeneralParameters.y, 0);
        }
        float4 packedSpec = TextureFlags0.w > 0.5 ? detailSample : 1;
        // TUR_Eye_Spec is not a conventional RGB tint. Red selects the iris
        // lobe, green the sclera lobe, and blue the separate lens lobe.
        float lens = pow(max(saturate(dot(reflect(-view, lensNormal), light)), 0.0001), max(GeneralParameters.z, 0.1));
        float3 lensSpecular = lens * (2 * packedSpec.b) * max(TertiaryColor.rgb, 0);

        // The original, unscaled lens normal adds the broad eclipse-shaped
        // edge response seen in the compiled direct-light permutation.
        float eclipse = 3 * pow(
            max(1 - saturate(dot(outerLensNormal, view)), 0.0001),
            4.25);
        float3 layeredTint = packedSpec.r * max(SecondaryColor.rgb, 0)
            + packedSpec.g * max(QuaternaryColor.rgb, 0)
            + eclipse;
        float layeredPower = max(
            dot(packedSpec.rg, GeneralParameters.yw),
            0.1);
        float layered = pow(max(reflectedViewLight, 0.0001), layeredPower);
        return lensSpecular + layered * layeredTint;
    }
    else if (family == 11)
    {
        float4 tint = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : 0;
        float complexionMask = TextureFlags0.z > 0.5
            ? min(dot(maskSample.rgb, FreckleRedColor.rgb), 1)
            : 1;
        float inverseComplexionMask = 1 - complexionMask;
        float additionAlpha = TextureFlags0.w > 0.5 ? detailSample.a : 0;
        float additionCoverage = additionAlpha * GeneralParameters.w * complexionMask;

        // Literal KRO direct-light mask chain. Diffuse alpha is multiplied by
        // 1.4 before it is combined with the inverse complexion region; the
        // addition alpha is a second, independent specular contribution.
        float surfaceSpecularMask = inverseComplexionMask * diffuseSample.a * 1.4
            + additionCoverage;
        float3 localSpecular = surfaceSpecularMask
            * tint.g
            * max(SpecularColor.rgb, 0)
            * max(SkinParameters0.x, 0);
        float nv = saturate(dot(normal, view));
        float shellViewTerm = pow(max(nv, 0.0001), 5);
        float shellCoverage = min(tint.r + additionAlpha * GeneralParameters.w, 1);
        localSpecular += shellViewTerm
            * shellCoverage
            * max(TransmissionColor.rgb, 0);
        localSpecular = lerp(
            localSpecular,
            diffuseSample.a * 1.4 * tint.rgb,
            SkinParameters0.y);

        float materialPower = tint.a * SkinParameters0.z * 100;
        return pow(max(reflectedViewLight, 0.0001), materialPower)
            * max(localSpecular, 0);
    }
    else if (family == 13)
    {
        float4 packedSpec = TextureFlags1.y > 0.5
            ? AuxiliaryTexture2.Sample(MaterialSampler, materialUv)
            : float4(0, 0, 0, 1);
        float complexionMask = TextureFlags0.z > 0.5
            ? min(dot(maskSample.rgb, FreckleBlueColor.rgb), 1)
            : 1;
        float additionAlpha = TextureFlags0.w > 0.5 ? detailSample.a : 0;
        float3 specularColour = min(
            packedSpec.g + additionAlpha * SkinParameters0.y * max(SpecularColor.rgb, 0) * 0.1,
            1);
        float le1Power = pow(abs(packedSpec.r), 0.45) * SkinParameters0.z * 100;
        float le2Power = packedSpec.r * 100;
        float materialPower = SurfaceParameters.w > 0.5 ? le2Power : le1Power;
        return pow(max(reflectedViewLight, 0.0001), max(materialPower, 0.0001))
            * max(specularColour, 0);
    }
    else if (family == 12)
    {
        float4 packedSpec = TextureFlags0.w > 0.5 ? detailSample : 1;
        float lensPhong = pow(
            max(saturate(dot(lensNormal, halfDirection)), 0.0001),
            500);
        float3 lensLobe = lensPhong * (packedSpec.b * 2) * 0.45;
        float3 reflectedEyeView = reflect(-view, normal);
        float3 cube = TextureFlags1.y > 0.5
            ? FixedCubeTexture.Sample(MaterialSampler, reflectedEyeView).rgb
            : 0;
        float cubeFactor = 0.1 * (1 - pow(
            max(1 - saturate(dot(normal, view)), 0.0001),
            0.35));
        float3 cubeLobe = cube * cube * cubeFactor;
        float normalLight = saturate(dot(normal, light));
        float irisPower = max(packedSpec.r + 60 * packedSpec.g, 0.1);
        float irisPhong = pow(max(reflectedViewLight, 0.0001), irisPower);
        return normalLight * (cubeLobe + lensLobe)
            + irisPhong * packedSpec.g;
    }
    else if (family == 2)
    {
        if (le3)
        {
            if (IsCcScalpPreview())
            {
                CcScalpMasks scalpMasks = EvaluateCcScalpMasks(maskSample);
                // The directional FXC builds two shifted tangent lobes from
                // normalized |HairColor| raised to HED_Spec_Aniso_Exp. The
                // second tint is a desaturated copy of that powered colour.
                float3 hairColor = abs(SecondaryColor.rgb);
                float hairLength = max(length(hairColor), 0.0001);
                float3 normalizedHair = hairColor / hairLength;
                float3 primaryTint = pow(max(normalizedHair, 0.0001),
                    max(ScalpParameters1.w, 0.1));
                float3 secondaryTint = lerp(primaryTint,
                    Luminance(primaryTint), saturate(ScalpParameters2.x));

                float3 halfDirection = normalize(light + view);
                float3 firstFibre, secondFibre;
                CcScalpFibreDirections(normal, tangent, bitangent, detailSample.rgb,
                    materialUv, firstFibre, secondFibre);
                float3 specShift = 1;
                if (TextureFlags1.y > 0.5)
                {
                    specShift = AuxiliaryTexture2.Sample(MaterialSampler, materialUv).rgb;
                }
                float primaryPower = hairColor.r <= 0.09
                    ? 400 : (hairColor.r < 0.4 ? 200 : 100);
                float secondaryPower = hairColor.r <= 0.09 ? 30 : 15;
                float primaryLobe = FibreLobe(secondFibre, halfDirection, primaryPower);
                float secondaryLobe = FibreLobe(firstFibre, halfDirection, secondaryPower);
                float3 hairSpecular = (primaryLobe * primaryTint
                    + secondaryLobe * secondaryTint)
                    * specShift
                    * diffuseSample.a
                    * scalpMasks.Mask
                    * ScalpParameters1.z;

                float teeth = TextureFlags1.x > 0.5
                    ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv).g
                    : 0;
                float3 baseSpecular = SpecularColor.rgb
                    * diffuseSample.a
                    * (1 - teeth * ScalpParameters1.y)
                    * scalpMasks.Overlay;
                float phong = pow(max(reflectedViewLight, 0.0001),
                    max(GeneralParameters.y, 0.1));
                return phong * max(baseSpecular + hairSpecular, 0);
            }

            // LE3's scalp master has no packed hair-mask/anisotropic branch.
            // HED_Scalp_Spec is a direct RGB blend between dielectric scalp
            // specular and the ordinary HED_Spec_Add response.
            float3 packedSpec = saturate(maskSample.rgb);
            float3 le3Tint = diffuseSample.rgb * packedSpec * 0.04
                + (1 - packedSpec) * diffuseSample.a * max(SpecularColor.rgb, 0);
            return pow(max(reflectedViewLight, 0.0001), max(GeneralParameters.y, 0.1))
                * max(le3Tint, 0);
        }

        // LegacyScalpDirectLight evaluates diffuse and both native hair lobes
        // together, because its diffuse composition also depends on the light.
        return 0;
    }
    else if (family == 3)
    {
        if (EyeParameters.w > 0.5)
        {
            // Keep the direct-light catchlights tight and reflection-aligned.
            // The former broad Blinn exponent-10 lobe was only a stand-in for
            // the fixed cubes; retaining it after restoring those cubes spread
            // the highlight across the iris and made the cornea look plastic.
            float primary = pow(max(reflectedViewLight, 0.0001), 100)
                * max(EyeParameters2.x, 0);
            float lensReflection = saturate(dot(reflect(-view, lensNormal), light));
            float secondary = pow(max(lensReflection, 0.0001), 500)
                * max(EyeParameters2.y, 0);
            return (primary + secondary).xxx;
        }

        // LE1 likewise uses a compact wet corneal catchlight.
        specular = pow(max(reflectedViewLight, 0.0001), 100);
    }
    else if (family == 4)
    {
        specular = 0;
    }
    else if (family == 5)
    {
        float tangentDot = saturate(abs(dot(tangent, halfDirection)));
        float tangentLobe = sqrt(saturate(1 - tangentDot * tangentDot));
        if (IsHumanHairPreview())
        {
            float4 hairStack = TextureFlags1.x > 0.5
                ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
                : float4(0, 0, 0, 1);
            return saturate(dot(normal, light))
                * EvaluateHumanHairStack(hairStack, tangent, halfDirection);
        }
        bool layeredHair = SkinParameters3.w > 0.5;
        if (layeredHair)
        {
            bool additionalHair = SkinParameters3.z > 0.5;
            // LE3 standard hair and the backported LE1/LE2 iconic-HMF master
            // expose both anisotropic highlight colours and powers. The
            // additional-hair variant has no intensity uniforms or extra gain.
            float primary = pow(max(tangentLobe, 0.0001), max(ScalpParameters2.x, 0.1));
            float secondary = pow(max(tangentLobe, 0.0001), max(ScalpParameters2.z, 0.1));
            float primaryIntensity = additionalHair ? 1 : max(ScalpParameters2.y, 0);
            float secondaryIntensity = additionalHair ? 1 : max(ScalpParameters2.w, 0);
            float3 highlights = primary * max(SecondaryColor.rgb, 0) * primaryIntensity
                + secondary * max(TertiaryColor.rgb, 0) * secondaryIntensity;
            return saturate(dot(normal, light)) * abs(diffuseSample.r)
                * (additionalHair ? 1 : 3) * highlights;
        }

        // The graph exposes Highlight1/2 scalar parameters, but both supplied
        // hair permutations compile them out. The directional-light FXC keeps
        // the narrow lobe at 400 until light hair (>= 0.4), then uses 100;
        // the broad lobe changes from 30 to 15 at the 0.09 threshold.
        float primaryPower = IsCcHairPreview()
            ? (BaseColor.r < 0.09 ? 400 : (BaseColor.r < 0.4 ? 200 : 100))
            : (BaseColor.r < 0.4 ? 400 : 100);
        float secondaryPower = BaseColor.r < 0.09 ? 30 : 15;
        float primary = pow(max(tangentLobe, 0.0001), primaryPower);
        float secondary = pow(max(tangentLobe, 0.0001), secondaryPower);
        // The directional-light FXC scales HairColour before saturating each
        // lobe. It does not use the generic HED specular colour or fixed gains.
        // Dark, mid and light hair select 7/4, 6/3 and 3/1.5.
        float primaryScale = BaseColor.r < 0.09 ? 7 : (BaseColor.r < 0.4 ? 6 : 3);
        float secondaryScale = BaseColor.r < 0.09 ? 4 : (BaseColor.r < 0.4 ? 3 : 1.5);
        float3 primaryTint = saturate(max(BaseColor.rgb, 0) * primaryScale);
        float3 secondaryTint = saturate(max(BaseColor.rgb, 0) * secondaryScale);
        float fibreIntensity = abs(diffuseSample.g);
        float diffuseGain = BaseColor.r < 0.09 ? 0.5 : 0.25;
        float3 baseDiffuse = fibreIntensity * diffuseGain * max(BaseColor.rgb, 0);
        float3 lobeColour = IsCcHairPreview()
            ? 0.5 * fibreIntensity + 0.75 * baseDiffuse
            : 0.5 * (fibreIntensity + baseDiffuse);
        float lobeGain = BaseColor.r < 0.09 ? 1 : 0.5;
        float lightFacing = saturate(dot(normal, light));
        return lightFacing
            * lobeGain
            * lobeColour
            * (primary * primaryTint + secondary * secondaryTint);
    }
    else if (family == 14)
    {
        // PROShort01 has its own literal custom-lighting equation below.
        // Keep it out of the renderer's generic Blinn response.
        return 0;
    }
    else
    {
        specular = pow(max(normalHalf, 0.0001), 32) * 0.15;
    }

    return specular * specularTint;
}

float3 EvaluateSubsurfaceLight(
    float family,
    bool le3,
    float3 normal,
    float3 view,
    float3 light,
    float4 diffuseSample,
    float3 albedo,
    float3 scatteringAlbedo,
    float4 surfaceSelector,
    float transmissionFactor)
{
    if (family == 2 && !le3)
    {
        return 0; // Evaluated with the native scalp's diffuse response below.
    }
    if (family != 1 && family != 2 && family != 3 && family != 6 && family != 7 && family != 9 && family != 11 && family != 13 && family != 15)
    {
        return 0;
    }

    // LE1's face/scalp direct-light permutations use a coloured wrapped
    // N.L term. The wrap width is driven by (1 - HED_Diff.a) multiplied by
    // SkinLightScattering; HED_TClr/HED_TMis then add the material's localized
    // transmission colour. This must be evaluated per light: the previous
    // view-rim approximation reacted to camera angle, not light direction.
    float thickness = family == 3 ? 1 : saturate(1 - diffuseSample.a);
    if (family == 7)
    {
        // Literal SAL direct-light bytecode: the wrapped-light thickness is
        // 1 - SAL_HED_Tint.G. The LE3 diffuse is DXT1 and therefore has an
        // opaque decoded alpha; using the shared diffuse-alpha rule reduced
        // every SkinLightScattering value to zero.
        thickness = saturate(transmissionFactor);
    }
    if (family == 9)
    {
        // The Turian master localises skin scattering to Tint.R. Its direct
        // permutation then blends wrapped N.L toward a diffuse-green/TMis
        // albedo response using the diffuse texture RGB per output channel.
        thickness *= saturate(surfaceSelector.r);
    }
    if (family == 15)
    {
        // Vorcha localises scattering to inverse Tint.B in the compiled
        // direct-light permutation.
        thickness *= saturate(1 - surfaceSelector.b);
    }
    if (family == 11)
    {
        float kroganThickness = saturate(1 - surfaceSelector.r);
        float3 kroganWrap = max(ScatterColor.rgb, 0)
            * saturate(albedo.g)
            * kroganThickness;
        float normalLight = dot(normal, light);
        float3 wrappedKrogan = saturate(
            (normalLight + kroganWrap) / max(1 + kroganWrap, 0.0001));
        float directKrogan = saturate(normalLight);
        float3 transmissionTarget = albedo
            * saturate(diffuseSample.r)
            * max(transmissionFactor, 0);
        float3 compiledKrogan = lerp(
            wrappedKrogan,
            transmissionTarget,
            transmissionTarget);
        return albedo * (compiledKrogan - directKrogan);
    }
    if (family == 13)
    {
        float3 wrapAmount = (1 - saturate(surfaceSelector.g)) * max(ScatterColor.rgb, 0);
        float normalLight = dot(normal, light);
        float3 wrappedBatarian = saturate(
            (normalLight + wrapAmount) / max(1 + wrapAmount, 0.0001));
        float directBatarian = saturate(normalLight);
        float transmission = saturate(diffuseSample.r)
            * max(transmissionFactor, 0)
            * (1 - saturate(dot(normal, view)));
        float3 compiledBatarian = lerp(
            wrappedBatarian,
            transmission.xxx,
            transmission.xxx);
        return albedo * (compiledBatarian - directBatarian);
    }
    float3 wrapAmount = max(ScatterColor.rgb, 0) * thickness * 0.5;
    float normalLight = dot(normal, light);
    float3 wrappedDiffuse = saturate(
        (normalLight + wrapAmount) / max(1 + wrapAmount, 0.0001));
    float directDiffuse = saturate(normalLight);
    float3 wrapContribution = max(wrappedDiffuse - directDiffuse, 0);

    if (family == 1 || (le3 && family == 2))
    {
        // Native human direct-light passes blend wrapped diffuse toward the
        // localized transmission colour, using that colour as the blend weight.
        // LE1 male faces additionally multiply the target by diffuse RGB;
        // female faces and LE2/LE3 faces do not.
        float3 localizedTransmission = max(TransmissionColor.rgb, 0)
            * max(transmissionFactor, 0);
        if (family == 1 && !le3 && HumanParameters.y < 0.5 && SkinParameters3.w < 0.5)
        {
            localizedTransmission *= diffuseSample.rgb;
        }
        float3 compiledLighting = lerp(
            wrappedDiffuse,
            localizedTransmission,
            localizedTransmission);
        return albedo * (compiledLighting - directDiffuse);
    }

    float3 transmission = 0;
    if (family == 3)
    {
        // LE3's eye direct-light permutation multiplies EYE_Diff by
        // Tmission_Color inside its wrapped-light branch. The uniform is live;
        // omitting family 3 here made the editor's colour control inert.
        transmission = diffuseSample.rgb
            * max(TransmissionColor.rgb, 0)
            * wrappedDiffuse;
    }
    if (family == 9)
    {
        float3 compiledLighting = lerp(
            wrappedDiffuse,
            max(transmissionFactor, 0) * saturate(diffuseSample.g) * scatteringAlbedo,
            saturate(diffuseSample.rgb));
        // Ordinary N.L is already present in diffuseLighting. Return the
        // signed correction needed to reproduce the compiled Turian factor.
        return albedo * (compiledLighting - directDiffuse);
    }
    if (family == 1 || family == 2 || family == 6 || family == 7 || family == 15)
    {
        transmission = diffuseSample.rgb
            * max(TransmissionColor.rgb, 0)
            * max(transmissionFactor, 0)
            * wrappedDiffuse
            * 0.25;
    }

    return albedo * wrapContribution + transmission;
}

float3 EvaluateMaskedHairLight(
    float3 geometricNormal,
    float3 fibreTangent,
    float3 view,
    float3 light,
    float3 diffuseColour,
    float3 specularColour)
{
    float normalLight = saturate(dot(geometricNormal, light));
    float diffuseAnisotropy = sqrt(saturate(
        1 - dot(fibreTangent, light) * dot(fibreTangent, light)));
    float3 halfDirection = normalize(view + light);
    float tangentHalf = dot(fibreTangent, halfDirection);
    float specularAnisotropy = pow(
        max(sqrt(saturate(1 - tangentHalf * tangentHalf)), 0.0001),
        500);
    return normalLight * (
        diffuseColour * diffuseAnisotropy
        + specularColour * specularAnisotropy);
}

// Canonical headwear uses its own packed-map contracts. Model IDs are the
// appended HumanMaterialPreviewModel values; family/control eligibility stays
// Accessory. Equations recovered from each game's FGPUSkin DXBC base/light passes.
float3 AttachmentDirectLight(float model, bool le3, float3 rawNormal,
    float3 normal, float3 view, float3 light, float3 albedo,
    float4 diffuse, float4 specMap, float viewFactor)
{
    float facing = saturate(dot(normal, light));
    float reflected = max(saturate(dot(reflect(-view, normal), light)), 0.0001);
    float power = 32;
    float strength = 0;
    float3 tint = 1;
    if (model == 5)
    {
        power = 100 * pow(max(abs(diffuse.r), 0.0001), 0.45);
        strength = diffuse.g;
        tint = SpecularColor.rgb;
    }
    else if (model == 7)
    {
        power = diffuse.b * 100;
        strength = diffuse.g;
        if (le3)
        {
            // The additional Phong term is part of the native diffuse input.
            float3 rawReflection = normalize(reflect(-light, rawNormal));
            albedo += SpecularColor.rgb * pow(max(saturate(dot(view, rawReflection)), 0.0001), max(GeneralParameters.z, 0.0001));
        }
    }
    else if (model == 8)
    {
        power = 100 * pow(max(abs(specMap.r), 0.0001), 0.45);
        strength = specMap.g;
    }
    else if (model == 9)
    {
        float3 rawReflection = normalize(reflect(-light, rawNormal));
        albedo += specMap.b * SpecularColor.rgb
            * pow(max(saturate(dot(view, rawReflection)), 0.0001), max(GeneralParameters.z, 0.0001));
        albedo *= viewFactor;
        power = specMap.r * 100;
        strength = specMap.g;
        // Anderson's native wrap term uses diffuse alpha and the inverse spec
        // intensity as its scattering mask. The preview supplies a fixed wrap.
        float3 wrap = (1 - specMap.g) * ScatterColor.rgb * diffuse.a * 0.5;
        float3 diffuseResponse = saturate((dot(normal, light) + wrap) / (1 + wrap));
        return albedo * diffuseResponse + strength * pow(reflected, power);
    }
    else if (model == 10)
    {
        power = specMap.b * 100;
        strength = specMap.g;
    }
    return albedo * facing + strength * tint * pow(reflected, power);
}

float4 AttachmentPreview(VertexOutput input, bool isFrontFace, out float4 aoNormalTarget, out float4 ambientDiffuseTarget)
{
    ambientDiffuseTarget = 0;
    float model = TextureFlags2.z;
    bool le3 = EyeParameters2.w > 0.5;
    bool le2 = EyeParameters2.z > 0.5;
    float3 geometricNormal = normalize(input.Normal);
    if (TextureFlags2.x > 0.5 && !isFrontFace) geometricNormal = -geometricNormal;
    float3 viewNormal = normalize(mul(float4(geometricNormal, 0), View).xyz);
    aoNormalTarget = float4(viewNormal * 0.5 + 0.5, 1);
    float3 tangent = normalize(input.Tangent.xyz - geometricNormal * dot(input.Tangent.xyz, geometricNormal));
    float3 bitangent = normalize(cross(geometricNormal, tangent)) * input.Tangent.w;
    float3 view = normalize(CameraPosition.xyz - input.WorldPosition);
    float3 key = normalize(KeyLightDirection.xyz);
    float3 shadowedKeyColor = KeyLightColor.rgb * KeyShadowVisibility(input.WorldPosition, geometricNormal);
    float3 fill = normalize(FillLightDirection.xyz);
    float3 rim = normalize(RimLightDirection.xyz);
    if (SurfaceParameters.y > 0.5)
    {
        float3 color = BaseColor.rgb * (AmbientColor.rgb
            + KeyLightColor.rgb * saturate(dot(geometricNormal, key))
            + FillLightColor.rgb * saturate(dot(geometricNormal, fill))
            + RimLightColor.rgb * saturate(dot(geometricNormal, rim))) * LightingParameters.x;
        ambientDiffuseTarget = float4(BaseColor.rgb * AmbientColor.rgb, 1);
        return float4(color, 1);
    }
    if (model == 11)
    {
        // LE1/2 HMM visor lenses use an inline additive, unlit material, not
        // the HMF_VSR master. The renderer supplies wrapped, per-frame phases
        // in the three existing offset vectors; dynamic opacity is fixed at 1.
        float scanline = TextureFlags0.x > 0.5
            ? DiffuseTexture.Sample(MaterialSampler,
                input.TexCoord * float2(1, -1) + SecondaryColor.xy).r
            : 0;
        float2 distortion = TextureFlags0.z > 0.5
            ? MaskTexture.Sample(MaterialSampler, input.TexCoord + TertiaryColor.xy).rg
            : 0;
        float3 fractal = TextureFlags0.w > 0.5
            ? DetailTexture.Sample(MaterialSampler,
                input.TexCoord + QuaternaryColor.xy + distortion).rgb
            : 0;
        float3 emission = TextureFlags0.y > 0.5
            ? NormalTexture.Sample(MaterialSampler, input.TexCoord).rgb
            : 0;
        float3 lensBase = fractal * emission * 25
            + scanline * float3(0.25, 1, 1.5);
        if (!le2 && TextureFlags1.y > 0.5)
        {
            float3 cube = FixedCubeTexture.Sample(MaterialSampler, reflect(-view, geometricNormal)).rgb;
            float cubeLuminance = dot(cube, float3(0.3, 0.59, 0.11));
            float reflectionFactor = max(abs(1 - max(dot(geometricNormal, view), 0)), 0.0001);
            lensBase = lerp(lensBase, cubeLuminance * float3(0.5, 2, 3), reflectionFactor);
        }
        float3 lensColor = (BaseColor.rgb + lensBase) * (0.1 * GeneralParameters.y);
        return float4(lensColor, 0);
    }
    float4 diffuse = TextureFlags0.x > 0.5 ? DiffuseTexture.Sample(MaterialSampler, input.TexCoord) : 1;
    float4 specMap = TextureFlags0.z > 0.5 ? MaskTexture.Sample(MaterialSampler, input.TexCoord) : 0;
    float4 packedNormal = TextureFlags0.y > 0.5 ? NormalTexture.Sample(MaterialSampler, input.TexCoord) : float4(0.5, 0.5, 1, 1);
    // LE1 VSR decodes RGB; LE2 VSR reconstructs Z from RG. LE3 hats use RGB.
    float3 tangentNormal = le3 || (model == 6 && !le2)
        ? packedNormal.rgb * 2 - 1 : DecodeNormal(packedNormal);
    float3 rawNormal = tangent * tangentNormal.x + bitangent * tangentNormal.y + geometricNormal * tangentNormal.z;
    float3 normal = normalize(rawNormal);
    float3 reflectedView = reflect(-view, normal);
    float3 cube = TextureFlags1.y > 0.5 ? FixedCubeTexture.Sample(MaterialSampler, reflectedView).rgb : 0;
    float3 albedo = diffuse.rgb;
    float3 ambientAlbedo = albedo;
    float3 ambientDiffuseAlbedo = albedo;
    float3 emissive = 0;
    float alpha = 1;
    float viewFactor = 1;
    if (model == 5)
    {
        albedo = lerp(SecondaryColor.rgb, BaseColor.rgb, diffuse.b) * diffuse.a;
        ambientDiffuseAlbedo = albedo;
        // LE3 removed both the cube sampler and its intensity parameter.
        if (!le3) albedo += diffuse.g * cube * GeneralParameters.x;
        ambientAlbedo = albedo;
    }
    else if (model == 6)
    {
        float normalView = dot(rawNormal, view);
        float fresnel = 1 - saturate(normalView);
        float gain = max(sqrt(max(fresnel, 0.0001)), SecondaryColor.x);
        float3 color = (cube * GeneralParameters.x + tangentNormal * BaseColor.rgb) * gain;
        alpha = le2 ? GeneralParameters.y : lerp(0.5 * GeneralParameters.y, GeneralParameters.y, pow(1 - max(normalView, 0), 3));
        // The VSR graph is unlit; fog/SelectionColor are engine inputs omitted
        // from the standalone studio preview.
        ambientDiffuseTarget.a = saturate(alpha);
        return float4(color, saturate(alpha));
    }
    else if (model == 7)
    {
        clip(diffuse.a * GeneralParameters.y - 0.3333);
        albedo = diffuse.r * (le3 ? TertiaryColor.rgb : float3(1, 1, 1));
        ambientDiffuseAlbedo = albedo;
        float3 baseReflection = normalize(reflect(-geometricNormal, rawNormal));
        ambientAlbedo = albedo + (le3 ? SpecularColor.rgb
            * pow(max(saturate(dot(view, baseReflection)), 0.0001), max(GeneralParameters.z, 0.0001)) : 0);
    }
    else if (model == 9)
    {
        viewFactor = sqrt(max(saturate(dot(rawNormal, view)), 0.0001));
        ambientDiffuseAlbedo = albedo * viewFactor;
        float3 baseReflection = normalize(reflect(-geometricNormal, rawNormal));
        ambientAlbedo = (albedo + specMap.b * SpecularColor.rgb
            * pow(max(saturate(dot(view, baseReflection)), 0.0001), max(GeneralParameters.z, 0.0001))) * viewFactor;
    }
    else if (model == 10)
    {
        albedo += saturate(dot(geometricNormal, view)) * cube * GeneralParameters.x * specMap.r;
        ambientAlbedo = albedo;
        emissive = specMap.a * GeneralParameters.w * EyeEmissiveColor.rgb;
    }
    if (LightingParameters.w > 0.5)
    {
        ambientDiffuseTarget.a = alpha;
        return float4(albedo + emissive, alpha);
    }
    float3 lit = ambientAlbedo * AmbientColor.rgb
        + AttachmentDirectLight(model, le3, rawNormal, normal, view, key, albedo, diffuse, specMap, viewFactor) * shadowedKeyColor
        + AttachmentDirectLight(model, le3, rawNormal, normal, view, fill, albedo, diffuse, specMap, viewFactor) * FillLightColor.rgb
        + AttachmentDirectLight(model, le3, rawNormal, normal, view, rim, albedo, diffuse, specMap, viewFactor) * RimLightColor.rgb;
    ambientDiffuseTarget = float4(ambientDiffuseAlbedo * AmbientColor.rgb, alpha);
    return float4(lit + emissive, alpha);
}

float4 PSMain(
    VertexOutput input,
    bool isFrontFace : SV_IsFrontFace,
    out float4 aoNormalTarget : SV_TARGET1,
    out float4 ambientDiffuseTarget : SV_TARGET2) : SV_TARGET0
{
    ambientDiffuseTarget = 0;
    if (TextureFlags2.z >= 5 && TextureFlags2.z <= 11)
        return AttachmentPreview(input, isFrontFace, aoNormalTarget, ambientDiffuseTarget);
    float family = SurfaceParameters.z;
    float diagnostic = SurfaceParameters.y;
    bool le3 = EyeParameters2.w > 0.5;
    float2 materialUv = input.TexCoord;
    if (family == 3)
    {
        // LE1 compiles fixed 0.9/1.2 values; LE2 exposes the same values as
        // X_Tile/Y_Tile. U/V remain offsets after scaling in both games.
        materialUv = input.TexCoord * EyeParameters1.xy + EyeParameters.xy;
    }

    float2 selectorUv = materialUv;
    float4 maskSample = TextureFlags0.z > 0.5 && diagnostic < 0.5
        ? MaskTexture.Sample(MaterialSampler, materialUv)
        : float4(1, 1, 1, 1);
    if (((family == 10 && !le3) || family == 12) && diagnostic < 0.5)
    {
        // Eye_Pupil/Krogan_Pupil scales U about 0.5 only inside Mask.G. The
        // mask itself continues to use the original UV in both compiled eyes.
        float pupilScale = lerp(1, GeneralParameters.x, maskSample.g);
        materialUv.x = pupilScale * materialUv.x + (1 - pupilScale) * 0.5;
    }
    float4 diffuseSample = TextureFlags0.x > 0.5 && diagnostic < 0.5
        ? DiffuseTexture.Sample(MaterialSampler, materialUv)
        : float4(1, 1, 1, 1);
    float4 detailSample = TextureFlags0.w > 0.5 && diagnostic < 0.5
        ? DetailTexture.Sample(MaterialSampler, materialUv)
        : float4(0.5, 0.5, 1, 0);
    float4 customBrowSample = TextureFlags2.x > 0.5 && diagnostic < 0.5
        ? CustomBrowTexture.Sample(MaterialSampler, materialUv)
        : float4(0.5, 0.5, 1, 0);
    float4 customScarSample = TextureFlags2.y > 0.5 && diagnostic < 0.5
        ? CustomScarTexture.Sample(MaterialSampler, materialUv)
        : float4(0.5, 0.5, 1, 0);

    // The face mask is a packed selector shared by colour, normal, scar, and
    // specular branches. Recover it before normal composition so every branch
    // uses the same LE1 value.
    float faceSelector = 0;
    bool femaleFace = family == 1 && SkinParameters3.w > 0.5;
    if (family == 1 && !femaleFace)
    {
        faceSelector = saturate(
            dot(maskSample.rgb, TertiaryColor.rgb)
            + maskSample.a * GeneralParameters.z);
    }

    float3 tangentNormal = float3(0, 0, 1);
    float3 lensTangentNormal = float3(0, 0, 1);
    float3 outerLensTangentNormal = float3(0, 0, 1);
    if (TextureFlags0.y > 0.5 && diagnostic < 0.5)
    {
        float4 packedNormal = NormalTexture.Sample(MaterialSampler, materialUv);
        // LE2 stores the audited Asari/Salarian normals as BC5 and reconstructs
        // tangent Z from RG. LE3 stores them as DXT1/DXT5 and the compiled
        // shaders consume all three RGB channels. The LE3 alien normal is
        // deliberately left unnormalised here. The compiled face graphs
        // add the Addn.xy/-1 perturbation to the raw unpacked DXT1 sample and
        // normalises only the combined vector below. Normalising this sample
        // first collapses its authored Z magnitude, so the subsequent -1
        // makes tiny scale detail dominate the lighting.
        tangentNormal = (family == 6 || family == 7 || family == 8 || family == 9 || family == 10 || family == 11 || family == 12 || family == 13 || family == 15 || family == 16) && le3
            ? packedNormal.rgb * 2 - 1
            : DecodeNormal(packedNormal);
        if (family == 1 && TextureFlags1.y > 0.5)
        {
            float3 secondaryNormal = DecodeNormal(AuxiliaryTexture2.Sample(MaterialSampler, materialUv));
            // The stock graph blends the two decoded vectors first and
            // normalises only after the Addn perturbation has been accumulated.
            tangentNormal = lerp(tangentNormal, secondaryNormal, saturate(SkinParameters0.x));
        }
        if (family == 1 && TextureFlags0.w > 0.5)
        {
            // HED_Addn RG is an additive tangent-space perturbation. Its
            // spatial selector comes from HED_Mask, not HED_Addn alpha.
            float2 additionNormal = detailSample.rg * 2 - 1;
            float additionSelector = femaleFace
                ? saturate(detailSample.a * 10)
                : TextureFlags2.x > 0.5 ? 1 : faceSelector;
            tangentNormal = normalize(tangentNormal + float3(
                additionNormal * additionSelector * SkinParameters0.y,
                0));
        }
        if (family == 1 && TextureFlags2.x > 0.5)
        {
            // The PROCustom face master uses HED_Brow as a second packed
            // addition: RG is a normal perturbation and blue fades it out.
            float2 browNormal = customBrowSample.rg * 2 - 1;
            float browNormalMask = saturate(customBrowSample.b * 10);
            tangentNormal = normalize(tangentNormal + float3(
                browNormal * browNormalMask * SkinParameters0.y,
                0));
        }
        if (family == 1 && TextureFlags2.y > 0.5)
        {
            // HED_Scar alpha and HED_Custom_Scar_Scalar blend its authored
            // tangent normal over the composed face/brow normal.
            float3 scarNormal = normalize(float3(customScarSample.rg * 2 - 1, 1));
            float scarNormalMask = saturate(
                customScarSample.a * CustomSkinParameters.x);
            tangentNormal = normalize(lerp(tangentNormal, scarNormal, scarNormalMask));
        }
        if (family == 6 && TextureFlags0.w > 0.5)
        {
            // ASA_HED_Addn is a normal perturbation as well as an albedo
            // input. The packed face mask chooses where its RG channels are
            // added to the base normal; Addn_Colour_Scalar does not gate it.
            float additionMask = TextureFlags0.z > 0.5
                ? saturate(dot(maskSample.rgb, BlondeColor.rgb)
                    + maskSample.a * SkinParameters0.x)
                : 1;
            float2 additionNormal = detailSample.rg * 2 - 1;
            // The LE3 bytecode appends -1 to this perturbation before writing
            // its deferred normal. Applying that literal value in this direct
            // forward-lit preview cancels the DXT1 base map's near-one Z and
            // turns the face normal almost sideways. Preserve the executable
            // RG selector, but compose it as an XY perturbation here so both
            // maps retain visible detail without overwhelming the viewport.
            tangentNormal = normalize(tangentNormal + float3(additionNormal * additionMask, 0));
        }
        if (family == 7 && TextureFlags0.w > 0.5)
        {
            // Both games add SAL Addn RG in the selected complexion region;
            // alpha gates colour coverage only. LE3 appends a literal -1 Z to
            // the perturbation after switching the base map from BC5 RG to
            // DXT1 RGB. As with the independently audited LE3 Asari path,
            // copying that deferred/direct-game cancellation into this
            // forward preview overwhelms the authored surface normal. Preserve
            // the proven RG mask while composing it as an XY perturbation.
            float additionMask = TextureFlags0.z > 0.5
                ? saturate(dot(maskSample.rgb, FreckleRedColor.rgb)
                    + maskSample.a * GeneralParameters.x)
                : 1;
            float2 additionNormal = detailSample.rg * 2 - 1;
            tangentNormal = normalize(tangentNormal + float3(
                additionNormal * additionMask, 0));
        }
        if (family == 9 && TextureFlags0.w > 0.5)
        {
            float additionMask = TextureFlags0.z > 0.5
                ? saturate(dot(maskSample.rgb, FreckleRedColor.rgb))
                : 1;
            float2 additionNormal = detailSample.rg * 2 - 1;
            // LE3 appends a literal -1 Z after switching the base normal from
            // BC5 RG reconstruction to DXT1 RGB. As in the independently
            // audited LE3 ASA/SAL forward-preview paths, retain the exact RG
            // selector but use an XY-only perturbation here so the game/deferred
            // Z cancellation does not turn the preview normal sideways.
            tangentNormal = normalize(tangentNormal + float3(additionNormal * additionMask, 0));
        }
        if (family == 11 && TextureFlags0.w > 0.5)
        {
            // KRO_HED_Addn.RG is added to the base normal after multiplication
            // by the RGB-dot complexion mask. Addn alpha and colour strength
            // do not participate in this normal equation. LE3 switches the
            // base from reconstructed BC5 RG to stored DXT1 RGB and appends a
            // literal -1 Z to the perturbation. As with the independently
            // audited LE3 ASA/SAL/TUR paths, retain the exact RG selector but
            // use an XY-only perturbation in this direct forward preview so
            // the deferred/game Z cancellation does not turn the face normal
            // almost tangent-facing.
            float complexionMask = TextureFlags0.z > 0.5
                ? min(dot(maskSample.rgb, FreckleRedColor.rgb), 1)
                : 1;
            float2 additionNormal = detailSample.rg * 2 - 1;
            tangentNormal = normalize(tangentNormal + float3(
                additionNormal * complexionMask, 0));
        }
        if (family == 13 && TextureFlags0.w > 0.5)
        {
            float complexionMask = TextureFlags0.z > 0.5
                ? min(dot(maskSample.rgb, FreckleBlueColor.rgb), 1)
                : 1;
            float2 additionNormal = detailSample.rg * 2 - 1;
            // LE2 reconstructs the base Z from BC5 RG and appends zero to the
            // selected Addn perturbation. LE3 samples stored RGB and appends a
            // literal -1. As with the independently audited LE3 alien heads,
            // retain the exact RGB/RG sources and selector but omit that
            // deferred-game Z cancellation in this direct forward preview.
            tangentNormal = normalize(tangentNormal + float3(
                additionNormal * complexionMask, 0));
        }
    }

    if (family == 3 && EyeParameters.w > 0.5 && TextureFlags1.x > 0.5 && diagnostic < 0.5)
    {
        lensTangentNormal = DecodeNormal(AuxiliaryTexture1.Sample(MaterialSampler, materialUv));
        // The LE2 eye graph composes iris and lens normals using inverse packed
        // mask green. Keep the separate lens normal for its reflection lobe.
        tangentNormal = normalize(lerp(
            tangentNormal,
            lensTangentNormal,
            saturate(1 - maskSample.g)));
    }
    if (family == 10 && TextureFlags1.x > 0.5 && diagnostic < 0.5)
    {
        lensTangentNormal = DecodeNormal(AuxiliaryTexture1.Sample(MaterialSampler, materialUv));
        outerLensTangentNormal = DecodeNormal(AuxiliaryTexture1.Sample(MaterialSampler, selectorUv));
    }
    if (family == 12 && TextureFlags1.x > 0.5 && diagnostic < 0.5)
    {
        float4 packedLensNormal = AuxiliaryTexture1.Sample(MaterialSampler, materialUv);
        lensTangentNormal = le3
            ? normalize(packedLensNormal.rgb * 2 - 1)
            : DecodeNormal(packedLensNormal);
    }

    float3 geometricNormal = normalize(input.Normal);
    if ((family == 4 || family == 5) && !isFrontFace)
    {
        // UE3's TwoSidedSign flips translucent card lighting on backfaces.
        geometricNormal = -geometricNormal;
    }
    float3 viewGeometricNormal = normalize(mul(float4(geometricNormal, 0), View).xyz);
    aoNormalTarget = float4(viewGeometricNormal * 0.5 + 0.5, 1);
    // Morph targets update TangentZ (the geometric normal) but LE1 retains the
    // base TangentX. Re-orthogonalize TangentX against the live normal before
    // applying either face or scalp normal maps; otherwise strong HMF
    // HED_Norm_Blend values amplify the stale basis at their shared boundary.
    float3 tangent = input.Tangent.xyz - geometricNormal * dot(input.Tangent.xyz, geometricNormal);
    tangent = normalize(tangent);
    float3 bitangent = normalize(cross(geometricNormal, tangent)) * input.Tangent.w;
    float3 normal = diagnostic < 0.5
        ? normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + geometricNormal * tangentNormal.z)
        : geometricNormal;
    float3 lensNormal = diagnostic < 0.5
        ? normalize(tangent * lensTangentNormal.x + bitangent * lensTangentNormal.y + geometricNormal * lensTangentNormal.z)
        : geometricNormal;
    float3 outerLensNormal = diagnostic < 0.5
        ? normalize(tangent * outerLensTangentNormal.x + bitangent * outerLensTangentNormal.y + geometricNormal * outerLensTangentNormal.z)
        : geometricNormal;

    float3 albedo = diffuseSample.rgb;
    float3 scalpDiffuseAlbedo = 0;
    float3 scatteringAlbedo = albedo;
    float4 surfaceSelector = 0;
    float alpha = 1;
    float transmissionFactor = 1;

    if (diagnostic > 0.5)
    {
        albedo = BaseColor.rgb;
    }
    else if (family == 14)
    {
        // Both independently recovered PROShort01 masters use Opac_M01.R
        // against Material::OpacityMaskClipValue (0.15). The texture alpha
        // and remaining colour channels are not part of coverage.
        clip(maskSample.r - 0.15);
        albedo = diffuseSample.rgb;
    }
    else if (family == 15)
    {
        // Literal ALN colour graph recovered from the base and direct-light
        // permutations. ALN_HED_Tint selects nested muzzle/teeth/skin colours;
        // ALN_HED_Tatt is dotted with Tattoo_Chooser for a discrete marking.
        float3 tint = saturate(maskSample.rgb);
        float3 inverseTint = 1 - tint.xzy;
        float3 regionColour = inverseTint.x * SecondaryColor.rgb;
        regionColour = inverseTint.y * regionColour + tint.b * TertiaryColor.rgb;
        regionColour = inverseTint.z * regionColour + tint.g * QuaternaryColor.rgb;
        regionColour = tint.r * BaseColor.rgb + regionColour;
        float tattooSelector = TextureFlags0.w > 0.5
            ? saturate(dot(detailSample.rgb, FreckleRedColor.rgb))
            : 0;
        regionColour = lerp(regionColour, FreckleGreenColor.rgb, tattooSelector);
        albedo = diffuseSample.rgb * max(regionColour, 0);
        scatteringAlbedo = albedo;
        surfaceSelector = float4(tint, tattooSelector);
        transmissionFactor = 1;
    }
    else if (family == 16)
    {
        albedo = diffuseSample.rgb * max(BaseColor.rgb, 0);
    }
    else if (family == 1)
    {
        // LE1/LE2 HMM and HMF base passes consume SkinTone directly, like LE3.
        // The female graph's 3.5 gain belongs only to its lip-colour branch.
        float3 skinTint = max(BaseColor.rgb, 0);
        // HED_TMis controls the direct-light transmission branch in LE1; it
        // never disables SkinTone in the base diffuse branch.
        albedo = diffuseSample.rgb * skinTint;
        transmissionFactor = (le3 || HumanParameters.y > 0.5 ? diffuseSample.b : diffuseSample.a)
            * max(GeneralParameters.x, 0);

        if (TextureFlags1.x > 0.5)
        {
            float3 freckleSample = AuxiliaryTexture1.Sample(MaterialSampler, materialUv).rgb;
            float redFreckle = saturate((1 - freckleSample.r) * SkinParameters2.x);
            float greenFreckle = saturate((1 - freckleSample.g) * SkinParameters2.y);
            albedo = lerp(albedo, albedo * FreckleRedColor.rgb, redFreckle);
            if (!femaleFace)
            {
                float blueFreckle = saturate((1 - freckleSample.b) * SkinParameters2.z);
                albedo = lerp(albedo, FreckleBlueColor.rgb, blueFreckle);
            }
            albedo = lerp(albedo, FreckleGreenColor.rgb, greenFreckle);
        }

        if (TextureFlags2.x > 0.5)
        {
            // PROCustom HED_Brow uses the same two addition colours as the
            // ordinary face graph. A selected custom scar suppresses those
            // brow layers beneath its alpha coverage.
            float scarExclusion = TextureFlags2.y > 0.5
                ? saturate(1 - customScarSample.a * CustomSkinParameters.x)
                : 1;
            float primaryBrow = saturate(
                customBrowSample.a * SkinParameters1.x * scarExclusion);
            albedo = lerp(albedo, SecondaryColor.rgb, primaryBrow);
            float secondaryBrow = saturate(
                customBrowSample.a * (2 - 2 * customBrowSample.b)
                * SkinParameters1.y * scarExclusion);
            albedo = lerp(albedo, BlondeColor.rgb, secondaryBrow);
        }

        if (femaleFace)
        {
            float4 makeupSample = TextureFlags1.z > 0.5
                ? AuxiliaryTexture3.Sample(MaterialSampler, materialUv)
                : 0;
            float3 makeupBase = saturate(diffuseSample.rgb * 1.5);
            albedo = lerp(
                albedo,
                makeupBase * TertiaryColor.rgb,
                saturate(makeupSample.r * SkinParameters0.z));
            albedo = lerp(
                albedo,
                makeupBase * QuaternaryColor.rgb,
                saturate(makeupSample.g * makeupSample.b * SkinParameters0.w));
            float lipMask = saturate(
                (1 - makeupSample.b) * makeupSample.a * GeneralParameters.z);
            albedo = lerp(
                albedo,
                diffuseSample.g * 3.5 * FreckleBlueColor.rgb,
                lipMask);

            if (TextureFlags0.w > 0.5 && TextureFlags2.x < 0.5)
            {
                float primaryAddition = saturate(detailSample.a * SkinParameters1.x);
                albedo = lerp(albedo, SecondaryColor.rgb, primaryAddition);
                float secondaryAddition = saturate(
                    detailSample.a * (2 - 2 * detailSample.b) * SkinParameters1.y);
                albedo = lerp(albedo, BlondeColor.rgb, secondaryAddition);
            }

            // HMF_Makeup.B * HMF_Makeup.A selects a multiplicative blush
            // colour. HED_Blush_Scalar blends that factor back toward one.
            // This is the exact ordering emitted by the HMF base pass: blush
            // follows brow, eyeshadow, lips, and both Addn colour layers.
            float blushMask = makeupSample.b * makeupSample.a;
            float3 blushFactor = lerp(1, BlushColor.rgb, blushMask);
            albedo = lerp(albedo, albedo * blushFactor, HumanParameters.x);
        }
        else if (TextureFlags0.w > 0.5)
        {
            // Exact ordering recovered from the LE1 base-pass permutation.
            // Alpha selects the primary Addn colour; inverse packed blue
            // selects the secondary `blonde` colour.
            float primaryAddition = saturate(
                (TextureFlags2.x > 0.5 ? 1 : faceSelector)
                * detailSample.a * SkinParameters1.x);
            albedo = lerp(albedo, SecondaryColor.rgb, primaryAddition);

            float secondaryAddition = saturate(
                (TextureFlags2.x > 0.5 ? 1 : faceSelector)
                * (2 - 2 * detailSample.b) * SkinParameters1.y);
            albedo = lerp(albedo, BlondeColor.rgb, secondaryAddition);
        }

        if (TextureFlags2.y > 0.5)
        {
            // The custom master selects scar diffuse from inverse packed blue;
            // alpha is reserved for the independently controlled normal blend.
            float scarDiffuseMask = saturate(
                (2 - 2 * customScarSample.b) * CustomSkinParameters.y);
            albedo = lerp(albedo, CustomScarColor.rgb, scarDiffuseMask);
        }
        else if (!femaleFace)
        {
            float scarMask = faceSelector
                * saturate(1 - maskSample.a * GeneralParameters.z)
                * detailSample.a
                * GeneralParameters.w;
            albedo = lerp(albedo, QuaternaryColor.rgb, saturate(scarMask));

            if (!le3)
            {
                // HED_Addn_Add and HED_Brow_FadeOut form the LE1/LE2 mask which
                // is finally applied by HED_Addn_Multiply. All three controls
                // are compiled out of the LE3 face master.
                float inverseAddition = 1 - faceSelector * detailSample.a;
                float maskedAddition = maskSample.a * GeneralParameters.z;
                float multiplyBase = max(
                    inverseAddition * SkinParameters0.z + maskedAddition,
                    1) - inverseAddition * SkinParameters0.z;
                float multiplyMask = saturate(
                    maskedAddition * SkinParameters1.z * multiplyBase
                    + inverseAddition * SkinParameters0.z);
                albedo *= lerp(1, multiplyMask, SkinParameters0.w);
            }
        }
    }
    else if (family == 6)
    {
        float4 makeup = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : 0;
        float3 skinOrTeeth = lerp(BaseColor.rgb, EyeEmissiveColor.rgb, makeup.b);
        float3 baseDiffuse = diffuseSample.rgb * skinOrTeeth;
        float highDiffuseGain = saturate(diffuseSample.b * 2.5);
        float lowDiffuseGain = saturate(diffuseSample.b * 1.5);

        // This is the ordering compiled by the ASA base pass. Makeup RGB are
        // selectors, while the blender vector controls their combined branch.
        float3 eyeLayer = lerp(
            baseDiffuse,
            highDiffuseGain * makeup.g * FreckleRedColor.rgb,
            makeup.g);
        float3 lipColour = (1 - makeup.r) + makeup.r * FreckleGreenColor.rgb;
        float3 makeupLayer = lerp(eyeLayer, highDiffuseGain * lipColour, makeup.r);
        float makeupDot = dot(makeup.rgb, FreckleBlueColor.rgb);
        float makeupStrength = makeupDot * SkinParameters0.z;
        albedo = lerp(baseDiffuse, makeupLayer, makeupStrength);

        float secondaryStrength = makeup.a * (1 - makeupDot) * GeneralParameters.z;
        albedo = lerp(
            albedo,
            lowDiffuseGain * SecondaryColor.rgb,
            secondaryStrength);
        transmissionFactor = diffuseSample.a * max(GeneralParameters.x, 0);

        float additionMask = TextureFlags0.z > 0.5
            ? saturate(dot(maskSample.rgb, BlondeColor.rgb)
                + maskSample.a * SkinParameters0.x)
            : 1;
        if (TextureFlags0.w > 0.5)
        {
            // The compiled sample swizzle is R,G,A: RG perturb the normal and
            // alpha gates the complexion colour.
            float addition = additionMask * detailSample.a * SkinParameters0.y;
            float3 additionColour = saturate(diffuseSample.b * 2) * TertiaryColor.rgb;
            albedo = lerp(albedo, additionColour, addition);
        }

        if (TextureFlags1.y > 0.5 && TextureFlags0.z > 0.5)
        {
            // ASA_HED_Tatt is a channel-packed pattern selector. Its RGB must
            // only determine coverage; ASA_HED_Tatt_Colour is the sole tattoo
            // colour. Treating the texture as colour caused the raw red/green
            // stripes and black crest reported during visual validation.
            float3 tattooPattern = AuxiliaryTexture2.Sample(MaterialSampler, materialUv).rgb;
            float primaryCoverage = dot(maskSample.rgb, SkinParameters2.rgb)
                + maskSample.a * SkinParameters1.x;
            float secondaryCoverage = dot(maskSample.rgb, SkinParameters3.rgb)
                + maskSample.a * SkinParameters1.y;
            float primaryPattern = dot(tattooPattern, ScalpParameters0.rgb);
            float secondaryPattern = dot(tattooPattern, ScalpParameters1.rgb);
            float tattooMask = saturate(
                primaryCoverage * primaryPattern
                + secondaryCoverage * secondaryPattern)
                * SkinParameters1.z;
            float3 tattooColour = saturate(diffuseSample.b * 2) * QuaternaryColor.rgb;
            albedo = lerp(albedo, tattooColour, tattooMask);
        }

        if (TextureFlags1.z > 0.5 && TextureFlags1.w > 0.5)
        {
            float3 skinNoise = AuxiliaryTexture3.Sample(MaterialSampler, materialUv * 1.5).rgb;
            float noiseSelector = AuxiliaryTexture4.Sample(MaterialSampler, materialUv).g;
            albedo *= lerp(skinNoise, 1 - skinNoise, noiseSelector);
        }

        // Mask controls the compiled opacity clip through the blue makeup
        // selector even though the material itself is classified as opaque.
        float coverage = (1 - makeup.b) + ScalpParameters2.x * makeup.b;
        clip(coverage - 0.3333);
    }
    else if (family == 7)
    {
        float4 tint = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : float4(1, 1, 1, 1);
        float additionMask = TextureFlags0.z > 0.5
            ? saturate(dot(maskSample.rgb, FreckleRedColor.rgb)
                + maskSample.a * GeneralParameters.x)
            : 1;
        float3 baseDiffuse = diffuseSample.rgb * BaseColor.rgb;

        // SAL_HED_Addn is sampled as R/G/A. RG supplies the normal above;
        // alpha blends toward Addn_Colour after the tint mask's authored gain.
        if (TextureFlags0.w > 0.5)
        {
            float additionStrength = saturate(
                additionMask * detailSample.a * GeneralParameters.y);
            float3 additionColour = saturate(tint.a * 1.85) * SecondaryColor.rgb;
            baseDiffuse = lerp(baseDiffuse, additionColour, additionStrength);
        }

        float secondaryStrength = saturate(
            (1 - additionMask * detailSample.a) * tint.g * GeneralParameters.z);
        float3 secondaryColour = saturate(diffuseSample.b * 1.5) * TertiaryColor.rgb;
        albedo = lerp(baseDiffuse, secondaryColour, secondaryStrength);

        if (TextureFlags1.y > 0.5 && TextureFlags0.z > 0.5)
        {
            float3 tattooPattern = AuxiliaryTexture2.Sample(MaterialSampler, materialUv).rgb;
            float primaryCoverage = dot(maskSample.rgb, SkinParameters1.rgb)
                + maskSample.a * SkinParameters0.x;
            float secondaryCoverage = dot(maskSample.rgb, SkinParameters2.rgb)
                + maskSample.a * SkinParameters0.y;
            float primaryPattern = dot(tattooPattern, SkinParameters3.rgb);
            float secondaryPattern = dot(tattooPattern, ScalpParameters0.rgb);
            float tattooMask = saturate(
                primaryCoverage * primaryPattern
                + secondaryCoverage * secondaryPattern);
            float3 tattooColour = saturate(diffuseSample.b * 2) * QuaternaryColor.rgb;
            albedo = lerp(albedo, tattooColour, tattooMask);
        }

        // The compiled direct-light path uses inverse tint green as the
        // localized transmission/thickness selector.
        transmissionFactor = saturate(1 - tint.g);
    }
    else if (family == 8)
    {
        // SAL_HED_EYE_Spec packs emissive/specular power in red, the
        // iris/pupil selector in blue, and pupil strength in alpha.
        float irisSelector = saturate(maskSample.b);
        float3 iris = diffuseSample.rgb * BaseColor.rgb * irisSelector;
        float3 pupil = SecondaryColor.rgb * (1 - irisSelector) * maskSample.a;
        albedo = iris + pupil;
    }
    else if (family == 9)
    {
        float4 tint = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : 0;
        if (diagnostic < 0.5)
        {
            // The compiled opacity mask is local to Tint.G (the teeth region):
            // (1 - G) + Mask * G, clipped against the literal 0.3333 value.
            float coverage = (1 - tint.g) + SkinParameters0.z * tint.g;
            clip(coverage - 0.3333);
        }
        surfaceSelector = tint;
        // Literal TUR base-pass ordering. Red selects skin, green teeth, blue
        // sockets, and the remaining unselected region receives the bone tint.
        float3 bone = diffuseSample.rgb * BlondeColor.rgb;
        float3 socket = diffuseSample.rgb * EyeEmissiveColor.rgb;
        float3 teeth = diffuseSample.rgb * ScalpParameters1.rgb;
        float3 skin = diffuseSample.rgb * BaseColor.rgb;
        float3 hardSurface = (1 - tint.b) * (1 - tint.r) * bone + tint.b * socket;
        hardSurface = (1 - tint.g) * hardSurface + tint.g * teeth;
        albedo = hardSurface + tint.r * skin;

        // Alpha does not identify bone. It gates the secondary diffuse colour,
        // whose compiled gain is saturate(Diff.b * 2.5).
        float secondaryStrength = saturate(tint.a * GeneralParameters.y);
        float3 secondaryDiffuse = saturate(diffuseSample.b * 2.5) * SecondaryColor.rgb;
        albedo = lerp(albedo, secondaryDiffuse, secondaryStrength);
        scatteringAlbedo = albedo;

        float additionMask = TextureFlags0.z > 0.5
            ? saturate(dot(maskSample.rgb, FreckleRedColor.rgb))
            : 1;
        if (TextureFlags0.w > 0.5)
        {
            float additionCoverage = saturate(additionMask * detailSample.a);
            albedo *= 1 - additionCoverage;
            albedo += additionCoverage * TertiaryColor.rgb;
        }

        if (TextureFlags1.y > 0.5 && TextureFlags0.z > 0.5)
        {
            float3 tattooPattern = AuxiliaryTexture2.Sample(MaterialSampler, materialUv).rgb;
            float primaryCoverage = dot(maskSample.rgb, SkinParameters1.rgb)
                + maskSample.a * SkinParameters0.x;
            float secondaryCoverage = dot(maskSample.rgb, SkinParameters2.rgb)
                + maskSample.a * SkinParameters0.y;
            float primaryPattern = dot(tattooPattern, SkinParameters3.rgb);
            float secondaryPattern = dot(tattooPattern, ScalpParameters0.rgb);
            float tattooMask = saturate(primaryCoverage * primaryPattern + secondaryCoverage * secondaryPattern);
            // Tattoo intensity comes from the raw diffuse-blue channel before
            // any bone/socket/teeth/skin tint composition. Using albedo blue
            // here incorrectly lets Bone Plate Tint recolour or black it out.
            float3 tattooColour = saturate(diffuseSample.b * 4) * QuaternaryColor.rgb;
            albedo = lerp(albedo, tattooColour, tattooMask);
        }
        transmissionFactor = max(GeneralParameters.x, 0);
    }
    else if (family == 10)
    {
        if (le3)
        {
            // The LE3 eye has no mask or pupil UV transform. Environment terms
            // are composed after the view vector is available below.
            albedo = diffuseSample.rgb * BaseColor.rgb;
        }
        else
        {
            // The LE1/LE2 base-pass bytecode multiplies the tinted diffuse by
            // Mask.R. The pupil parameter has already acted through UV scaling.
            albedo = diffuseSample.rgb * BaseColor.rgb * saturate(maskSample.r);
        }
    }
    else if (family == 11)
    {
        float4 tint = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : 0;
        float4 gradientSelectors = TextureFlags1.y > 0.5
            ? AuxiliaryTexture2.Sample(MaterialSampler, materialUv)
            : 0;
        surfaceSelector = gradientSelectors;

        // KRO_HED_Tint.R is used twice by the compiled base pass: it removes
        // SkinTone and independently introduces Helmet_Tint.
        float3 skinTint = lerp(BaseColor.rgb, 1, tint.r);
        float3 helmetTint = lerp(1, SecondaryColor.rgb, tint.r);
        albedo = diffuseSample.rgb * skinTint * helmetTint;

        float3 gainedDiffuse = saturate(diffuseSample.rgb * 2.75);
        albedo = lerp(
            albedo,
            gainedDiffuse * TertiaryColor.rgb,
            gradientSelectors.g * GeneralParameters.x);
        albedo = lerp(
            albedo,
            gainedDiffuse * QuaternaryColor.rgb,
            gradientSelectors.r * GeneralParameters.y);
        albedo = lerp(
            albedo,
            gainedDiffuse * FreckleBlueColor.rgb,
            gradientSelectors.b * GeneralParameters.z);

        float complexionMask = TextureFlags0.z > 0.5
            ? min(dot(maskSample.rgb, FreckleRedColor.rgb), 1)
            : 1;
        float complexionCoverage = complexionMask
            * detailSample.a
            * GeneralParameters.w;
        albedo = lerp(albedo, FreckleGreenColor.rgb, complexionCoverage);

        float3 teethColour = saturate(diffuseSample.r * 1.85) * BlondeColor.rgb;
        albedo = lerp(albedo, teethColour, tint.b);
        scatteringAlbedo = albedo;
        transmissionFactor = max(SkinParameters0.w, 0);
    }
    else if (family == 13)
    {
        float4 gradientSelectors = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : 0;
        float4 packedSpec = TextureFlags1.y > 0.5
            ? AuxiliaryTexture2.Sample(MaterialSampler, materialUv)
            : 0;
        surfaceSelector = packedSpec;
        float3 baseTint = lerp(BaseColor.rgb, SecondaryColor.rgb, packedSpec.b);
        float complexionMask = TextureFlags0.z > 0.5
            ? min(dot(maskSample.rgb, FreckleBlueColor.rgb), 1)
            : 1;
        float additionCoverage = complexionMask * detailSample.a * SkinParameters0.y;

        if (SurfaceParameters.w > 0.5)
        {
            // LE2 and LE3 compile this same post-composition diffuse chain;
            // their executable difference is confined to the normal path.
            float gain = min(SkinParameters0.x, 1);
            albedo = baseTint;
            albedo = lerp(albedo, gain * TertiaryColor.rgb,
                gradientSelectors.g * GeneralParameters.x);
            albedo = lerp(albedo, gain * QuaternaryColor.rgb,
                gradientSelectors.r * GeneralParameters.y);
            albedo = lerp(albedo, gain * FreckleRedColor.rgb,
                gradientSelectors.b * GeneralParameters.z);
            float3 additionTarget = lerp(
                FreckleGreenColor.rgb,
                gain * FreckleGreenColor.rgb,
                GeneralParameters.w);
            albedo = lerp(albedo, additionTarget, additionCoverage);
            albedo *= diffuseSample.rgb;
        }
        else
        {
            float3 gainedDiffuse = saturate(diffuseSample.rgb * SkinParameters0.x);
            albedo = diffuseSample.rgb * baseTint;
            albedo = lerp(albedo, gainedDiffuse * TertiaryColor.rgb,
                gradientSelectors.g * GeneralParameters.x);
            albedo = lerp(albedo, gainedDiffuse * QuaternaryColor.rgb,
                gradientSelectors.r * GeneralParameters.y);
            albedo = lerp(albedo, gainedDiffuse * FreckleRedColor.rgb,
                gradientSelectors.b * GeneralParameters.z);
            float3 additionTarget = lerp(
                FreckleGreenColor.rgb,
                gainedDiffuse * FreckleGreenColor.rgb,
                GeneralParameters.w);
            albedo = lerp(albedo, additionTarget, additionCoverage);
        }
        scatteringAlbedo = albedo;
        transmissionFactor = max(SkinParameters0.w, 0);
    }
    else if (family == 12)
    {
        // The diffuse/mask/pupil arithmetic is instruction-equivalent across
        // the inspected LE1/LE2/LE3 streams. LE3 independently changes both
        // normals to stored DXT5 RGB and binds Cube_Chrome in place of the
        // earlier Metal_Cube; those paths are selected above and below.
        float3 le1Eye = diffuseSample.rgb * BaseColor.rgb * saturate(maskSample.r);
        float3 le2Eye = diffuseSample.rgb * BaseColor.rgb * saturate(maskSample.r);
        albedo = SurfaceParameters.w > 0.5 ? le2Eye : le1Eye;
    }
    else if (family == 2)
    {
        // HED_Teeth_Diff is a packed selector in the original LE1 shader:
        // red drives masked coverage and green isolates teeth colour.
        float4 teethSample = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : float4(1, 0, 0, 1);
        if (le3)
        {
            // Literal LE3 base-pass composition. The compiled sample writes
            // t1.y to r0.x via the resource swizzle t1.yxzw, so the teeth
            // selector is green. It bypasses both skin tint and specular/hair
            // tinting; scalp-spec RGB blends between SkinTone and HairColor^2.
            float teeth = saturate(teethSample.g);
            CcScalpMasks ccScalpMasks;
            float3 packedSpec = saturate(maskSample.rgb);
            if (IsCcScalpPreview())
            {
                ccScalpMasks = EvaluateCcScalpMasks(maskSample);
                packedSpec = saturate(ccScalpMasks.Mask);
            }
            float3 scalp = teeth
                + (1 - teeth) * max(BaseColor.rgb, 0) * (1 - packedSpec)
                + packedSpec * max(SecondaryColor.rgb * SecondaryColor.rgb, 0);
            albedo = diffuseSample.rgb * scalp;
            scalpDiffuseAlbedo = albedo;
            if (IsCcScalpPreview())
            {
                // CC's base pass includes two fibre lobes and a component-wise
                // blend into the buzzcut layer. Keep this independent of opacity.
                float3 firstFibre, secondFibre;
                CcScalpFibreDirections(normal, tangent, bitangent, detailSample.rgb,
                    materialUv, firstFibre, secondFibre);
                float3 ccView = normalize(CameraPosition.xyz - input.WorldPosition);
                float3 ambientHalf = normalize(ccView + geometricNormal);
                float hairLevel = SecondaryColor.r;
                float primaryPower = hairLevel <= 0.09 ? 400 : (hairLevel < 0.4 ? 200 : 100);
                float secondaryPower = hairLevel <= 0.09 ? 30 : 15;
                float primaryGain = hairLevel < 0.0125 ? 20
                    : hairLevel <= 0.09 ? 2.5
                    : hairLevel < 0.4 ? 2 : 1.25;
                float secondaryGain = hairLevel < 0.0125 ? 8
                    : hairLevel <= 0.09 ? 1 : hairLevel < 0.4 ? 1.5 : 1;
                float3 diffuseMask = diffuseSample.rgb * ccScalpMasks.Mask;
                float3 hairBase = diffuseMask * (
                        FibreLobe(firstFibre, ambientHalf, primaryPower) * saturate(SecondaryColor.rgb * primaryGain)
                        + FibreLobe(secondFibre, ambientHalf, secondaryPower) * saturate(SecondaryColor.rgb * secondaryGain))
                    * (hairLevel <= 0.09 ? 1 : 0.5)
                    + 1.5 * max(abs(diffuseMask), 0.01) * (hairLevel <= 0.09 ? 0.5 : 0.25) * SecondaryColor.rgb;
                float3 maskedHair = maskSample.rgb * hairBase;
                float3 skinBase = diffuseSample.rgb
                    * (teeth + (1 - teeth) * BaseColor.rgb * ccScalpMasks.Overlay);
                albedo = lerp(skinBase + maskedHair, maskedHair, ccScalpMasks.Buzzcut);
                float3 diffuseHair = maskSample.rgb * (1.5 * max(abs(diffuseMask), 0.01)
                    * (hairLevel <= 0.09 ? 0.5 : 0.25) * SecondaryColor.rgb);
                scalpDiffuseAlbedo = lerp(skinBase + diffuseHair, diffuseHair, ccScalpMasks.Buzzcut);
            }
            transmissionFactor = diffuseSample.b * max(GeneralParameters.x, 0);
        }
        else
        {
            LegacyScalpLobes lobes = LegacyScalpDirections(geometricNormal, tangent, bitangent, materialUv);
            float3 scalpView = normalize(CameraPosition.xyz - input.WorldPosition);
            albedo = LegacyScalpAlbedo(diffuseSample.rgb, maskSample.rgb, teethSample.g,
                lobes, normalize(scalpView + geometricNormal), scalpDiffuseAlbedo);
            if (SurfaceParameters.w > 0)
            {
                float coverage = lerp(teethSample.r, 1, saturate(ScalpParameters1.x));
                clip(coverage - 0.3333);
            }
        }
    }
    else if (family == 3)
    {
        float3 tintedWhite = diffuseSample.rgb * max(SecondaryColor.rgb * 1.45, 0.08);
        float3 eyeWhite = 0.5 * (tintedWhite + Luminance(tintedWhite));
        // Iris_Colour_Multiplier is an ordinary multiplier on the authored
        // linear iris colour. Do not add an editor-side gain: the previous
        // x3.2 factor regularly saturated amber and pale eyes.
        float3 iris = diffuseSample.rgb * max(BaseColor.rgb, 0);
        if (EyeParameters.w > 0.5)
        {
            // Packed red selects iris colour and its inverse selects sclera
            // colour. Green has a separate job above: its inverse blends the
            // iris and lens normals. The compiled shader's yxyy swizzle produces
            // both 1-green and 1-red; do not conflate those two temporaries.
            float irisMask = saturate(maskSample.r);
            float scleraMask = saturate(1 - maskSample.r);
            iris *= max(EyeParameters1.w, 0);
            albedo = saturate(
                iris * irisMask
                + eyeWhite * scleraMask * max(EyeParameters2.z, 0));
        }
        else
        {
            albedo = lerp(eyeWhite, iris, saturate(maskSample.r));
        }
        albedo = lerp(albedo, QuaternaryColor.rgb, saturate(EyeParameters.z));
    }
    else if (family == 4)
    {
        // The LE1 lash master is unlit. Its colour/spec parameters are present
        // in the graph but optimized out; HED_Lash_Diff.r alone drives alpha.
        albedo = 0;
        alpha = saturate(diffuseSample.r * SkinParameters3.x);
    }
    else if (family == 5)
    {
        if (IsHumanHairPreview())
        {
            float4 hairStack = TextureFlags1.x > 0.5
                ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
                : float4(0, 0, 0, 1);
            // The James master is driven by its packed stack texture. Its
            // direct-light branch supplies diffuse and both anisotropic lobes
            // through EvaluateSpecular; alpha remains the translucent card.
            albedo = 0;
            alpha = hairStack.a;
        }
        else
        {
            // The LE1 hair master treats HAIR_Diff as a packed map: green is the
            // diffuse intensity and alpha is opacity. The direct-light FXC uses the
            // gained HairColour product as its diffuse term; the brighter half-
            // green blend belongs only to the two anisotropic highlight lobes.
            bool additionalHair = SkinParameters3.z > 0.5;
            bool layeredHair = SkinParameters3.w > 0.5;
            float hairDiffuseGain = BaseColor.r >= 0.09 ? 0.25 : 0.5;
            albedo = abs(diffuseSample.g)
                * (layeredHair ? 1 : hairDiffuseGain)
                * (additionalHair ? max(BaseColor.rgb * BaseColor.rgb, 0) : max(BaseColor.rgb, 0));
            // DXT1 textures naturally decode to alpha=1; substituting luminance
            // made them incorrectly semi-transparent.
            alpha = diffuseSample.a;
        }
        if (SurfaceParameters.x < 0)
        {
            // Hair depth-only pass. The renderer supplies 1.0 for UE3's lit
            // translucency ScreenDoor prepass and 1/255 for its post-render
            // depth pass; shadow alpha testing is a separate preview pass.
            clip(alpha + SurfaceParameters.x);
            return 0;
        }
    }

    float3 view = normalize(CameraPosition.xyz - input.WorldPosition);
    float3 ambientDiffuseAlbedo = family == 2 && diagnostic < 0.5 ? scalpDiffuseAlbedo : albedo;
    float3 eyeBaseReflection = 0;
    if (family == 3 && diagnostic < 0.5)
    {
        // Human and Asari eye masters carry two fixed cube expressions in all
        // three games (Metal/Chrome and EyeReflection). They are independent
        // of the morph-instance 2D parameters and must be sampled explicitly;
        // directional Phong lobes alone leave the cornea looking like plastic.
        float3 reflectedIrisView = reflect(-view, normal);
        float3 reflectedLensView = reflect(-view, lensNormal);
        float3 primaryCube = TextureFlags1.y > 0.5
            ? FixedCubeTexture.Sample(MaterialSampler, reflectedIrisView).rgb
            : 0;
        float3 secondaryCube = TextureFlags1.z > 0.5
            ? SecondaryFixedCubeTexture.Sample(MaterialSampler, reflectedLensView).rgb
            : 0;
        float irisFacing = 1 - pow(
            max(1 - saturate(dot(normal, view)), 0.0001),
            0.35);
        float lensFacing = 1 - pow(
            max(1 - saturate(dot(lensNormal, view)), 0.0001),
            0.35);
        eyeBaseReflection =
            primaryCube * primaryCube * (0.1 * irisFacing * max(EyeParameters2.x, 0))
            + secondaryCube * (0.25 * lensFacing * max(EyeParameters2.y, 0));
    }
    if (family == 10 && le3 && diagnostic < 0.5)
    {
        // Literal LE3 Turian eye base response. EYE_Diff/EYE_Tint and the
        // parameter CubeMap are multiplied by squared normal-map N.V. The fixed
        // Chrome cube is squared and scaled by the geometric view-facing term.
        float3 reflectedView = reflect(-view, normal);
        float3 chrome = TextureFlags1.x > 0.5
            ? FixedCubeTexture.Sample(MaterialSampler, reflectedView).rgb
            : 0;
        float3 eyeCube = TextureFlags1.y > 0.5
            ? SecondaryFixedCubeTexture.Sample(MaterialSampler, reflectedView).rgb
            : 0;
        float mappedFacing = saturate(dot(normal, view));
        float geometricFacing = saturate(dot(geometricNormal, view));
        float chromeFactor = 0.1 * (1 - pow(
            max(1 - geometricFacing, 0.0001),
            0.35));
        ambientDiffuseAlbedo *= mappedFacing * mappedFacing;
        albedo = mappedFacing * mappedFacing
                * (albedo + max(GeneralParameters.x, 0) * eyeCube)
            + chrome * chrome * chromeFactor;
    }
    if (family == 8 && diagnostic < 0.5)
    {
        // Both LE2 and LE3 SAL eye permutations use the same two fixed cubes:
        // squared Cube_Chrome at 0.1 and Cube_Visor luminance at 0.25, under
        // their shared pow(0.35) view-facing term.
        float3 reflectedView = reflect(-view, normal);
        float3 chrome = TextureFlags1.x > 0.5
            ? FixedCubeTexture.Sample(MaterialSampler, reflectedView).rgb
            : 0;
        float3 visor = TextureFlags1.y > 0.5
            ? SecondaryFixedCubeTexture.Sample(MaterialSampler, reflectedView).rgb
            : 0;
        float facing = 1 - pow(
            max(1 - saturate(dot(normal, view)), 0.0001),
            0.35);
        eyeBaseReflection = facing * (
            0.1 * chrome * chrome
            + 0.25 * dot(visor, float3(0.3, 0.59, 0.11)));
    }
    if (family == 12 && diagnostic < 0.5)
    {
        float4 packedSpec = TextureFlags0.w > 0.5 ? detailSample : 1;
        float3 reflectedView = reflect(-view, normal);
        float3 cubeLe1 = TextureFlags1.y > 0.5
            ? FixedCubeTexture.Sample(MaterialSampler, reflectedView).rgb
            : 0;
        float3 cubeLe2 = TextureFlags1.y > 0.5
            ? FixedCubeTexture.Sample(MaterialSampler, reflectedView).rgb
            : 0;
        // All inspected permutations compile the same cube equation. Keep the
        // older game selection explicit while the resolved package binding
        // supplies Metal_Cube for LE1/LE2 and Cube_Chrome for LE3.
        float3 cube = SurfaceParameters.w > 0.5 ? cubeLe2 : cubeLe1;
        float grazing = 1 - pow(
            max(1 - saturate(dot(normal, view)), 0.0001),
            0.35);
        float3 cubeLobe = cube * cube * (0.1 * grazing);
        float lensLobe = pow(
            max(saturate(dot(lensNormal, view)), 0.0001),
            500)
            * (packedSpec.b * 2)
            * 0.45;
        // The no-light-map base pass multiplies the same cube/lens sum by the
        // engine ambient colour. Directional permutations add an N.L-scaled
        // copy per light in EvaluateSpecular.
        eyeBaseReflection = (cubeLobe + lensLobe) * AmbientColor.rgb;
    }
    // CPU-side camera parenting converts the rig into world-space directions
    // for this frame. Orbiting therefore rotates the rig around the head.
    float3 keyLight = normalize(KeyLightDirection.xyz);
    float3 shadowedKeyColor = KeyLightColor.rgb * KeyShadowVisibility(input.WorldPosition, geometricNormal);
    float3 fillLight = normalize(FillLightDirection.xyz);
    float3 rimLight = normalize(RimLightDirection.xyz);
    // A compact UE3-era environment model: soft-edged directional sources,
    // a sky/ground hemisphere, a horizon band, and a grazing ambient response.
    // It remains deterministic and cheap enough for interactive morph editing,
    // but avoids the flat "three points plus a constant" result.
    float keyDiffuse = saturate(dot(normal, keyLight));
    float fillDiffuse = saturate(dot(normal, fillLight));
    float rimDiffuse = saturate(dot(normal, rimLight));
    float hemisphere = normal.y * 0.5 + 0.5;
    float horizon = pow(saturate(1 - abs(normal.y)), lerp(4, 2, saturate(LightingParameters.y * 8)));
    float grazingAmbient = pow(1 - saturate(dot(normal, view)), 4) * LightingParameters.z;
    float3 shapedEnvironment = (
        lerp(GroundColor.rgb, SkyColor.rgb, hemisphere)
        + HorizonColor.rgb * horizon * 0.25
        + SkyColor.rgb * grazingAmbient) * EnvironmentParameters.x;
    // Preserve each established preset's average ambient level while shaping
    // it spatially; this keeps authored UE3 material responses from being
    // crushed by a wholesale relight.
    float3 environmentLighting = lerp(AmbientColor.rgb, shapedEnvironment, 0.1);
    float3 diffuseLighting = environmentLighting
        + shadowedKeyColor * keyDiffuse
        + FillLightColor.rgb * fillDiffuse
        + RimLightColor.rgb * rimDiffuse;
    float3 specularLighting =
        EvaluateSpecular(family, normal, lensNormal, outerLensNormal, tangent, bitangent, view, keyLight, diffuseSample, maskSample, detailSample, faceSelector, albedo, materialUv)
            * shadowedKeyColor
        + EvaluateSpecular(family, normal, lensNormal, outerLensNormal, tangent, bitangent, view, fillLight, diffuseSample, maskSample, detailSample, faceSelector, albedo, materialUv)
            * FillLightColor.rgb
        + EvaluateSpecular(family, normal, lensNormal, outerLensNormal, tangent, bitangent, view, rimLight, diffuseSample, maskSample, detailSample, faceSelector, albedo, materialUv)
            * RimLightColor.rgb;
    float3 subsurfaceLighting =
        EvaluateSubsurfaceLight(family, le3, normal, view, keyLight, diffuseSample, albedo, scatteringAlbedo, surfaceSelector, transmissionFactor)
            * shadowedKeyColor
        + EvaluateSubsurfaceLight(family, le3, normal, view, fillLight, diffuseSample, albedo, scatteringAlbedo, surfaceSelector, transmissionFactor)
            * FillLightColor.rgb
        + EvaluateSubsurfaceLight(family, le3, normal, view, rimLight, diffuseSample, albedo, scatteringAlbedo, surfaceSelector, transmissionFactor)
            * RimLightColor.rgb;

    float3 lit = (albedo * diffuseLighting + specularLighting + subsurfaceLighting) * LightingParameters.x
        + eyeBaseReflection;
    float3 ambientDiffuse = ambientDiffuseAlbedo * environmentLighting;

    if (family == 2 && !le3 && diagnostic < 0.5)
    {
        float teeth = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv).g : 0;
        lit = (albedo * environmentLighting
            + LegacyScalpDirectLight(normal, geometricNormal, tangent, bitangent, view, keyLight,
                diffuseSample, maskSample.rgb, teeth, materialUv) * shadowedKeyColor
            + LegacyScalpDirectLight(normal, geometricNormal, tangent, bitangent, view, fillLight,
                diffuseSample, maskSample.rgb, teeth, materialUv) * FillLightColor.rgb
            + LegacyScalpDirectLight(normal, geometricNormal, tangent, bitangent, view, rimLight,
                diffuseSample, maskSample.rgb, teeth, materialUv) * RimLightColor.rgb) * LightingParameters.x;
    }

    if (family == 5 && IsHumanHairPreview() && diagnostic < 0.5)
    {
        float4 hairStack = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv)
            : float4(0, 0, 0, 1);
        // The native base pass supplies the same stack response with a
        // view/normal half-vector, scaled by the engine ambient colour.
        lit += EvaluateHumanHairStack(hairStack, tangent, normalize(view + geometricNormal))
            * environmentLighting * LightingParameters.x;
    }

    if (family == 14 && diagnostic < 0.5)
    {
        // PROShort01 stores a tangent-space fibre direction in Tang.RG. Its
        // positive Z is reconstructed exactly like the SM3 bytecode; the
        // material never samples the similarly named _Norm asset.
        float2 fibreXY = detailSample.rg * 2 - 1;
        float tangentEnergy = dot(fibreXY, fibreXY);
        float3 tangentFibre = float3(
            fibreXY,
            sqrt(saturate(1 - tangentEnergy)));
        float3 fibreTangent = normalize(
            tangent * tangentFibre.x
            + bitangent * tangentFibre.y
            + geometricNormal * tangentFibre.z);
        float3 specularColour = TextureFlags1.x > 0.5
            ? AuxiliaryTexture1.Sample(MaterialSampler, materialUv).rgb
            : 0;

        // The no-light-map base permutation uses the view/normal half-vector
        // for a separate exponent-500 anisotropic lobe, then adds diffuse
        // scaled by the authored tangent XY energy.
        float3 ambientHalf = normalize(view + geometricNormal);
        float tangentAmbient = dot(fibreTangent, ambientHalf);
        float ambientSpecular = pow(
            max(sqrt(saturate(1 - tangentAmbient * tangentAmbient)), 0.0001),
            500);
        float3 maskedHairLighting = environmentLighting * (
            diffuseSample.rgb * tangentEnergy
            + specularColour * ambientSpecular);
        ambientDiffuse = diffuseSample.rgb * tangentEnergy * environmentLighting;
        maskedHairLighting += shadowedKeyColor * EvaluateMaskedHairLight(
            geometricNormal, fibreTangent, view, keyLight, diffuseSample.rgb, specularColour);
        maskedHairLighting += FillLightColor.rgb * EvaluateMaskedHairLight(
            geometricNormal, fibreTangent, view, fillLight, diffuseSample.rgb, specularColour);
        maskedHairLighting += RimLightColor.rgb * EvaluateMaskedHairLight(
            geometricNormal, fibreTangent, view, rimLight, diffuseSample.rgb, specularColour);
        lit = maskedHairLighting * LightingParameters.x;
    }

    if (LightingParameters.w > 0.5)
    {
        // UE3 unlit materials retain their authored diffuse/base result while
        // bypassing the scene's directional, environment, and specular lighting.
        lit = albedo;
        ambientDiffuse = 0;
    }

    if (family == 3 && EyeParameters.w > 0.5 && diagnostic < 0.5)
    {
        // LE2 multiplies emissive by EYE_Diff alpha, not EYE_Mask alpha.
        lit += max(EyeEmissiveColor.rgb, 0)
            * max(EyeParameters1.z, 0)
            * saturate(diffuseSample.a);
    }

    if (family == 8 && diagnostic < 0.5)
    {
        lit += max(GeneralParameters.x, 0) * saturate(maskSample.r);
    }

    if (family == 16 && diagnostic < 0.5)
    {
        // ALN_HED_Diff alpha is the eye emissive mask in the LE2 FXC.
        lit += max(SecondaryColor.rgb, 0)
            * max(GeneralParameters.z, 0)
            * saturate(diffuseSample.a);
    }

    if (family == 4 && diagnostic < 0.5)
    {
        lit = albedo;
        ambientDiffuse = 0;
    }

    if (diagnostic > 0.5)
    {
        lit = BaseColor.rgb * (environmentLighting + KeyLightColor.rgb * keyDiffuse
            + FillLightColor.rgb * fillDiffuse + RimLightColor.rgb * rimDiffuse) * LightingParameters.x;
        alpha = 1;
        ambientDiffuse = BaseColor.rgb * environmentLighting;
    }
    ambientDiffuseTarget = float4(ambientDiffuse, saturate(alpha));
    return float4(lit, saturate(alpha));
}

void PSShadow(VertexOutput input)
{
    // Reuse the material pass's coverage decisions, including masked scalp,
    // alien faces and PROShort01's 0.15 red clip. The compiler discards the
    // unused lighting result. Translucent cards retain their authored opacity
    // map with a 0.333 preview cutoff, independently of their camera-view
    // depth pre/post passes (continuous transparency needs a binary depth test).
    float4 unusedNormal;
    float4 unusedAmbient;
    float opacity = PSMain(input, true, unusedNormal, unusedAmbient).a;
    clip(opacity - 0.3333);
}

struct PostVertexOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

PostVertexOutput VSPostProcess(uint vertexId : SV_VertexID)
{
    PostVertexOutput output;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.TexCoord = uv;
    output.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return output;
}

static const int2 SsaoOffsets[12] =
{
    // Three view-space scales: contact, feature, and orbital-cavity range.
    int2( 3,  0), int2(-3,  0), int2( 0,  3), int2( 0, -3),
    int2( 7,  7), int2(-7,  7), int2( 7, -7), int2(-7, -7),
    int2(24,  5), int2(-24, -5), int2( 5, -24), int2(-5, 24)
};

int2 ClampPostPixel(int2 pixel)
{
    int2 dimensions = int2(1.0 / PixelSizeAndSamples.xy);
    return clamp(pixel, int2(0, 0), dimensions - 1);
}

float LoadMsaaDepth(int2 pixel)
{
    float depth = 1;
    [unroll]
    for (int sampleIndex = 0; sampleIndex < 4; ++sampleIndex)
    {
        if (sampleIndex < (int)PixelSizeAndSamples.z)
        {
            depth = min(depth, SceneDepthMsaaTexture.Load(ClampPostPixel(pixel), sampleIndex));
        }
    }
    return depth;
}

float LoadSingleDepth(int2 pixel)
{
    return SceneDepthSingleTexture.Load(int3(ClampPostPixel(pixel), 0)).r;
}

float3 ReconstructViewPosition(int2 pixel, float depth)
{
    float2 uv = (float2(ClampPostPixel(pixel)) + 0.5) * PixelSizeAndSamples.xy;
    float4 clip = float4(uv.x * 2 - 1, 1 - uv.y * 2, depth, 1);
    float4 view = mul(clip, InverseProjection);
    return view.xyz / max(abs(view.w), 0.000001);
}

float3 LoadMsaaAoNormal(int2 pixel)
{
    float3 normal = 0;
    float samples = 0;
    [unroll]
    for (int sampleIndex = 0; sampleIndex < 4; ++sampleIndex)
    {
        if (sampleIndex < (int)PixelSizeAndSamples.z)
        {
            normal += SceneNormalMsaaTexture.Load(ClampPostPixel(pixel), sampleIndex).rgb * 2 - 1;
            samples += 1;
        }
    }
    return normalize(normal / max(samples, 1));
}

float3 LoadSingleAoNormal(int2 pixel)
{
    return normalize(SceneNormalSingleTexture.Load(int3(ClampPostPixel(pixel), 0)).rgb * 2 - 1);
}

float EvaluateViewSpaceOccluder(float3 centerPosition, float3 normal, int2 samplePixel, float sampleDepth)
{
    if (sampleDepth >= 0.99999)
    {
        return 0;
    }
    float3 offset = ReconstructViewPosition(samplePixel, sampleDepth) - centerPosition;
    float distanceToSample = length(offset);
    if (distanceToSample <= 0.000001)
    {
        return 0;
    }
    // A coplanar or smoothly convex neighbour lies on/below the tangent plane
    // and contributes nothing. Only geometry rising into the normal hemisphere
    // can occlude, which prevents individual mesh triangles becoming visible.
    float horizon = saturate((dot(normal, offset / distanceToSample) - DepthParameters.y) /
                             max(1 - DepthParameters.y, 0.0001));
    float viewRadius = max(abs(centerPosition.z) * DepthParameters.x, 0.025);
    float rangeWeight = saturate(1 - distanceToSample / viewRadius);
    rangeWeight *= rangeWeight;
    return horizon * rangeWeight;
}

float EvaluateSsaoMsaa(int2 pixel, float centerDepth)
{
    if (centerDepth >= 0.99999)
    {
        return 1;
    }
    float3 centerPosition = ReconstructViewPosition(pixel, centerDepth);
    float3 normal = LoadMsaaAoNormal(pixel);
    float occlusion = 0;
    [unroll]
    for (int index = 0; index < 12; ++index)
    {
        int2 scaledOffset = (int2)round(float2(SsaoOffsets[index]) * PixelSizeAndSamples.w);
        int2 samplePixel = ClampPostPixel(pixel + scaledOffset);
        occlusion += EvaluateViewSpaceOccluder(
            centerPosition, normal, samplePixel, LoadMsaaDepth(samplePixel));
    }
    return saturate(1 - (occlusion / 12) * DepthParameters.z * DepthParameters.w);
}

float EvaluateSsaoSingle(int2 pixel, float centerDepth)
{
    if (centerDepth >= 0.99999)
    {
        return 1;
    }
    float3 centerPosition = ReconstructViewPosition(pixel, centerDepth);
    float3 normal = LoadSingleAoNormal(pixel);
    float occlusion = 0;
    [unroll]
    for (int index = 0; index < 12; ++index)
    {
        int2 scaledOffset = (int2)round(float2(SsaoOffsets[index]) * PixelSizeAndSamples.w);
        int2 samplePixel = ClampPostPixel(pixel + scaledOffset);
        occlusion += EvaluateViewSpaceOccluder(
            centerPosition, normal, samplePixel, LoadSingleDepth(samplePixel));
    }
    return saturate(1 - (occlusion / 12) * DepthParameters.z * DepthParameters.w);
}

bool IsDisplayBackground(float2 uv, float3 color)
{
    // Ambient alpha also tracks surface coverage, including materials whose
    // diffuse ambient is zero. Additive emission leaves alpha unchanged, so
    // require the clear colour as well before treating a pixel as background.
    return SceneAmbientDiffuseTexture.SampleLevel(PostSampler, uv, 0).a <= 0.000001
        && all(abs(color - DisplayBackground.rgb) < 0.000001);
}

float3 BloomHighlight(float2 uv)
{
    float3 scene = SceneColorTexture.SampleLevel(PostSampler, uv, 0).rgb;
    if (IsDisplayBackground(uv, scene)) return 0;
    return max(scene - BloomParameters.x, 0);
}

float3 EvaluateBloom(float2 uv)
{
    float2 texel = PixelSizeAndSamples.xy;
    float3 bloom = 0;
    float totalWeight = 0;
    [unroll]
    for (int distance = 1; distance <= 4; ++distance)
    {
        float weight = 5 - distance;
        float2 offset = texel * distance * 1.5;
        // Extract highlights before filtering, in scene-linear HDR. A bright
        // texel can contribute even when its neighbouring pixels are dark.
        bloom += BloomHighlight(uv + float2(offset.x, 0)) * weight;
        bloom += BloomHighlight(uv - float2(offset.x, 0)) * weight;
        bloom += BloomHighlight(uv + float2(0, offset.y)) * weight;
        bloom += BloomHighlight(uv - float2(0, offset.y)) * weight;
        totalWeight += weight * 4;
    }
    bloom /= max(totalWeight, 1);
    return bloom * BloomParameters.y;
}

float4 PSBloomExtract(PostVertexOutput input) : SV_TARGET
{
    float3 bloom = 0;
    // Extract before downsampling so small HDR highlights are not lost below
    // threshold. Four by four samples cover each quarter-resolution pixel.
    [unroll]
    for (int y = 0; y < 4; ++y)
    {
        [unroll]
        for (int x = 0; x < 4; ++x)
        {
            float2 uv = input.TexCoord + (float2(x, y) - 1.5) * PixelSizeAndSamples.xy;
            float3 scene = SceneColorTexture.SampleLevel(PostSampler, uv, 0).rgb;
            if (!IsDisplayBackground(uv, scene))
            {
                float peak = max(scene.r, max(scene.g, scene.b));
                // Recovered UE3 gather rule: maximum channel, threshold,
                // fixed ramp of two. No per-channel colour subtraction.
                float amount = saturate((peak - BloomParameters.x) * 0.5);
                bloom += max(scene, 0) * amount;
            }
        }
    }
    return float4(bloom * (DisplayParameters.x / 16.0), 1);
}

float4 PSBloomBlur(PostVertexOutput input) : SV_TARGET
{
    // Five bilinear fetches approximate a normalized nine-tap Gaussian.
    float2 step = BloomPassParameters.xy;
    float3 bloom = PostBloomTexture.SampleLevel(PostSampler, input.TexCoord, 0).rgb * 0.227027;
    bloom += PostBloomTexture.SampleLevel(PostSampler, input.TexCoord + step * 1.384615, 0).rgb * 0.316216;
    bloom += PostBloomTexture.SampleLevel(PostSampler, input.TexCoord - step * 1.384615, 0).rgb * 0.316216;
    bloom += PostBloomTexture.SampleLevel(PostSampler, input.TexCoord + step * 3.230769, 0).rgb * 0.070270;
    bloom += PostBloomTexture.SampleLevel(PostSampler, input.TexCoord - step * 3.230769, 0).rgb * 0.070270;
    return float4(bloom, 1);
}

float3 LookupPreviewGrade(float3 color)
{
    color = saturate(color);
    float blue = color.b * 15;
    float slice = floor(blue);
    float2 uv = float2((slice * 16 + color.r * 15 + 0.5) / 256,
                      (color.g * 15 + 0.5) / 16);
    // Clamp the upper slice explicitly at white; never bleed into another tile.
    float2 upperUv = uv + float2(min(slice + 1, 15) - slice, 0) / 16;
    return lerp(GradingLutTexture.SampleLevel(PostSampler, uv, 0).rgb,
                GradingLutTexture.SampleLevel(PostSampler, upperUv, 0).rgb, frac(blue));
}

float3 PreviewFilmicResponse(float3 color)
{
    // Analytic fallback shipped in BioWare's SFX shader. The captured runtime
    // LUT differs; this is a documented preview approximation, not a LUT copy.
    float3 x = max(color - 0.004, 0);
    float3 f = (x * (6.2 * x + 0.5)) / (x * (6.2 * x + 1.7) + 0.06);
    return f * f;
}

float3 EncodeDisplaySrgb(float3 linearColor);
float3 MapDisplayHighlights(float3 color);

float3 ComposeGamePresentation(float3 scene, float3 bloom)
{
    float3 exposedScene = max(scene, 0) * DisplayParameters.x;
    float3 color = exposedScene;
    // Shared LE2-style presentation, independent of the loaded source game.
    color = 1 - exp2(-1.7 * color);
    float luminance = dot(color, float3(0.299, 0.587, 0.114));
    float receiver = saturate(BloomParameters.z * exp2(-3 * luminance));
    float3 bloomContribution = bloom * BloomParameters.y * BloomTint.rgb * receiver;
    color += bloomContribution;
    color = PreviewFilmicResponse(color);
    // Anchor the shared response to Neutral before applying the environment grade.
    // Convert the neutral reference to this route's pre-power grading domain:
    // the final 1/2.2 power cancels this 2.2 power at zero curve strength. This
    // is a domain conversion, not a second display gamma on the final output.
    float3 referenceDisplay = EncodeDisplaySrgb(MapDisplayHighlights(exposedScene + bloomContribution));
    float3 referenceGradeDomain = pow(saturate(referenceDisplay), 2.2);
    color = lerp(referenceGradeDomain, color, DisplayParameters.w);
    color = LookupPreviewGrade(color);
    // This path follows the native post-grade power transfer. WPF receives the
    // encoded bytes directly: do not apply EncodeDisplaySrgb after this power.
    return pow(saturate(color), 1.0 / 2.2);
}

float3 EncodeDisplaySrgb(float3 linearColor)
{
    linearColor = max(linearColor, 0);
    float3 low = linearColor * 12.92;
    float3 high = 1.055 * pow(linearColor, 1.0 / 2.4) - 0.055;
    return float3(
        linearColor.r <= 0.0031308 ? low.r : high.r,
        linearColor.g <= 0.0031308 ? low.g : high.g,
        linearColor.b <= 0.0031308 ? low.b : high.b);
}

float3 MapDisplayHighlights(float3 color)
{
    // Provisional neutral display shoulder: retain dark/midtone linear values,
    // smoothly roll highlights into display range. This is a preview transform,
    // not a recovered BioWare grading curve; preset calibration comes later.
    float3 highlight = 0.8 + 0.2 * (1 - exp(-max(color - 0.8, 0) / 0.2));
    return float3(color.r <= 0.8 ? color.r : highlight.r,
        color.g <= 0.8 ? color.g : highlight.g,
        color.b <= 0.8 ? color.b : highlight.b);
}

float4 ComposePostProcess(PostVertexOutput input, float ambientOcclusion)
{
    float4 scene = SceneColorTexture.SampleLevel(PostSampler, input.TexCoord, 0);
    float3 color = scene.rgb * ambientOcclusion;
    if (DisplayParameters.y > 0.5)
    {
        bool background = IsDisplayBackground(input.TexCoord, scene.rgb);
        if (DisplayParameters.z > 0.5)
        {
            float3 bloom = PostBloomTexture.SampleLevel(PostSampler, input.TexCoord, 0).rgb;
            if (!background)
                return float4(saturate(ComposeGamePresentation(color, bloom)), scene.a);
            // Grade/tone mapping must not recolour the selected backdrop. Bloom
            // can still spread across silhouettes in linear display space.
            color = scene.rgb + bloom * BloomParameters.y * BloomTint.rgb;
            return float4(saturate(EncodeDisplaySrgb(color)), scene.a);
        }
        float3 bloom = EvaluateBloom(input.TexCoord);
        // Keep the user's background colour stable; additive materials are
        // identified by their HDR colour, so alpha=0 emission still transforms.
        if (!background || any(bloom > 0))
            color = MapDisplayHighlights(max(color + bloom, 0) * DisplayParameters.x);
    }
    // B8G8R8A8_UNorm and WPF Bgra32 consume encoded bytes without another
    // sRGB conversion. This is the sole display encoding in the pipeline.
    return float4(saturate(EncodeDisplaySrgb(color)), scene.a);
}

float4 PSPostProcessMsaa(PostVertexOutput input) : SV_TARGET
{
    int2 pixel = int2(input.Position.xy);
    float depth = LoadMsaaDepth(pixel);
    return ComposePostProcess(input, EvaluateSsaoMsaa(pixel, depth));
}

float4 PSPostProcessSingle(PostVertexOutput input) : SV_TARGET
{
    int2 pixel = int2(input.Position.xy);
    float depth = SceneDepthSingleTexture.Load(int3(ClampPostPixel(pixel), 0)).r;
    return ComposePostProcess(input, EvaluateSsaoSingle(pixel, depth));
}
