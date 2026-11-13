#ifndef LRT_SCREEN_SPACE_GI_INCLUDED
#define LRT_SCREEN_SPACE_GI_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "LrtIndirectLighting.hlsl"

TEXTURE2D_X(_LrtScreenSpaceGI);
float _LrtScreenSpaceGIReady;

float4 LrtReadScreenSpaceGI(float2 uv)
{
    // Undo URP's normalized screen UV transform to address the raw RT pixels.
    TransformNormalizedScreenUV(uv);
    return SAMPLE_TEXTURE2D_X(_LrtScreenSpaceGI, sampler_LinearClamp, UnityStereoTransformScreenSpaceTex(uv));
}

#endif
