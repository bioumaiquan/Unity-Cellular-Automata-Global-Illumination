#ifndef LRT_INDIRECT_LIGHTING_INCLUDED
#define LRT_INDIRECT_LIGHTING_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"

static const float4 LRT_UNOCCLUDED_GI = float4(0, 0, 0, 1);

float4 _LrtGridOrigin;
float3 _LrtCellSpacing;
float3 _LrtGridExtent;
int _LrtAmbientMode;
float3 _LrtAmbientSky;
float3 _LrtAmbientEquator;
float3 _LrtAmbientGround;
float4 _LrtAmbientSH[7];

float3 LrtEnvironmentLighting(float3 normal)
{
    // Values are UnityEngine.Rendering.AmbientMode.Trilight and Flat.
    if (_LrtAmbientMode == 1)
        return lerp(_LrtAmbientEquator, normal.y >= 0.0 ? _LrtAmbientSky : _LrtAmbientGround, abs(normal.y));
    if (_LrtAmbientMode == 3)
        return _LrtAmbientSky;

    return max(0.0, SampleSH9(_LrtAmbientSH, normal));
}

bool LrtHasProbes(float3 positionWS)
{
    float3 offset = positionWS - _LrtGridOrigin.xyz;
    return _LrtCellSpacing.x > 0.0 && all(offset >= 0.0) &&
           all(offset < _LrtGridExtent);
}

#endif
