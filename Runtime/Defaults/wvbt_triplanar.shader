Shader "Custom/wvbt_triplanar"
{
    Properties
    {
        [Header(Textures)]
        _TexRock("Rock (Base/Red)", 2D) = "white" {}
        _TexGrass("Grass (Green Channel)", 2D) = "white" {}
        _TexDirt("Dirt (Blue Channel)", 2D) = "white" {}
        _TexPath("Path (Alpha Channel)", 2D) = "white" {}

        [Header(Tiling and Blend)]
        _TextureScale("Texture Tiling", Float) = 0.2
        _TriplanarSharpness("Projection Sharpness", Range(1.0, 16.0)) = 4.0

        [Header(Wireframe)]
        [Toggle(_SHOWWIREFRAME_ON)] _ShowWireframe("Enable Culled Wireframe", Float) = 0
        _WireframeColor("Wireframe Color", Color) = (0.05, 0.05, 0.05, 0.7)
            //_WireframeGridSize("Wireframe Grid Spacing", Float) = 1.0
            _WireframeThickness("Wireframe Thickness", Range(0.01, 0.2)) = 0.04
    }

        SubShader
        {
            Tags
            {
                "RenderType" = "Opaque"
                "RenderPipeline" = "UniversalPipeline"
                "Queue" = "Geometry"
            }
            LOD 200

            Pass
            {
                Name "ForwardLit"
                Tags { "LightMode" = "UniversalForward" }

                Cull Back
                ZWrite On
                ZTest LEqual

                HLSLPROGRAM
                #pragma vertex vert
                #pragma fragment frag

                #pragma shader_feature_local _SHOWWIREFRAME_ON

                #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
                #pragma multi_compile _ _ADDITIONAL_LIGHTS
                #pragma multi_compile_fragment _ _SHADOWS_SOFT
                #pragma multi_compile _ LIGHTMAP_ON
                #pragma multi_compile _ DIRLIGHTMAP_COMBINED
                #pragma multi_compile_fog

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

                struct Attributes
                {
                    float4 positionOS   : POSITION;
                    float3 normalOS     : NORMAL;
                    float4 color        : COLOR;
                    float2 uv1          : TEXCOORD1;
                };

                struct Varyings
                {
                    float4 positionCS   : SV_POSITION;
                    float3 positionWS   : TEXCOORD0;
                    float3 normalWS     : TEXCOORD1;
                    float4 color        : COLOR;
                    float  fogFactor : TEXCOORD3;
                    DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 2);
                };

                TEXTURE2D(_TexRock);  SAMPLER(sampler_TexRock);
                TEXTURE2D(_TexGrass); SAMPLER(sampler_TexGrass);
                TEXTURE2D(_TexDirt);  SAMPLER(sampler_TexDirt);
                TEXTURE2D(_TexPath);  SAMPLER(sampler_TexPath);

                CBUFFER_START(UnityPerMaterial)
                    float _TextureScale;
                    float _TriplanarSharpness;
                    float4 _WireframeColor;
                    //float _WireframeGridSize;
                    float _WireframeThickness;
                CBUFFER_END

                Varyings vert(Attributes input)
                {
                    Varyings output;
                    VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                    VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                    output.positionCS = posInputs.positionCS;
                    output.positionWS = posInputs.positionWS;
                    output.normalWS = normInputs.normalWS;
                    output.color = input.color;

                    OUTPUT_LIGHTMAP_UV(input.uv1, unity_LightmapST, output.lightmapUV);
                    OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

                    output.fogFactor = ComputeFogFactor(output.positionCS.z);

                    return output;
                }

                half4 SampleTriplanar(Texture2D tex, SamplerState smp, float3 posWS, float3 weights)
                {
                    float2 uvX = posWS.zy * _TextureScale;
                    float2 uvY = posWS.xz * _TextureScale;
                    float2 uvZ = posWS.xy * _TextureScale;

                    half4 colX = SAMPLE_TEXTURE2D(tex, smp, uvX);
                    half4 colY = SAMPLE_TEXTURE2D(tex, smp, uvY);
                    half4 colZ = SAMPLE_TEXTURE2D(tex, smp, uvZ);

                    return colX * weights.x + colY * weights.y + colZ * weights.z;
                }

                half4 frag(Varyings input) : SV_Target
                {
                    float3 blend = pow(abs(input.normalWS), _TriplanarSharpness);
                    blend /= (blend.x + blend.y + blend.z + 1e-4);

                    half3 cRock = SampleTriplanar(_TexRock,  sampler_TexRock,  input.positionWS, blend).rgb;
                    half3 cGrass = SampleTriplanar(_TexGrass, sampler_TexGrass, input.positionWS, blend).rgb;
                    half3 cDirt = SampleTriplanar(_TexDirt,  sampler_TexDirt,  input.positionWS, blend).rgb;
                    half3 cPath = SampleTriplanar(_TexPath,  sampler_TexPath,  input.positionWS, blend).rgb;

                    // Base rock + splat layers
                    half3 albedo = cRock;
                    float4 splat = saturate(input.color);
                    albedo = lerp(albedo, cGrass, splat.g);
                    albedo = lerp(albedo, cDirt, splat.b);
                    albedo = lerp(albedo, cPath, splat.a);

                    // Lighting
                    half3 bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, input.normalWS);
                    Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                    float NdotL = saturate(dot(input.normalWS, mainLight.direction));
                    half3 directLighting = mainLight.color * (NdotL * mainLight.shadowAttenuation);
                    half3 totalLighting = directLighting + bakedGI;

                    half3 finalColor = albedo * totalLighting;

    #if defined(_SHOWWIREFRAME_ON)
                    // World-space triplanar voxel cell wireframe:
                    // Draws crisp grid outlines matching the voxel cells directly onto visible surfaces
                    float3 coord = input.positionWS / max(1.0, 0.001);
                    float3 grid = abs(frac(coord - 0.5) - 0.5);

                    // Screen-space edge anti-aliasing
                    float3 width = fwidth(coord);
                    float3 wire = smoothstep(width * _WireframeThickness, width * (_WireframeThickness + 1.0), grid);
                    float lineMask = min(min(wire.x, wire.y), wire.z);

                    finalColor = lerp(_WireframeColor.rgb, finalColor, 1 - (1 - lineMask) * _WireframeColor.a);
    #endif
                    finalColor = MixFog(finalColor, input.fogFactor);

                    return half4(finalColor, 1.0);
                }
                ENDHLSL
            }

            // ------------------------------------------------------------------
        // Depth Only Pass
        // Writes the terrain depth to _CameraDepthTexture for water foam, decals, etc.
            Pass
            {
                Name "DepthOnly"
                Tags { "LightMode" = "DepthOnly" }

                ZWrite On
                ColorMask R
                Cull Back

                HLSLPROGRAM
                #pragma vertex DepthOnlyVertex
                #pragma fragment DepthOnlyFragment

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

                struct Attributes
                {
                    float4 positionOS : POSITION;
                };

                struct Varyings
                {
                    float4 positionCS : SV_POSITION;
                };

                Varyings DepthOnlyVertex(Attributes input)
                {
                    Varyings output;
                    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                    return output;
                }

                half4 DepthOnlyFragment(Varyings input) : SV_Target
                {
                    return 0;
                }
                ENDHLSL
            }

                    // ------------------------------------------------------------------
                    // DepthNormals Pass
                    // Writes depth + world-space normals for Screen-Space AO (SSAO) and Decals
                        Pass
                    {
                        Name "DepthNormals"
                        Tags { "LightMode" = "DepthNormals" }

                        ZWrite On
                        Cull Back

                        HLSLPROGRAM
                        #pragma vertex DepthNormalsVertex
                        #pragma fragment DepthNormalsFragment

                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

                        struct Attributes
                        {
                            float4 positionOS : POSITION;
                            float3 normalOS   : NORMAL;
                        };

                        struct Varyings
                        {
                            float4 positionCS : SV_POSITION;
                            float3 normalWS   : TEXCOORD0;
                        };

                        Varyings DepthNormalsVertex(Attributes input)
                        {
                            Varyings output;
                            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                            return output;
                        }

                        half4 DepthNormalsFragment(Varyings input) : SV_Target
                        {
                            float3 normalWS = normalize(input.normalWS);
                            return half4(NormalizeNormalPerPixel(normalWS), 0.0);
                        }
                        ENDHLSL
                    }

                    Pass
                    {
                        Name "Meta"
                        Tags { "LightMode" = "Meta" }

                        Cull Off

                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag

                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

                        struct Attributes
                        {
                            float4 positionOS : POSITION;
                            float2 uv1        : TEXCOORD1;
                        };

                        struct Varyings
                        {
                            float4 positionCS : SV_POSITION;
                        };

                        Varyings vert(Attributes input)
                        {
                            Varyings output;
                            output.positionCS = UnityMetaVertexPosition(input.positionOS.xyz, input.uv1, input.uv1, unity_LightmapST, unity_DynamicLightmapST);
                            return output;
                        }

                        half4 frag(Varyings input) : SV_Target
                        {
                            UnityMetaInput metaInput = (UnityMetaInput)0;
                            metaInput.Albedo = half3(0.5, 0.5, 0.5);
                            return UnityMetaFragment(metaInput);
                        }
                        ENDHLSL
                    }
        }
            FallBack "Hidden/Universal Render Pipeline/FallbackError"
}