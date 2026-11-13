Shader "Hidden/Local Radiance Transfer/Probe Markers"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Pass
        {
            Name "Probe Markers"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_LrtVolume);
            StructuredBuffer<uint4> _LrtProbeLayout;
            #include "../Shaders/LrtProbeAtlas.hlsl"
            float4 _LrtGridOrigin;
            float3 _LrtCellSpacing;
            float4 _LrtProbeDrawDimensions;
            float _LrtProbeMarkerRadius;
            uint _LrtProbeDrawStride;
            uint _LrtMarkerEdge;
            uint _LrtMarkerTrunkOffset;

            static const float2 Corners[6] =
            {
                float2(-1, -1), float2(-1, 1), float2(1, 1),
                float2(-1, -1), float2(1, 1), float2(1, -1)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                nointerpolation uint3 probe : TEXCOORD1;
            };

            Varyings Vert(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                uint3 dimensions = uint3(_LrtProbeDrawDimensions.xyz);
                uint3 probe;
                float3 center;
                if (_LrtMarkerEdge > 0)
                {
                    uint perBrick = dimensions.x * dimensions.y * dimensions.z;
                    uint slot = instance / perBrick;
                    uint3 local = LrtAtlasUnflatten(instance % perBrick, dimensions) * _LrtProbeDrawStride;
                    uint3 trunk = LrtAtlasUnflatten(_LrtProbeLayout[slot + _LrtMarkerTrunkOffset].w,
                        uint3(_LrtProbeTrunkDimensions.xyz));
                    center = _LrtGridOrigin.xyz + (trunk + (float3(local) + 0.5) / _LrtMarkerEdge) * _LrtProbeTrunkEdge;
                    probe = LrtAtlasBrickOrigin(slot, _LrtMarkerEdge) + local + 1;
                }
                else
                {
                    probe = LrtAtlasUnflatten(instance, dimensions) * _LrtProbeDrawStride;
                    center = _LrtGridOrigin.xyz + (float3(probe) + 0.5) * _LrtCellSpacing;
                }
                float3 positionVS = TransformWorldToView(center);
                float4 centerCS = TransformWViewToHClip(positionVS);
                positionVS.xy += Corners[vertex] * _LrtProbeMarkerRadius;

                Varyings output;
                output.positionCS = TransformWViewToHClip(positionVS);
                // Keep close markers from obscuring the view as the camera passes through the grid.
                float2 maxOffset = 12.0 * max(centerCS.w, 0.0) / _ScaledScreenParams.xy;
                output.positionCS.xy = centerCS.xy + clamp(output.positionCS.xy - centerCS.xy, -maxOffset, maxOffset);
                output.corner = Corners[vertex];
                output.probe = probe;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float radiusSquared = dot(input.corner, input.corner);
                clip(1.0 - radiusSquared);

                // The first coefficient slab holds the probe's mean reflected radiance.
                float3 bounce;
                [branch] if (_LrtMarkerEdge == (uint)_LrtFineProbeEdge && _LrtMarkerEdge > 0)
                    bounce = LOAD_TEXTURE3D(_LrtFineVolume, input.probe).rgb;
                else
                    bounce = LOAD_TEXTURE3D(_LrtVolume, input.probe).rgb;
                bounce = max(bounce, 0.0);
                float dome = sqrt(saturate(1.0 - radiusSquared));
                return half4((0.012 + 4.0 * bounce) * (0.55 + 0.45 * dome), 1.0);
            }
            ENDHLSL
        }
    }
}
