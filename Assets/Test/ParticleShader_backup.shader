Shader "BlendedBuffer/ParticleShader_Backup"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
        _Cutoff("AlphaCutout", Range(0.0, 1.0)) = 0.5
        
        [Toggle(_ALPHATEST_ON)] _AlphaTest("Alpha Test", Float) = 0.0
        
        [Enum(UnityEngine.Rendering.BlendMode)]
        _SrcFactor("Src Factor", Int) = 5     // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)]
        _DstFactor("Dst Factor", Int) = 10    // OneMinusSrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)]
        _SrcBlendAlpha("SrcAlpha Factor", Int) = 1     // One
        [Enum(UnityEngine.Rendering.BlendMode)]
        _DstBlendAlpha("DstAlpha Factor", Int) = 10    // OneMinusSrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)]
        _AlphaSrcFactor("Alpha Src Factor", Int) = 5     // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)]
        _AlphaDstFactor("Alpha Dst Factor", Int) = 10    // OneMinusSrcAlpha
        
        _Surface("Surface Type", Float) = 1.0 // 0: Opaque, 1: Transparent
        _BaseColorAddSubDiff("Base Color AddSubDiff", Vector) = (1, 0, 0, 0)
    }
    SubShader
    {
        Tags {
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "LightMode" = "SRPDefaultUnlit"
        }
        LOD 100
        
        Cull Back
        ZTest LEqual
        ZWrite Off
        
        //  Forward pass.
        Pass
        {
            Name "ForwardLit"

            // -------------------------------------
            // Render State Commands
            //BlendOp[_BlendOp]
            //Blend[_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
        Blend 0 [_SrcFactor][_DstFactor], [_SrcBlendAlpha][_DstBlendAlpha]
        Blend 1 [_AlphaSrcFactor][_AlphaDstFactor]
//            ZWrite[_ZWrite]
//            Cull[_Cull]
//            AlphaToMask[_AlphaToMask]

            HLSLPROGRAM
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex vertParticleUnlit
            //#pragma fragment fragParticleUnlit
            #pragma fragment frag

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _EMISSION

            // -------------------------------------
            // Particle Keywords
            #pragma shader_feature_local _FLIPBOOKBLENDING_ON
            #pragma shader_feature_local _SOFTPARTICLES_ON
            #pragma shader_feature_local _FADING_ON
            #pragma shader_feature_local _DISTORTION_ON
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ _ALPHAPREMULTIPLY_ON _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _ _COLOROVERLAY_ON _COLORCOLOR_ON _COLORADDSUBDIFF_ON

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma instancing_options procedural:ParticleInstancingSetup

            // -------------------------------------
            // Includes
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/Shaders/Particles/ParticlesUnlitInput.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/Shaders/Particles/ParticlesUnlitForwardPass.hlsl"

void frag(VaryingsParticle input, out float4 color : SV_Target0, out float4 alpha : SV_Target1)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    ParticleParams particleParams;
    InitParticleParams(input, particleParams);

    SurfaceData surfaceData;
    InitializeSurfaceData(particleParams, surfaceData);
    InputData inputData;
    InitializeInputData(input, surfaceData, inputData);
    SETUP_DEBUG_TEXTURE_DATA_FOR_TEX(inputData, input.texcoord, _BaseMap);

    half4 finalColor = UniversalFragmentUnlit(inputData, surfaceData);

    #if defined(_SCREEN_SPACE_OCCLUSION) && !defined(_SURFACE_TYPE_TRANSPARENT)
        float2 normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.clipPos);
        AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(normalizedScreenSpaceUV);
        finalColor.rgb *= aoFactor.directAmbientOcclusion;
    #endif

    finalColor.rgb = MixFog(finalColor.rgb, inputData.fogCoord);
    finalColor.a = OutputAlpha(finalColor.a, IsSurfaceTypeTransparent(_Surface));

    //return finalColor;
                color = finalColor;
                alpha = finalColor.a;
}
            ENDHLSL
        }

//        Pass
//        {
//            Name "Blended Particle"
//            
////            Stencil {  
////                Ref 1
////                Comp Always  
////                Pass Replace  
////            }
//            
//            HLSLPROGRAM
//            #pragma vertex vert
//            #pragma fragment frag
//            
//            //#pragma multi_compile_fog
//            #pragma shader_feature_local_fragment _ALPHATEST_ON
//            //#pragma shader_feature_local_fragment _ALPHAMODULATE_ON
//
//            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
//
//            struct Attributes
//            {
//                float4 positionOS : POSITION;
//                float2 texcoord : TEXCOORD0;
//                float4 color : COLOR;
//            };
//
//            struct Varyings
//            {
//                float2 uv : TEXCOORD0;
//                float4 positionCS : SV_POSITION;
//                float4 color : COLOR;
//            };
//
//            TEXTURE2D(_BaseMap);
//            SAMPLER(sampler_BaseMap);
//            
//CBUFFER_START(UnityPerMaterial)
//            float4 _BaseMap_ST;
//            half4 _BaseColor;
//            half _Cutoff;
//CBUFFER_END
//
//            Varyings vert (Attributes input)
//            {
//                Varyings output;
//                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
//                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
//                output.color = input.color;
//                return output;
//            }
//
//            void frag (Varyings i, out float4 color : SV_Target0, out float4 alpha : SV_Target1)
//            {
//                // sample the texture
//                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
//                float a = texColor.a * _BaseColor.a;
//                a = AlphaDiscard(a, _Cutoff);
//                alpha = float4(a,a,a,a);
//                color = float4(texColor.rgb * _BaseColor.rgb, a);
//            }
//            // half4 frag (Varyings i) : SV_Target
//            // {
//            //     // sample the texture
//            //     half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
//            //     half4 color = 0;
//            //     half alpha = texColor.a * _BaseColor.a;
//            //     color.a = AlphaDiscard(alpha, _Cutoff);
//            //     half3 temp = texColor.rgb * _BaseColor.rgb;
//            //     color.rgb = temp;//AlphaModulate(color, alpha);
//            // }
//            ENDHLSL
//        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
