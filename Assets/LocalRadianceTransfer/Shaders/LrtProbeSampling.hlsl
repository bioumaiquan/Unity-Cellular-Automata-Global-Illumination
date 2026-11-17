#ifndef LRT_PROBE_SAMPLING_INCLUDED
#define LRT_PROBE_SAMPLING_INCLUDED

#include "LrtIndirectLighting.hlsl"

TEXTURE3D(_LrtVolume);
SAMPLER(sampler_LrtVolume);
#include "LrtProbeAtlas.hlsl"
TEXTURE2D(_LrtBlueNoise);
float2 _LrtBlueNoiseSize;
float4 _LrtGridDimensions;
float _LrtProbeNormalOffset;
float _LrtSkySampleRadius;

float4 LrtSampleCoefficient(float3 uv, float coefficient)
{
    uv.z = (uv.z + coefficient) * 0.25;
    return SAMPLE_TEXTURE3D_LOD(_LrtVolume, sampler_LrtVolume, uv, 0);
}

float4 LrtSampleAtlasField(float3 position, float3 normal)
{
    float3 grid = clamp((position - _LrtGridOrigin.xyz) / _LrtProbeTrunkEdge, 0.0,
        _LrtProbeTrunkDimensions.xyz - 0.0001);
    uint3 trunk = uint3(grid);
    uint4 brick = _LrtProbeLayout[LrtWorldTrunkIndex(trunk)];
    uint edge = brick.y;
    uint slot = brick.z;
    bool fine = edge == (uint)_LrtFineProbeEdge;
    float3 address = LrtAtlasBrickOrigin(slot, edge) + 1.0 + (grid - trunk) * edge;
    float3 uv = address / (fine ? _LrtFineAtlasSize.xyz : _LrtCoarseAtlasSize.xyz);
    float3 dz = float3(0, 0, 0.25);
    float4 field = 0.0;
    [branch] if (fine)
        field = SAMPLE_TEXTURE3D_LOD(_LrtFineVolume, sampler_LrtVolume, uv, 0)
            - normal.x * SAMPLE_TEXTURE3D_LOD(_LrtFineVolume, sampler_LrtVolume, uv + dz, 0)
            - normal.y * SAMPLE_TEXTURE3D_LOD(_LrtFineVolume, sampler_LrtVolume, uv + 2 * dz, 0)
            - normal.z * SAMPLE_TEXTURE3D_LOD(_LrtFineVolume, sampler_LrtVolume, uv + 3 * dz, 0);
    else
        field = SAMPLE_TEXTURE3D_LOD(_LrtVolume, sampler_LrtVolume, uv, 0)
            - normal.x * SAMPLE_TEXTURE3D_LOD(_LrtVolume, sampler_LrtVolume, uv + dz, 0)
            - normal.y * SAMPLE_TEXTURE3D_LOD(_LrtVolume, sampler_LrtVolume, uv + 2 * dz, 0)
            - normal.z * SAMPLE_TEXTURE3D_LOD(_LrtVolume, sampler_LrtVolume, uv + 3 * dz, 0);
    return field;
}

float4 LrtSampleDirectionalField(float3 cell, float3 normal)
{
    float4 field = 0.0;
    [branch] if (_LrtMixedEnabled != 0)
        field = LrtSampleAtlasField(_LrtGridOrigin.xyz + cell * _LrtCellSpacing, normal);
    else
    {
        // Keep hardware filtering inside each coefficient's slab.
        float3 uv = clamp(cell, 0.5, _LrtGridDimensions.xyz - 0.5) / _LrtGridDimensions.xyz;
        // A surface receives radiance travelling towards it, opposite its normal.
        field = LrtSampleCoefficient(uv, 0.0)
              - normal.x * LrtSampleCoefficient(uv, 1.0)
              - normal.y * LrtSampleCoefficient(uv, 2.0)
              - normal.z * LrtSampleCoefficient(uv, 3.0);
    }
    return field;
}

#define GI_LUMINANCE_MIN 0.00

float4 LrtSampleProbes(float3 positionWS, float3 normal, uint2 gatherPixel)
{
    float3 cellNormal = normalize(normal / _LrtCellSpacing);
    float3 gridPosition = (positionWS - _LrtGridOrigin.xyz) / _LrtCellSpacing;
    float3 cell = gridPosition + cellNormal * _LrtProbeNormalOffset;
    
    [branch] if (_LrtSkySampleRadius > 0.0)
    {
        // Tile the same blue-noise pattern at one texel per gather pixel each frame.
        float2 uv = (float2(gatherPixel) + 0.5) / _LrtBlueNoiseSize;
        float noise = SAMPLE_TEXTURE2D_LOD(_LrtBlueNoise, sampler_PointRepeat, uv, 0).r;

        // URP SSAO's blue-noise direction for sample index 0, evaluated in world space.
        float z = frac(noise) * 2.0 - 1.0;
        float theta = (0.9296875 + noise) * (TWO_PI * 100.0);
        float xy = sqrt(saturate(1.0 - z * z));
        float sine, cosine;
        sincos(theta, sine, cosine);
        float3 direction = float3(xy * cosine, xy * sine, z);
        direction *= dot(normal, direction) >= 0.0 ? 1.0 : -1.0;
        cell += normalize(direction / _LrtCellSpacing) * _LrtSkySampleRadius;
    }
    
    float4 field = LrtSampleDirectionalField(cell, normal);

    return float4(max(field.rgb, GI_LUMINANCE_MIN), saturate(field.a));
}

#endif
