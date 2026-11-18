Shader "Hidden/Local Radiance Transfer/Screen Space Gather"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "LRT Screen Space Gather"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            TEXTURE2D_X(_LrtDepthNormalTexture);
            TEXTURE2D_X_FLOAT(_LrtDepthNormalDepth);
            #include "LrtProbeSampling.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Both targets have the same dimensions. Load without filtering across surfaces.
                uint2 pixel = uint2(input.positionCS.xy);
                float deviceDepth = LOAD_TEXTURE2D_X(_LrtDepthNormalDepth, pixel).r;
                #if UNITY_REVERSED_Z
                    if (deviceDepth <= 0.0)
                        return LRT_UNOCCLUDED_GI;
                #else
                    if (deviceDepth >= 1.0)
                        return LRT_UNOCCLUDED_GI;
                    deviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, deviceDepth);
                #endif
                float3 positionWS = ComputeWorldSpacePosition(input.texcoord, deviceDepth, UNITY_MATRIX_I_VP);
                if (!LrtHasProbes(positionWS))
                    return LRT_UNOCCLUDED_GI;

                float2 packedNormal = LOAD_TEXTURE2D_X(_LrtDepthNormalTexture, pixel).rg;
                float3 normalWS = UnpackNormalOctQuadEncode(packedNormal * 2.0 - 1.0);
                return LrtSampleProbes(positionWS, normalWS, uint2(input.positionCS.xy));
            }
            ENDHLSL
        }
    }
}
