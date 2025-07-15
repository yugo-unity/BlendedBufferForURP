using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace BlendedBuffer
{
    /// <summary>
    /// Blended Buffer for URP
    /// </summary>
    public class BlendedBufferFeature : ScriptableRendererFeature
    {
        const string RENDER_TARGET_NAME = "_BlendedTarget";
        const string RENDER_TARGET_DEPTH_NAME = "_BlendedTarget_Depth";

        public enum DOWN_SAMPLING
        {
            NONE = 1,
            X2 = 2,
            X4 = 4,
            X8 = 8,
            X16 = 16,
        }

        [System.Serializable]
        public class Settings
        {
            public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            public DOWN_SAMPLING downSampling = DOWN_SAMPLING.X2; // to divide resolution
            public LayerMask layerMask = 0; // layer for VFX

            [Tooltip("BilinearだとDepthとのEdgeは綺麗になりますが全体的にボケが強くなるのでお好みで選択ください")]
            public FilterMode downSampleFilterMode = FilterMode.Bilinear;

            [HideInInspector] public Shader copyDepthShader = null;
            [HideInInspector] public Shader blitShader = null;

            public bool enabledMRT = true;
        }

        [SerializeField] Settings settings = new Settings();
        BlendedBufferPass blendedBufferPass;

        class BlendedBufferPass : ScriptableRenderPass
        {
            static readonly List<ShaderTagId> SHADER_TAG_ID = new List<ShaderTagId>
            {
                new ShaderTagId("SRPDefaultUnlit"),
                //new ShaderTagId("UniversalForward"),
            };

            static readonly int ALPHA_TEX_ID = Shader.PropertyToID("_AlphaTex");

            class PassData
            {
                public TextureHandle color, alpha, depth, srcColor, srcDepth;
                public RendererListHandle rendererListHandle;
                public bool enabledMRT;
                public Material copyDepth, blitMaterial;
            }

            Material copyDepth, blitMaterial;
            FilteringSettings filteringSettings;
            Settings settings;
            static readonly RenderTargetIdentifier[] mrtTargets = new RenderTargetIdentifier[2];
            static readonly Color[] MRT_CLEAR_COLORS = new Color[2] { Color.black, Color.red };
            static readonly Vector4 SCALE_BIAS = new Vector4(1f, 1f, 0f, 0f);

            public void UpdateSettings(Settings settings)
            {
                this.settings = settings;
                this.renderPassEvent = settings.renderPassEvent;
                this.filteringSettings.layerMask= settings.layerMask;
            }
            
            public BlendedBufferPass(Settings settings)
            {
                this.filteringSettings = new FilteringSettings(RenderQueueRange.transparent);
                this.UpdateSettings(settings);

#if UNITY_EDITOR
                if (settings.copyDepthShader == null)
                    settings.copyDepthShader = Shader.Find("BlendedBuffer/CopyDepth");
                if (settings.blitShader == null)
                    settings.blitShader = Shader.Find("BlendedBuffer/Premultiply Blit");

                Debug.Assert(settings.copyDepthShader != null, "Copy Depth Shader is not found.");
                Debug.Assert(settings.blitShader != null, "Blit Shader is not found.");
#endif
                this.copyDepth = CoreUtils.CreateEngineMaterial(settings.copyDepthShader);
                this.blitMaterial = CoreUtils.CreateEngineMaterial(settings.blitShader);
            }

            public void Dispose()
            {
                CoreUtils.Destroy(this.copyDepth);
                CoreUtils.Destroy(this.blitMaterial);
                this.copyDepth = this.blitMaterial = null;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var lightData = frameData.Get<UniversalLightData>();

                var isGameCamera = cameraData.cameraType == CameraType.Game;
                var colorTarget = resourceData.activeColorTexture;
                var depthTarget = resourceData.activeDepthTexture;
                var alphaTarget = colorTarget;
                if (isGameCamera)
                {
                    // TODO: 自前のRTHandle作った方が良さげ
                    var desc = renderGraph.GetTextureDesc(resourceData.activeColorTexture);
                    var downSampling = (int)this.settings.downSampling;
                    var width = desc.width / downSampling;
                    var height = desc.height / downSampling;
                    desc.width = width;
                    desc.height = height;
                    if (this.settings.enabledMRT)
                        desc.format = GraphicsFormat.B10G11R11_UFloatPack32;
                    else
                        desc.format = GraphicsFormat.R16G16B16A16_SFloat;
                    desc.filterMode = this.settings.downSampleFilterMode;
                    desc.name = RENDER_TARGET_NAME;
                    colorTarget = renderGraph.CreateTexture(desc);
                    desc.format = GraphicsFormat.R8_SNorm;
                    alphaTarget = renderGraph.CreateTexture(desc);
                    desc.format = GraphicsFormat.D24_UNorm_S8_UInt;
                    desc.msaaSamples = MSAASamples.None;
                    desc.name = RENDER_TARGET_DEPTH_NAME;
                    depthTarget = renderGraph.CreateTexture(desc);
                }

                var drawSettings = RenderingUtils.CreateDrawingSettings(SHADER_TAG_ID, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);
                drawSettings.perObjectData = PerObjectData.None;
                var renderListParam = new RendererListParams(renderingData.cullResults, drawSettings, this.filteringSettings);
                var rendererListHandle = renderGraph.CreateRendererList(renderListParam);

                // NOTE:
                // 近景・遠景のVFXでLayerを分けて遠景の画面占有率が低いVFXに関しては直接バッファに書き込むアプローチもあるらしい
                // https://game.watch.impress.co.jp/docs/20081203/3dmg4.htm
                using (var builder = renderGraph.AddUnsafePass("Draw Downsampled Buffer", out PassData passData))
                {
                    passData.enabledMRT = this.settings.enabledMRT;
                    passData.srcColor = resourceData.activeColorTexture;
                    passData.srcDepth = resourceData.activeDepthTexture;
                    passData.color = colorTarget;
                    passData.alpha = alphaTarget;
                    passData.depth = depthTarget;
                    passData.rendererListHandle = rendererListHandle;
                    passData.copyDepth = this.copyDepth;
                    passData.blitMaterial = this.blitMaterial;
                    builder.UseRendererList(rendererListHandle);
                    builder.AllowPassCulling(false);
                    if (isGameCamera)
                    {
                        builder.UseTexture(in colorTarget, AccessFlags.Write);
                        if (this.settings.enabledMRT)
                            builder.UseTexture(in alphaTarget, AccessFlags.Write);
                        builder.UseTexture(in depthTarget, AccessFlags.Write);
                        builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                        {
                            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                            // Ready for downsampled buffer
                            if (data.enabledMRT)
                            {
                                data.blitMaterial.EnableKeyword("MRT");
                                data.blitMaterial.SetTexture(ALPHA_TEX_ID, alphaTarget);
                                mrtTargets[0] = data.color;
                                mrtTargets[1] = data.alpha;
                                cmd.SetRenderTarget(mrtTargets, data.depth);
                            }
                            else
                            {
                                data.blitMaterial.DisableKeyword("MRT");
                                data.blitMaterial.SetTexture(ALPHA_TEX_ID, null);
                                cmd.SetRenderTarget(data.color, data.depth);
                            }
                            cmd.ClearRenderTarget(RTClearFlags.All, MRT_CLEAR_COLORS, 1, 0);
                            Blitter.BlitTexture(cmd, data.srcDepth, SCALE_BIAS, data.copyDepth, 0);

                            // Transparent
                            cmd.DrawRendererList(data.rendererListHandle);

                            // Combine
                            cmd.SetRenderTarget(data.srcColor);
                            Blitter.BlitTexture(cmd, data.color, SCALE_BIAS, data.blitMaterial, 0);
                        });
                    }
                    else
                    {
                        builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                        {
                            var cmd = context.cmd;
                            cmd.SetRenderTarget(data.color, data.depth);
                            cmd.DrawRendererList(data.rendererListHandle);
                        });
                    }
                }
            }
        }

        public override void Create()
        {
            this.name = "BlendedBuffer";
            this.blendedBufferPass ??= new BlendedBufferPass(this.settings);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            this.blendedBufferPass.UpdateSettings(this.settings);
#endif
            renderer.EnqueuePass(this.blendedBufferPass);
        }

        // public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        // {
        // }

        protected override void Dispose(bool disposing)
        {
            this.blendedBufferPass?.Dispose();
            this.blendedBufferPass = null;
        }
    }
}
