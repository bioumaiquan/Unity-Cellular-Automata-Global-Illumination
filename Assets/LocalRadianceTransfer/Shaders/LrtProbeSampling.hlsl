#ifndef LRT_PROBE_SAMPLING_INCLUDED
#define LRT_PROBE_SAMPLING_INCLUDED

#include "LrtIndirectLighting.hlsl"

TEXTURE3D(_LrtVolume);
SAMPLER(sampler_LrtVolume);
#include "LrtProbeAtlas.hlsl"
TEXTURE2D_FLOAT(_LrtProbeLayoutTexture);
TEXTURE2D(_LrtBlueNoise);
float2 _LrtBlueNoiseSize;
uint _LrtProbeLayoutTextureWidth;
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
    uint index = LrtWorldTrunkIndex(trunk);
    uint2 texel = uint2(index % _LrtProbeLayoutTextureWidth, index / _LrtProbeLayoutTextureWidth);
    uint4 brick = (uint4)round(LOAD_TEXTURE2D(_LrtProbeLayoutTexture, texel) * 255.0);
    uint slot = brick.r | (brick.g << 8u) | (brick.b << 16u);
    uint edge = brick.a;
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
    [branch] if (_LrtSkySampleRadius <= 0.0)
    {
        float4 field = LrtSampleDirectionalField(cell, normal);
        return float4(max(field.rgb, GI_LUMINANCE_MIN), saturate(field.a));
    }
    else
    {
        float3 referenceAxis = abs(normal.y) < 0.99 ? float3(0, 1, 0) : float3(0, 0, 1);
        float3 tangent = normalize(cross(normal, referenceAxis));
        float3 bitangent = cross(normal, tangent);
        // Tile at one blue-noise texel per gather pixel, independent of camera distance.
        uint2 tileSize = uint2(_LrtBlueNoiseSize);
        uint2 firstTexel = gatherPixel % tileSize;
        uint2 secondTexel = (gatherPixel + uint2(5, 9)) % tileSize;
        float2 noise = float2(
            LOAD_TEXTURE2D(_LrtBlueNoise, firstTexel).r,
            LOAD_TEXTURE2D(_LrtBlueNoise, secondTexel).r);
        float4 field = 0.0;

        // Jitter one sample in each 2x2 stratum and map the disk to the cosine-weighted hemisphere.
        // Keep a single atlas lookup path across taps. Each tap can land in a different brick.
        [loop] for (uint sampleIndex = 0u; sampleIndex < 4u; sampleIndex++)
        {
            // Rotate the blue-noise pair differently in each stratum.
            float2 jitter = frac(noise + float2(0.754877666, 0.569840296) * sampleIndex);
            float2 u = (float2(sampleIndex & 1u, sampleIndex >> 1u) + jitter) * 0.5;
            float sine, cosine;
            sincos(TWO_PI * u.y, sine, cosine);
            float3 direction = sqrt(u.x) * (cosine * tangent + sine * bitangent)
                             + sqrt(saturate(1.0 - u.x)) * normal;
            float3 offset = normalize(direction / _LrtCellSpacing) * _LrtSkySampleRadius;
            field += LrtSampleDirectionalField(cell + offset, normal);
        }
        field *= 0.25;
        return float4(max(field.rgb, GI_LUMINANCE_MIN), saturate(field.a));
    }
}

#endif
