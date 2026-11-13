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
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "LrtProbeSampling.hlsl"

            bool LrtHasSceneDepth(float depth)
            {
                #if UNITY_REVERSED_Z
                    return depth > 0.0;
                #else
                    return depth < 1.0;
                #endif
            }

            float3 LrtUnprojectDepth(int2 pixel, float2 invScreenSize, float depth)
            {
                #if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
                #endif
                float2 uv = (float2(pixel) + 0.5) * invScreenSize;
                return ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
            }

            float3 LrtDepthTangent(int2 pixel, int2 axis, int2 screenSize, float2 invScreenSize,
                float depth, float3 positionWS)
            {
                int2 before = pixel - axis;
                int2 after = pixel + axis;
                float beforeDepth = LoadSceneDepth(clamp(before, 0, screenSize - 1));
                float afterDepth = LoadSceneDepth(clamp(after, 0, screenSize - 1));
                bool beforeValid = all(before >= 0) && LrtHasSceneDepth(beforeDepth);
                bool afterValid = all(after < screenSize) && LrtHasSceneDepth(afterDepth);

                // Prefer the neighbor with the smaller depth discontinuity.
                if (beforeValid && (!afterValid || abs(beforeDepth - depth) < abs(afterDepth - depth)))
                    return positionWS - LrtUnprojectDepth(before, invScreenSize, beforeDepth);
                if (afterValid)
                    return LrtUnprojectDepth(after, invScreenSize, afterDepth) - positionWS;
                return 0.0;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int2 screenSize = int2(GetScaledScreenParams().xy);
                float2 invScreenSize = rcp(float2(screenSize));
                int2 pixel = min(int2(input.texcoord * screenSize), screenSize - 1);
                float depth = LoadSceneDepth(pixel);
                if (!LrtHasSceneDepth(depth))
                    return LRT_UNOCCLUDED_GI;

                float3 positionWS = LrtUnprojectDepth(pixel, invScreenSize, depth);
                if (!LrtHasProbes(positionWS))
                    return LRT_UNOCCLUDED_GI;

                float3 dx = LrtDepthTangent(pixel, int2(1, 0), screenSize, invScreenSize, depth, positionWS);
                float3 dy = LrtDepthTangent(pixel, int2(0, 1), screenSize, invScreenSize, depth, positionWS);
                float3 normalWS = cross(dx, dy);
                float normalLengthSq = dot(normalWS, normalWS);
                if (normalLengthSq <= 0.0)
                    return LRT_UNOCCLUDED_GI;

                normalWS *= rsqrt(normalLengthSq);
                if (dot(normalWS, GetWorldSpaceViewDir(positionWS)) < 0.0)
                    normalWS = -normalWS;
                return LrtSampleProbes(positionWS, normalWS, uint2(input.positionCS.xy));
            }
            ENDHLSL
        }
    }
}
