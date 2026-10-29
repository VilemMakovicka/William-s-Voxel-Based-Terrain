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
        _R_RandomizerIntensity("R Randomizer Intensity", Range(0.0, 1.0)) = 0.0
        _G_RandomizerIntensity("G Randomizer Intensity", Range(0.0, 1.0)) = 0.0
        _B_RandomizerIntensity("B Randomizer Intensity", Range(0.0, 1.0)) = 0.0
        _A_RandomizerIntensity("A Randomizer Intensity", Range(0.0, 1.0)) = 0.0

        [Header(Wireframe)]
        [Toggle(_SHOWWIREFRAME_ON)] _ShowWireframe("Enable Culled Wireframe", Float) = 0
        _WireframeColor("Wireframe Color", Color) = (0.05, 0.05, 0.05, 0.7)
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
                    float _R_RandomizerIntensity;
                    float _G_RandomizerIntensity;
                    float _B_RandomizerIntensity;
                    float _A_RandomizerIntensity;
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

                float2 Hash22(float2 p)
                {
                    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                    return frac(sin(p) * 43758.5453123);
                }

                // Stochastic sampling on a single 2D plane (e.g. positionWS.xz)
                float4 SampleStochasticPlane(Texture2D tex, SamplerState smp, float2 uv, float intensity)
                {
                    // Fast path: if intensity is 0, do a regular sample
                    if (intensity <= 0.001)
                        return tex.Sample(smp, uv);

                    float2x2 gridToSkew = float2x2(1.0, 0.0, -0.57735027, 1.15470054);
                    float2 skewedCoord = mul(gridToSkew, uv);
                    float2 baseId = floor(skewedCoord);
                    float2 f = frac(skewedCoord);

                    float3 blendWeights = (f.x > f.y) ? float3(1.0 - f.x, f.x - f.y, f.y)
                        : float3(1.0 - f.y, f.y - f.x, f.x);

                    float2 offset1 = (f.x > f.y) ? float2(1, 0) : float2(0, 1);
                    float2 node0 = baseId;
                    float2 node1 = baseId + offset1;
                    float2 node2 = baseId + float2(1, 1);

                    // Compute explicit derivatives to eliminate mipmap artifacts across cells
                    float2 dx = ddx(uv);
                    float2 dy = ddy(uv);

                    float4 c0 = tex.SampleGrad(smp, uv + Hash22(node0) * intensity * 16.0, dx, dy);
                    float4 c1 = tex.SampleGrad(smp, uv + Hash22(node1) * intensity * 16.0, dx, dy);
                    float4 c2 = tex.SampleGrad(smp, uv + Hash22(node2) * intensity * 16.0, dx, dy);

                    float3 w = pow(blendWeights, 4.0);
                    w /= (w.x + w.y + w.z + 1e-5);

                    float4 stochastic = c0 * w.x + c1 * w.y + c2 * w.z;

                    // Contrast preservation against variance loss
                    float3 mean = (c0.rgb + c1.rgb + c2.rgb) * 0.33333;
                    stochastic.rgb = lerp(mean, stochastic.rgb, intensity);

                    // Blend between standard UV sample and stochastic using user slider
                    float4 standard = tex.Sample(smp, uv);
                    return lerp(standard, stochastic, intensity);
                }

                half4 SampleTriplanar(Texture2D tex, SamplerState smp, float3 posWS, float3 weights, float breakupIntensity, bool breakTopOnly)
                {
                    float2 uvX = posWS.zy * _TextureScale;
                    float2 uvY = posWS.xz * _TextureScale;
                    float2 uvZ = posWS.xy * _TextureScale;

                    /*half4 colX = tex.Sample(smp, uvX);
                    half4 colZ = tex.Sample(smp, uvZ);

                    half4 colY;
                    if (breakTopOnly)
                    {
                        colY = SampleStochasticPlane(tex, smp, uvY, breakupIntensity);
                    }
                    else
                    {
                        colY = tex.Sample(smp, uvY);
                    }*/

                    half4 colX = SampleStochasticPlane(tex, smp, uvX, breakupIntensity);
                    half4 colZ = SampleStochasticPlane(tex, smp, uvZ, breakupIntensity);
                    half4 colY = SampleStochasticPlane(tex, smp, uvY, breakupIntensity);

                    return colX * weights.x + colY * weights.y + colZ * weights.z;
                }



                half4 frag(Varyings input) : SV_Target
                {
                    float3 blend = pow(abs(input.normalWS), _TriplanarSharpness);
                    blend /= (blend.x + blend.y + blend.z + 1e-4);

                    //half3 cRock = SampleTriplanar(_TexRock,  sampler_TexRock,  input.positionWS, blend).rgb;
                    //half3 cGrass = SampleTriplanar(_TexGrass, sampler_TexGrass, input.positionWS, blend).rgb;
                    //half3 cDirt = SampleTriplanar(_TexDirt,  sampler_TexDirt,  input.positionWS, blend).rgb;
                    //half3 cPath = SampleTriplanar(_TexPath,  sampler_TexPath,  input.positionWS, blend).rgb;

                    half3 cRock = SampleTriplanar(_TexRock, sampler_TexRock, input.positionWS, blend, _R_RandomizerIntensity, true).rgb;
                    half3 cGrass = SampleTriplanar(_TexGrass, sampler_TexGrass, input.positionWS, blend, _G_RandomizerIntensity, true).rgb;
                    half3 cDirt = SampleTriplanar(_TexDirt, sampler_TexDirt, input.positionWS, blend, _B_RandomizerIntensity, true).rgb;
                    half3 cPath = SampleTriplanar(_TexPath, sampler_TexPath, input.positionWS, blend, _A_RandomizerIntensity, true).rgb;

                    float4 splat = saturate(input.color);

                    // Sum weights to normalize them
                    float totalWeight = splat.r + splat.g + splat.b + splat.a;

                    // Prevent division by zero if all vertex color channels are 0
                    splat /= max(totalWeight, 0.0001);

                    // Weighted sum blend
                    half3 albedo = cRock * splat.r
                                 + cGrass * splat.g
                                 + cDirt * splat.b
                                 + cPath * splat.a;

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