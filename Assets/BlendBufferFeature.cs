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

            class PassData
            {
                public TextureHandle color, depth, srcColor, srcDepth;
                public RendererListHandle rendererListHandle;
            }

            Material copyDepth, blitMaterial;
            FilteringSettings filteringSettings;
            int downSampling = 1;
            FilterMode filterMode = FilterMode.Bilinear;

            public BlendedBufferPass(Settings settings)
            {
                this.renderPassEvent = settings.renderPassEvent;
                this.filteringSettings = new FilteringSettings(RenderQueueRange.transparent, settings.layerMask);
                this.downSampling = (int)settings.downSampling;
                
#if UNITY_EDITOR
                if (settings.copyDepthShader == null)
                    settings.copyDepthShader = Shader.Find("BlendedBuffer/CopyDepth");
                if (settings.blitShader == null)
                    settings.blitShader = Shader.Find("BlendedBuffer/Premultiply Blit");
                
                Debug.Assert(settings.copyDepthShader != null, "Copy Depth Shader is not found.");
                Debug.Assert(settings.blitShader != null, "Blit Shader is not found.");
#endif
                this.copyDepth = new Material(settings.copyDepthShader);
                this.blitMaterial = new Material(settings.blitShader);
            }

            public void Dispose()
            {
                CoreUtils.Destroy(this.copyDepth);
                CoreUtils.Destroy(this.blitMaterial);
                this.copyDepth = this.blitMaterial = null;
            }
            
#if UNITY_EDITOR
            public void UpdateSettings(Settings settings)
            {
                this.renderPassEvent = settings.renderPassEvent;
                this.filteringSettings.layerMask = settings.layerMask;
                this.downSampling = (int)settings.downSampling;
                this.filterMode = settings.downSampleFilterMode;
            }
#endif

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var lightData = frameData.Get<UniversalLightData>();
                
                var isGameCamera = cameraData.cameraType == CameraType.Game;
                var colorTarget = resourceData.activeColorTexture;
                var depthTarget = resourceData.activeDepthTexture;
                if (isGameCamera)
                {
                    var downDesc = renderGraph.GetTextureDesc(resourceData.activeColorTexture);
                    var width = downDesc.width / this.downSampling;
                    var height = downDesc.height / this.downSampling;
                    downDesc.width = width;
                    downDesc.height = height;
                    downDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
                    downDesc.filterMode = this.filterMode;
                    downDesc.name = RENDER_TARGET_NAME;
                    colorTarget = renderGraph.CreateTexture(downDesc);
                    downDesc.format = GraphicsFormat.D24_UNorm;
                    downDesc.msaaSamples = MSAASamples.None;
                    downDesc.name = RENDER_TARGET_DEPTH_NAME;
                    depthTarget = renderGraph.CreateTexture(downDesc);
                }

                var drawSettings = RenderingUtils.CreateDrawingSettings(SHADER_TAG_ID, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);
                drawSettings.perObjectData = PerObjectData.None;
                var renderListParam = new RendererListParams(renderingData.cullResults, drawSettings, this.filteringSettings);
                var rendererListHandle = renderGraph.CreateRendererList(renderListParam);
                
                // NOTE:
                // 近景・遠景のVFXでLayerを分けて遠景の画面占有率が低いVFXに関しては直接バッファに書き込むアプローチもあるらしい
                // https://game.watch.impress.co.jp/docs/20081203/3dmg4.htm
                using(var builder = renderGraph.AddUnsafePass("Draw Downsampled Buffer", out PassData passData))
                {
                    passData.srcColor = resourceData.activeColorTexture;
                    passData.srcDepth = resourceData.activeDepthTexture;
                    passData.color = colorTarget;
                    passData.depth = depthTarget;
                    passData.rendererListHandle = rendererListHandle;
                    builder.UseRendererList(rendererListHandle);
                    builder.AllowPassCulling(false);
                    if (isGameCamera)
                    {
                        builder.UseTexture(in colorTarget, AccessFlags.Write);
                        builder.UseTexture(in depthTarget, AccessFlags.Write);
                        builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                        {
                            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                            var scaleBias = new Vector4(1f, 1f, 0f, 0f);
                            
                            // Ready for downsampled buffer
                            cmd.SetRenderTarget(data.color, data.depth);
                            cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1, 0);
                            Blitter.BlitTexture(cmd, data.srcDepth, scaleBias, this.copyDepth, 0);

                            // Transparent
                            cmd.DrawRendererList(data.rendererListHandle);
                            
                            // Combine
                            cmd.SetRenderTarget(data.srcColor);
                            Blitter.BlitTexture(cmd, data.color, scaleBias, this.blitMaterial, 0);
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
