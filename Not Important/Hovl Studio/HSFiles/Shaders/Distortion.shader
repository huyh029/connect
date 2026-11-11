Shader "Hovl/Particles/Distortion"
{
    Properties
    {
        _NormalMap("Normal Map", 2D) = "bump" {}
        _Distortionpower("Distortion power", Float) = 0.05
        _InvFade("Soft Particles Factor", Range(0.01,3.0)) = 1.0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Pass
        {
            Name "Distortion"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ SOFTPARTICLES_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _NormalMap_ST;
                float _Distortionpower;
                float _InvFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.color = input.color;
                output.uv = TRANSFORM_TEX(input.uv, _NormalMap);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                half alpha = input.color.a;
                #if defined(SOFTPARTICLES_ON)
                    float rawDepth = SampleSceneDepth(screenUV);
                    float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                    if (unity_OrthoParams.w > 0.5)
                    {
                        #if UNITY_REVERSED_Z
                            rawDepth = 1.0 - rawDepth;
                        #endif
                        sceneDepth = lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
                    }
                    float particleDepth = -TransformWorldToView(input.positionWS).z;
                    alpha *= saturate(_InvFade * (sceneDepth - particleDepth));
                #endif
                half3 normal = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));
                float cameraDistance = max(distance(GetCameraPositionWS(), input.positionWS), 0.001);
                // Preserve the original texture-pixel offset and distance attenuation.
                float2 offset = normal.rg * _CameraOpaqueTexture_TexelSize.xy * _Distortionpower * alpha / cameraDistance;
                half3 sceneColor = SampleSceneColor(saturate(screenUV + offset));
                half mask = saturate(abs(normal.r) + abs(normal.g) * 30.0 - 0.03);
                return half4(sceneColor, mask * alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}