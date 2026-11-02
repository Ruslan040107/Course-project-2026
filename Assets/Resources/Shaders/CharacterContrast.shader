// The character models with "Boost player contrast" (like the CS2 video setting), URP 17 Forward+.
// A small PBR lit shader for the flat colours of the models (a base map works too) plus four boost terms, faded in
// from _CC_FadeStart to _CC_FadeEnd metres (nothing changes up close):
//   1. dark lift          - very dark cloth is raised to a minimum albedo (its hue stays),
//   2. fill light         - a part of the albedo is added as self-illumination, a model in shade never goes black,
//   3. rim                - a thin light fresnel rim on the silhouette,
//   4. adaptive contrast  - the scene behind the pixel (_CameraOpaqueTexture) is compared with the character's colour;
//                           when their luminance is too close the pixel is pushed lighter or darker, away from it.
// The queue is Transparent-499 (2501): the opaque copy of the scene is made before the characters are drawn, so it
// holds the background behind them. Because of that the characters are not in the depth texture and get no SSAO
// (the SSAO keyword is left out on purpose: the occlusion at their pixels would be the background's); they still cast
// and receive shadows (PC_Renderer: Transparent Receive Shadows on) and need the Opaque Texture (PC_RPAsset: on).
// Globals: _CC_Boost 0/1 (the settings option), _CC_FadeStart / _CC_FadeEnd (m), set by CharacterContrast.
// No shader_feature keywords, so nothing is stripped: CharacterContrast loads it from Resources (Shader.Find alone
// would not find it in a build, nothing else references it). Works with SkinnedMeshRenderer, SRP Batcher compatible.
// The default values are the set "P1" measured in the game (AutoShot test farview, see CharacterContrast.cs).
Shader "Course/CharacterContrast"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0.25
        _Metallic("Metallic", Range(0, 1)) = 0
        [HDR] _EmissionColor("Emission", Color) = (0, 0, 0, 1)

        [Header(Contrast boost)]
        _MinAlbedo("Dark lift: minimum albedo luminance (linear)", Range(0, 0.3)) = 0.03
        _Fill("Fill light (x albedo)", Range(0, 1)) = 0.15
        [HDR] _RimColor("Rim colour", Color) = (1, 1, 1, 1)
        _RimPower("Rim power", Range(0.5, 8)) = 2.5
        _RimStrength("Rim strength", Range(0, 2)) = 0.5
        _Adaptive("Adaptive contrast (0..1)", Range(0, 1)) = 1
        _MinContrast("Adaptive: min difference of sqrt(luminance)", Range(0, 0.4)) = 0.2

        [HideInInspector] _Cull("__cull", Float) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
            // after the opaque copy, so _CameraOpaqueTexture holds the background behind the character
            "Queue" = "Transparent-499"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"   // _BaseMap, SampleAlbedoAlpha

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Smoothness;
            half _Metallic;
            half4 _EmissionColor;
            half _MinAlbedo;
            half _Fill;
            half4 _RimColor;
            half _RimPower;
            half _RimStrength;
            half _Adaptive;
            half _MinContrast;
            half _Cull;
            half _Cutoff;          // only read by URP pass includes compiled with _ALPHATEST_ON (never here)
        CBUFFER_END

        // globals (not per material): the settings option and the distance fade
        float _CC_Boost;
        float _CC_FadeStart;
        float _CC_FadeEnd;
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex CCVertex
            #pragma fragment CCFragment

            // the URP keywords of the Lit forward pass (Forward+ = _CLUSTER_LIGHT_LOOP in URP 17), without SSAO
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                half   fogFactor  : TEXCOORD5;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
            #ifdef USE_APV_PROBE_OCCLUSION
                float4 probeOcclusion : TEXCOORD10;
            #endif
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CCVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vp = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs vn = GetVertexNormalInputs(input.normalOS);
                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.positionWS = vp.positionWS;
                output.normalWS = vn.normalWS;
                output.fogFactor = ComputeFogFactor(vp.positionCS.z);
                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
                OUTPUT_SH4(vp.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vp.positionWS), output.vertexSH, output.probeOcclusion);
                output.positionCS = vp.positionCS;
                return output;
            }

            // the scene behind the pixel, a little blurred (5 taps about 6 px apart)
            half3 BackgroundAt(float2 uv)
            {
                float2 o = 6.0 * (_ScreenParams.zw - 1.0);
                half3 c = SampleSceneColor(uv) * 0.4;
                c += SampleSceneColor(uv + float2(o.x, 0)) * 0.15;
                c += SampleSceneColor(uv - float2(o.x, 0)) * 0.15;
                c += SampleSceneColor(uv + float2(0, o.y)) * 0.15;
                c += SampleSceneColor(uv - float2(0, o.y)) * 0.15;
                return c;
            }

            void CCFragment(Varyings input, out half4 outColor : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // how much of the boost: the option times the distance fade
                float dist = distance(GetCameraPositionWS(), input.positionWS);
                half w = (half)(saturate(_CC_Boost) * saturate((dist - _CC_FadeStart) / max(_CC_FadeEnd - _CC_FadeStart, 0.01)));

                half4 albedoAlpha = SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)) * _BaseColor;
                half3 albedo = albedoAlpha.rgb;

                // 1. dark lift: the luminance goes up to at least _MinAlbedo, the hue stays
                half lum = Luminance(albedo);
                half lifted = max(lum, _MinAlbedo);
                albedo = lerp(albedo, albedo * (lifted / max(lum, 1e-4)), w);
                albedo = (lum < 1e-4) ? lerp(albedoAlpha.rgb, _MinAlbedo.xxx, w) : albedo;

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = _Metallic;
                surface.specular = half3(0, 0, 0);
                surface.smoothness = _Smoothness;
                surface.normalTS = half3(0, 0, 1);
                surface.emission = _EmissionColor.rgb;
                surface.occlusion = 1;
                surface.alpha = 1;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
            #endif
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            #if !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                inputData.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(inputData.positionWS), inputData.normalWS,
                                              inputData.viewDirectionWS, input.positionCS.xy, input.probeOcclusion, inputData.shadowMask);
            #else
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
            #endif

                half4 color = UniversalFragmentPBR(inputData, surface);

                // 2. fill light and 3. rim
                half ndv = saturate(dot(inputData.normalWS, inputData.viewDirectionWS));
                half rim = pow(1.0h - ndv, _RimPower) * _RimStrength;
                color.rgb += (albedo * _Fill + _RimColor.rgb * rim) * w;

                // 4. adaptive contrast against the background behind this pixel
                half3 bg = BackgroundAt(inputData.normalizedScreenSpaceUV);
                half sb = sqrt(max(Luminance(bg), 0));
                half lc = max(Luminance(color.rgb), 1e-4);
                half sc = sqrt(lc);
                half d = sc - sb;
                if (abs(d) < _MinContrast)
                {
                    // away from the background; a nearly black or white background leaves only one way
                    half dir = d >= 0 ? 1.0h : -1.0h;
                    if (sb + _MinContrast > 0.95h) dir = -1.0h;
                    if (sb - _MinContrast < 0.05h) dir = 1.0h;
                    half target = max(sb + dir * _MinContrast, 0.02h);
                    half k = (target * target) / lc;
                    color.rgb = lerp(color.rgb, color.rgb * k, _Adaptive * w);
                }

                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                outColor = half4(color.rgb, 1);
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
