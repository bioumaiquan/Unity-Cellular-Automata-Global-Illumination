#ifndef LRT_LIGHTING_INCLUDED
#define LRT_LIGHTING_INCLUDED

struct LrtPunctualLight
{
    float4 positionAndInvRangeSq;
    float4 radiance;
    float4 spotCone; // xyz: direction toward the light scaled by the cone falloff, w: falloff offset
};

StructuredBuffer<LrtPunctualLight> _LrtPunctualLights;
int _LrtPunctualLightCount;

struct LrtAreaEmitter
{
    float4 positionAndInvRangeSq;
    float4 radiance;
    float4 rightAndHalfWidth;
    float4 upAndHalfHeight;
};

StructuredBuffer<LrtAreaEmitter> _LrtAreaLights;
int _LrtAreaLightCount;

bool LrtIsOccluded(float3 rayOrigin, float3 ray, float maxDistance);

float LrtRangeFade(float distanceSquared, float inverseRangeSquared)
{
    float factor = distanceSquared * inverseRangeSquared;
    float fade = saturate(1.0 - factor * factor);
    return fade * fade;
}

float3 LrtPunctualIncident(float3 position, float3 normal, LrtPunctualLight light, float3 lightColor)
{
    if (!any(lightColor > 0.0))
        return 0.0;

    float3 delta = light.positionAndInvRangeSq.xyz - position;
    float distanceSquared = dot(delta, delta);
    float3 toLight = delta * rsqrt(max(distanceSquared, 1e-5));
    float cosine = saturate(dot(normal, toLight));
    float cone = saturate(dot(light.spotCone.xyz, toLight) + light.spotCone.w);
    if (cosine <= 0.0 || cone <= 0.0)
        return 0.0;

    float attenuation = rcp(1.0 + distanceSquared);
    if (light.positionAndInvRangeSq.w > 0.0)
    {
        // Match URP's inverse-square attenuation and smooth range cutoff.
        float fade = LrtRangeFade(distanceSquared, light.positionAndInvRangeSq.w);
        if (fade <= 0.0)
            return 0.0;
        attenuation = fade / max(distanceSquared, 6.103515625e-5);
    }

    float3 rayOrigin = position + normal * 0.015;
    if (LrtIsOccluded(rayOrigin, light.positionAndInvRangeSq.xyz - rayOrigin, 0.998))
        return 0.0;
    return lightColor * (cosine * attenuation * cone * cone);
}

float3 LrtPunctualLighting(float3 position, float3 normal)
{
    float3 total = 0.0;
    [loop] for (int i = 0; i < _LrtPunctualLightCount; i++)
    {
        LrtPunctualLight light = _LrtPunctualLights[i];
        float3 color = light.radiance.rgb;
        total += LrtPunctualIncident(position, normal, light, color);
    }
    return total;
}

float3 LrtAreaLighting(float3 position, float3 normal)
{
    float3 total = 0.0;
    float3 rayOrigin = position + normal * 0.015;
    [loop] for (int i = 0; i < _LrtAreaLightCount; i++)
    {
        LrtAreaEmitter light = _LrtAreaLights[i];
        float3 center = light.positionAndInvRangeSq.xyz;
        float3 emissionNormal = cross(light.rightAndHalfWidth.xyz, light.upAndHalfHeight.xyz);
        float3 color = light.radiance.rgb;
        if (!any(color > 0.0) || dot(position - center, emissionNormal) <= 0.0)
            continue;

        float3 right = light.rightAndHalfWidth.xyz * (0.5 * light.rightAndHalfWidth.w);
        float3 up = light.upAndHalfHeight.xyz * (0.5 * light.upAndHalfHeight.w);
        // Clamp the inverse-square term at the radius of a disk with the area of one patch.
        float minDistanceSquared = max(6.103515625e-5, light.rightAndHalfWidth.w * light.upAndHalfHeight.w * 0.318309886);
        float contribution = 0.0;
        [unroll] for (uint sampleIndex = 0; sampleIndex < 4; sampleIndex++)
        {
            float3 samplePosition = center + ((sampleIndex & 1u) != 0u ? right : -right)
                                          + ((sampleIndex & 2u) != 0u ? up : -up);
            float3 delta = samplePosition - position;
            float distanceSquared = dot(delta, delta);
            float3 toLight = delta * rsqrt(max(distanceSquared, 1e-5));
            float receive = saturate(dot(normal, toLight));
            float emit = saturate(dot(emissionNormal, -toLight));
            float fade = LrtRangeFade(distanceSquared, light.positionAndInvRangeSq.w);
            if (receive > 0.0 && fade > 0.0 && !LrtIsOccluded(rayOrigin, samplePosition - rayOrigin, 0.998))
                contribution += receive * emit * fade / max(distanceSquared, minDistanceSquared);
        }
        total += color * (0.25 * contribution);
    }
    return total;
}

#endif
