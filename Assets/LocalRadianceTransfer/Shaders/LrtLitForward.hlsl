#ifndef LRT_LIT_FORWARD_INCLUDED
#define LRT_LIT_FORWARD_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "LrtScreenSpaceGI.hlsl"

// The probe bounds check needs a world position in every forward variant.
#ifndef REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
    #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
#endif

float _LrtTrunkDebug;
float _LrtSkyOcclusionStrength;

half4 LrtFragmentPBR(InputData inputData, SurfaceData surfaceData)
{
    if (_LrtScreenSpaceGIReady > 0.0 && _Surface < 0.5 && LrtHasProbes(inputData.positionWS))
    {
        float4 field = LrtReadScreenSpaceGI(inputData.normalizedScreenSpaceUV);
        half skyVisibility = lerp(1.0, saturate(field.a), _LrtSkyOcclusionStrength);
        if (_LrtTrunkDebug == 2)
            return half4(field.rgb, 1);
        if (_LrtTrunkDebug == 3)
            return half4(LrtEnvironmentLighting(inputData.normalWS) * skyVisibility, 1);

        inputData.bakedGI = _LrtTrunkDebug == 1
            ? half3(0, 0, 0)
            : field.rgb + LrtEnvironmentLighting(inputData.normalWS) * skyVisibility;
    }
    
    //return half4(inputData.bakedGI, 1);

    return UniversalFragmentPBR(inputData, surfaceData) ;
}

// Route the stock Lit forward fragment through the indirect lighting inputs above.
#define UniversalFragmentPBR LrtFragmentPBR
#include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
#undef UniversalFragmentPBR

#endif
