Shader "DOTSAnimation/GPUSkinning"
{
    Properties
    {
        _BaseMap    ("Albedo",   2D)         = "white" {}
        _BaseColor  ("Color",    Color)      = (1,1,1,1)
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _Metallic   ("Metallic",   Range(0,1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _Smoothness;
                float  _Metallic;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            struct SkinnedVertex { float3 position; float pad0; float3 normal; float pad1; };
            StructuredBuffer<SkinnedVertex> _SkinnedVertices;

            StructuredBuffer<uint>     _InstanceVertexOffsets;
            StructuredBuffer<float4x4> _InstanceMatrices;

            uint _VertexCount;

            struct Attributes
            {
                uint   vertexID   : SV_VertexID;
                float2 uv         : TEXCOORD0;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                float  fogFactor   : TEXCOORD4;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                uint bufIdx   = IN.instanceID * _VertexCount + IN.vertexID;
                float3 posWS  = _SkinnedVertices[bufIdx].position;
                float3 normWS = normalize(_SkinnedVertices[bufIdx].normal);

                OUT.positionHCS = mul(UNITY_MATRIX_VP, float4(posWS, 1.0));
                OUT.positionWS  = posWS;
                OUT.normalWS    = normWS;
                OUT.uv          = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.shadowCoord = TransformWorldToShadowCoord(posWS);
                OUT.fogFactor   = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                half4  albedo   = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                float3 diffuseColor  = albedo.rgb * (1.0 - _Metallic);
                float3 specularColor = lerp(float3(0.04, 0.04, 0.04), albedo.rgb, _Metallic);

                Light  mainLight = GetMainLight(IN.shadowCoord);
                float  NdotL     = saturate(dot(normalWS, mainLight.direction));
                float3 radiance  = mainLight.color * mainLight.shadowAttenuation * NdotL;
                float3 ambient   = SampleSH(normalWS);

                float3 addLight = 0;
                #ifdef _ADDITIONAL_LIGHTS
                for (uint i = 0; i < GetAdditionalLightsCount(); ++i)
                {
                    Light l = GetAdditionalLight(i, IN.positionWS);
                    addLight += l.color * l.distanceAttenuation * l.shadowAttenuation * saturate(dot(normalWS, l.direction));
                }
                #endif

                float3 viewDir = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float3 halfDir = normalize(mainLight.direction + viewDir);
                float  rough   = 1.0 - _Smoothness;
                float  a2      = max(rough*rough*rough*rough, 1e-4);
                float  NdotH   = saturate(dot(normalWS, halfDir));
                float  NdotV   = saturate(dot(normalWS, viewDir));
                float  denom   = NdotH*NdotH*(a2-1.0)+1.0;
                float  D       = a2 / (PI*denom*denom+1e-7);
                float  k       = rough*0.5;
                float  G       = (NdotL/(NdotL*(1.0-k)+k+1e-7))*(NdotV/(NdotV*(1.0-k)+k+1e-7));
                float3 F       = specularColor+(1.0-specularColor)*pow(1.0-saturate(dot(halfDir,viewDir)),5.0);
                float3 spec    = (D*G*F)/(4.0*NdotL*NdotV+1e-7)*radiance;

                float3 color = diffuseColor*(radiance+ambient+addLight)+spec;
                color = MixFog(color, IN.fogFactor);
                return half4(color, albedo.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex   vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_shadowcaster

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST; float4 _BaseColor; float _Smoothness; float _Metallic;
            CBUFFER_END


            struct SkinnedVertex { float3 position; float pad0; float3 normal; float pad1; };
            StructuredBuffer<SkinnedVertex> _SkinnedVertices;
            uint _VertexCount;

            struct Attributes { uint vertexID : SV_VertexID; uint instanceID : SV_InstanceID; };
            struct Varyings   { float4 positionHCS : SV_POSITION; };

            Varyings vertShadow(Attributes IN)
            {
                Varyings OUT;
                uint   bufIdx = IN.instanceID * _VertexCount + IN.vertexID;
                float3 posWS  = _SkinnedVertices[bufIdx].position;
                float3 normWS = normalize(_SkinnedVertices[bufIdx].normal);

                // _ShadowBias.y = normal bias scale set by URP shadow settings
                float3 biasedPosWS = posWS + normWS * _ShadowBias.y;
                OUT.positionHCS = TransformWorldToHClip(biasedPosWS);

                #if UNITY_REVERSED_Z
                    OUT.positionHCS.z = min(OUT.positionHCS.z, OUT.positionHCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    OUT.positionHCS.z = max(OUT.positionHCS.z, OUT.positionHCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return OUT;
            }

            half4 fragShadow(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask 0

            HLSLPROGRAM
            #pragma vertex   vertDepth
            #pragma fragment fragDepth
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST; float4 _BaseColor; float _Smoothness; float _Metallic;
            CBUFFER_END

            struct SkinnedVertex { float3 position; float pad0; float3 normal; float pad1; };
            StructuredBuffer<SkinnedVertex> _SkinnedVertices;
            uint _VertexCount;

            struct Attributes { uint vertexID : SV_VertexID; uint instanceID : SV_InstanceID; };
            struct Varyings   { float4 positionHCS : SV_POSITION; };

            Varyings vertDepth(Attributes IN)
            {
                Varyings OUT;
                uint   bufIdx = IN.instanceID * _VertexCount + IN.vertexID;
                float3 posWS  = _SkinnedVertices[bufIdx].position;
                OUT.positionHCS = mul(UNITY_MATRIX_VP, float4(posWS, 1.0));
                return OUT;
            }

            half4 fragDepth(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}