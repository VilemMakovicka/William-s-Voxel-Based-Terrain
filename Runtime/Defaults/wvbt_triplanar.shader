Shader "Custom/wvbt_triplanar"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TextureScale ("Texture Scale", Float) = 1.0
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags 
        { 
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque" 
            "Queue" = "Geometry" 
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _TextureScale;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 blend = abs(input.normalWS);
                blend /= (blend.x + blend.y + blend.z + 0.00001);

                float2 uvX = input.positionWS.zy * _TextureScale;
                float2 uvY = input.positionWS.xz * _TextureScale;
                float2 uvZ = input.positionWS.xy * _TextureScale;

                half4 colX = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvX);
                half4 colY = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvY);
                half4 colZ = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvZ);

                half4 finalColor = (colX * blend.x + colY * blend.y + colZ * blend.z) * _Color;

                // Simple main directional light shading
                Light mainLight = GetMainLight();
                half NdotL = saturate(dot(input.normalWS, mainLight.direction));
                finalColor.rgb *= (mainLight.color * NdotL + half3(0.2, 0.2, 0.2));

                return finalColor;
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}