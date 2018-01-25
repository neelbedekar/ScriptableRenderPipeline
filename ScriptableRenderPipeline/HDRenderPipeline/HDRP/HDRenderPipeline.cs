using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using System;
using System.Diagnostics;
using UnityEngine.Rendering.PostProcessing;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public partial class HDRenderPipeline : RenderPipeline
    {
        enum ForwardPass
        {
            Opaque,
            PreRefraction,
            Transparent
        }

        static readonly string[] k_ForwardPassDebugName =
        {
            "Forward Opaque Debug",
            "Forward PreRefraction Debug",
            "Forward Transparent Debug"
        };

        static readonly string[] k_ForwardPassName =
        {
            "Forward Opaque",
            "Forward PreRefraction",
            "Forward Transparent"
        };

        static readonly RenderQueueRange k_RenderQueue_PreRefraction = new RenderQueueRange { min = (int)HDRenderQueue.PreRefraction, max = (int)HDRenderQueue.Transparent - 1 };
        static readonly RenderQueueRange k_RenderQueue_Transparent = new RenderQueueRange { min = (int)HDRenderQueue.Transparent, max = (int)HDRenderQueue.Overlay - 1 };
        static readonly RenderQueueRange k_RenderQueue_AllTransparent = new RenderQueueRange { min = (int)HDRenderQueue.PreRefraction, max = (int)HDRenderQueue.Overlay - 1 };

        readonly HDRenderPipelineAsset m_Asset;

        DiffusionProfileSettings m_InternalSSSAsset;
        public DiffusionProfileSettings diffusionProfileSettings
        {
            get
            {
                // If no SSS asset is set, build / reuse an internal one for simplicity
                var asset = m_Asset.diffusionProfileSettings;

                if (asset == null)
                {
                    if (m_InternalSSSAsset == null)
                        m_InternalSSSAsset = ScriptableObject.CreateInstance<DiffusionProfileSettings>();

                    asset = m_InternalSSSAsset;
                }

                return asset;
            }
        }

        public bool IsInternalDiffusionProfile(DiffusionProfileSettings profile)
        {
            return m_InternalSSSAsset == profile;
        }

        readonly RenderPipelineMaterial m_DeferredMaterial;
        readonly List<RenderPipelineMaterial> m_MaterialList = new List<RenderPipelineMaterial>();

        readonly GBufferManager m_GbufferManager;
        readonly DBufferManager m_DbufferManager;
        readonly SubsurfaceScatteringManager m_SSSBufferManager = new SubsurfaceScatteringManager();

        // Renderer Bake configuration can vary depends on if shadow mask is enabled or no
        RendererConfiguration m_currentRendererConfigurationBakedLighting = HDUtils.k_RendererConfigurationBakedLighting;
        Material m_CopyStencilForNoLighting;
        Material m_CopyDepth;
        GPUCopy m_GPUCopy;

        IBLFilterGGX m_IBLFilterGGX = null;

        ComputeShader m_GaussianPyramidCS { get { return m_Asset.renderPipelineResources.gaussianPyramidCS; } }
        int m_GaussianPyramidKernel;
        ComputeShader m_DepthPyramidCS { get { return m_Asset.renderPipelineResources.depthPyramidCS; } }
        int m_DepthPyramidKernel;

        ComputeShader m_applyDistortionCS { get { return m_Asset.renderPipelineResources.applyDistortionCS; } }
        int m_applyDistortionKernel;

        Material m_CameraMotionVectorsMaterial;

        // Debug material
        Material m_DebugViewMaterialGBuffer;
        Material m_DebugViewMaterialGBufferShadowMask;
        Material m_currentDebugViewMaterialGBuffer;
        Material m_DebugDisplayLatlong;
        Material m_DebugFullScreen;
        Material m_DebugColorPicker;
        Material m_Blit;
        Material m_ErrorMaterial;

        RenderTargetIdentifier[] m_MRTCache2 = new RenderTargetIdentifier[2];

        // 'm_CameraColorBuffer' does not contain diffuse lighting of SSS materials until the SSS pass. It is stored within 'm_CameraSssDiffuseLightingBuffer'.
        RTHandle m_CameraColorBuffer;
        RTHandle m_CameraSssDiffuseLightingBuffer;

        RTHandle m_CameraDepthStencilBuffer;
        RTHandle m_CameraDepthBufferCopy;
        RTHandle m_CameraStencilBufferCopy;

        RTHandle m_VelocityBuffer;
        RTHandle m_DeferredShadowBuffer;
        RTHandle m_AmbientOcclusionBuffer;
        RTHandle m_DistortionBuffer;

        RTHandle m_GaussianPyramidColorBuffer;
        List<RTHandle> m_GaussianPyramidColorMips = new List<RTHandle>();
        RTHandle m_DepthPyramidBuffer;
        List<RTHandle> m_DepthPyramidMips = new List<RTHandle>();

        // The pass "SRPDefaultUnlit" is a fall back to legacy unlit rendering and is required to support unity 2d + unity UI that render in the scene.
        ShaderPassName[] m_ForwardAndForwardOnlyPassNames = { new ShaderPassName(), new ShaderPassName(), HDShaderPassNames.s_SRPDefaultUnlitName };
        ShaderPassName[] m_ForwardOnlyPassNames = { new ShaderPassName(), HDShaderPassNames.s_SRPDefaultUnlitName };

        ShaderPassName[] m_AllTransparentPassNames = {  HDShaderPassNames.s_TransparentBackfaceName,
                                                        HDShaderPassNames.s_ForwardOnlyName,
                                                        HDShaderPassNames.s_ForwardName,
                                                        HDShaderPassNames.s_SRPDefaultUnlitName };

        ShaderPassName[] m_AllTransparentDebugDisplayPassNames = {  HDShaderPassNames.s_TransparentBackfaceDebugDisplayName,
                                                                    HDShaderPassNames.s_ForwardOnlyDebugDisplayName,
                                                                    HDShaderPassNames.s_ForwardDebugDisplayName,
                                                                    HDShaderPassNames.s_SRPDefaultUnlitName };

        ShaderPassName[] m_AllForwardDebugDisplayPassNames = {  HDShaderPassNames.s_TransparentBackfaceDebugDisplayName,
                                                                HDShaderPassNames.s_ForwardOnlyDebugDisplayName,
                                                                HDShaderPassNames.s_ForwardDebugDisplayName };

        ShaderPassName[] m_DepthOnlyAndDepthForwardOnlyPassNames = { HDShaderPassNames.s_DepthForwardOnlyName, HDShaderPassNames.s_DepthOnlyName };
        ShaderPassName[] m_DepthForwardOnlyPassNames = { HDShaderPassNames.s_DepthForwardOnlyName };
        ShaderPassName[] m_DepthOnlyPassNames = { HDShaderPassNames.s_DepthOnlyName };
        ShaderPassName[] m_TransparentDepthPrepassNames = { HDShaderPassNames.s_TransparentDepthPrepassName };
        ShaderPassName[] m_TransparentDepthPostpassNames = { HDShaderPassNames.s_TransparentDepthPostpassName };
        ShaderPassName[] m_ForwardErrorPassNames = { HDShaderPassNames.s_AlwaysName, HDShaderPassNames.s_ForwardBaseName, HDShaderPassNames.s_DeferredName, HDShaderPassNames.s_PrepassBaseName, HDShaderPassNames.s_VertexName, HDShaderPassNames.s_VertexLMRGBMName, HDShaderPassNames.s_VertexLMName };
        ShaderPassName[] m_SinglePassName = new ShaderPassName[1];

        // Stencil usage in HDRenderPipeline.
        // Currently we use only 2 bits to identify the kind of lighting that is expected from the render pipeline
        // Usage is define in LightDefinitions.cs
        [Flags]
        public enum StencilBitMask
        {
            Clear           = 0,             // 0x0
            LightingMask    = 7,             // 0x7  - 3 bit
            ObjectVelocity  = 128,           // 0x80 - 1 bit
            All             = 255            // 0xFF - 8 bit
        }

        RenderStateBlock m_DepthStateOpaque;
        RenderStateBlock m_DepthStateOpaqueWithPrepass;

        // Detect when windows size is changing
        int m_CurrentWidth;
        int m_CurrentHeight;

        // Use to detect frame changes
        int m_FrameCount;

        public int GetCurrentShadowCount() { return m_LightLoop.GetCurrentShadowCount(); }
        public int GetShadowAtlasCount() { return m_LightLoop.GetShadowAtlasCount(); }

        readonly SkyManager m_SkyManager = new SkyManager();
        readonly LightLoop m_LightLoop = new LightLoop();
        readonly ShadowSettings m_ShadowSettings = new ShadowSettings();

        // Debugging
        MaterialPropertyBlock           m_SharedPropertyBlock = new MaterialPropertyBlock();
        DebugDisplaySettings            m_DebugDisplaySettings = new DebugDisplaySettings();
        public DebugDisplaySettings     debugDisplaySettings { get { return m_DebugDisplaySettings; } }
        static DebugDisplaySettings     s_NeutralDebugDisplaySettings = new DebugDisplaySettings();
        DebugDisplaySettings            m_CurrentDebugDisplaySettings;
        RTHandle                        m_DebugColorPickerBuffer;
        RTHandle                        m_DebugFullScreenTempBuffer;
        bool                            m_FullScreenDebugPushed;

        FrameSettings m_FrameSettings; // Init every frame

        public HDRenderPipeline(HDRenderPipelineAsset asset)
        {
            SetRenderingFeatures();

            m_Asset = asset;
            m_GPUCopy = new GPUCopy(asset.renderPipelineResources.copyChannelCS);
            EncodeBC6H.DefaultInstance = EncodeBC6H.DefaultInstance ?? new EncodeBC6H(asset.renderPipelineResources.encodeBC6HCS);

            // Scan material list and assign it
            m_MaterialList = HDUtils.GetRenderPipelineMaterialList();
            // Find first material that have non 0 Gbuffer count and assign it as deferredMaterial
            m_DeferredMaterial = null;
            foreach (var material in m_MaterialList)
            {
                if (material.GetMaterialGBufferCount() > 0)
                    m_DeferredMaterial = material;
            }

            // TODO: Handle the case of no Gbuffer material
            // TODO: I comment the assert here because m_DeferredMaterial for whatever reasons contain the correct class but with a "null" in the name instead of the real name and then trigger the assert
            // whereas it work. Don't know what is happening, DebugDisplay use the same code and name is correct there.
            // Debug.Assert(m_DeferredMaterial != null);

            m_GbufferManager = new GBufferManager(m_DeferredMaterial, m_Asset.renderPipelineSettings.supportShadowMask);
            m_DbufferManager = new DBufferManager();

            m_SSSBufferManager.Build(asset);

            // Initialize various compute shader resources
            m_applyDistortionKernel = m_applyDistortionCS.FindKernel("KMain");

            // General material
            m_CopyStencilForNoLighting = CoreUtils.CreateEngineMaterial(asset.renderPipelineResources.copyStencilBuffer);
            m_CopyStencilForNoLighting.SetInt(HDShaderIDs._StencilRef, (int)StencilLightingUsage.NoLighting);
            m_CopyStencilForNoLighting.SetInt(HDShaderIDs._StencilMask, (int)StencilBitMask.LightingMask);
            m_CameraMotionVectorsMaterial = CoreUtils.CreateEngineMaterial(asset.renderPipelineResources.cameraMotionVectors);

            m_CopyDepth = CoreUtils.CreateEngineMaterial(asset.renderPipelineResources.copyDepthBuffer);

            InitializeDebugMaterials();

            m_GaussianPyramidKernel = m_GaussianPyramidCS.FindKernel("KMain");
            m_DepthPyramidKernel = m_DepthPyramidCS.FindKernel("KMain");

            m_MaterialList.ForEach(material => material.Build(asset));

            m_IBLFilterGGX = new IBLFilterGGX(asset.renderPipelineResources);

            m_LightLoop.Build(asset, m_ShadowSettings, m_IBLFilterGGX);

            m_SkyManager.Build(asset, m_IBLFilterGGX);

            m_DebugDisplaySettings.RegisterDebug();
            FrameSettings.RegisterDebug("Default Camera", m_Asset.GetFrameSettings());

            InitializeRenderTextures();

            // For debugging
            MousePositionDebug.instance.Build();

            InitializeRenderStateBlocks();
        }

        int GetPyramidSize()
        {
            // The monoscopic pyramid texture has both the width and height
            // matching, so the pyramid size could be either dimension.
            // However, with stereo double-wide rendering, we will arrange
            // two pyramid textures next to each other inside the double-wide
            // texture.  The whole texture width will no longer be representative
            // of the pyramid size, but the height still corresponds to the pyramid.
            return (int)m_GaussianPyramidColorBuffer.rt.height;
        }

        Vector2Int CalculatePyramidSize(Vector2Int size)
        {
            // for stereo double-wide, each half of the texture will represent a single eye's pyramid
            var widthModifier = 1;
            //if (m_Asset.renderPipelineSettings.supportsStereo && (desc.dimension != TextureDimension.Tex2DArray))
            //    widthModifier = 2; // double-wide

            int pyramidSize = (int)Mathf.ClosestPowerOfTwo(Mathf.Min(size.x, size.y));
            return new Vector2Int(pyramidSize * widthModifier, pyramidSize);
        }

        Vector2Int CalculatePyramidMipSize(Vector2Int baseMipSize, int mipIndex)
        {
            return new Vector2Int(baseMipSize.x >> mipIndex, baseMipSize.y >> mipIndex);
        }

        int GetPyramidLodCount()
        {
            var pyramidSideSize = GetPyramidSize();
            // The Gaussian pyramid compute works in blocks of 8x8 so make sure the last lod has a
            // minimum size of 8x8
            return Mathf.FloorToInt(Mathf.Log(pyramidSideSize, 2f) - 3f);
        }

        void UpdatePyramidMips(RenderTextureFormat format, List<RTHandle> mipList)
        {
            int lodCount = GetPyramidLodCount();
            int currentLodCount = mipList.Count;
            if (lodCount > currentLodCount)
            {
                for (int i = currentLodCount; i < lodCount; ++i)
                {
                    int localCopy = i; // Don't remove this copy! It's important for the value to be correctly captured by the lambda.
                    RTHandle newMip = RTHandle.Alloc(size => CalculatePyramidMipSize(CalculatePyramidSize(size), localCopy + 1), colorFormat: format, sRGB: false, enableRandomWrite: true, useMipMap: false, filterMode: FilterMode.Bilinear);
                    mipList.Add(newMip);
                }
            }
        }

        void InitializeRenderTextures()
        {
            m_GbufferManager.CreateBuffers();

            if(m_Asset.renderPipelineSettings.supportDBuffer)
                m_DbufferManager.CreateBuffers();

            m_SSSBufferManager.InitSSSBuffers(m_GbufferManager, m_Asset.renderPipelineSettings);

            m_CameraColorBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: RenderTextureFormat.ARGBHalf, sRGB : false, enableRandomWrite: true);
            m_CameraSssDiffuseLightingBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: RenderTextureFormat.RGB111110Float, sRGB: false, enableRandomWrite: true);

            m_CameraDepthStencilBuffer = RTHandle.Alloc(Vector2.one, depthBufferBits: DepthBits.Depth24, colorFormat: RenderTextureFormat.Depth, filterMode: FilterMode.Point);

            if (NeedDepthBufferCopy())
            {
                m_CameraDepthBufferCopy = RTHandle.Alloc(Vector2.one, depthBufferBits: DepthBits.Depth24, colorFormat: RenderTextureFormat.Depth, filterMode: FilterMode.Point);
            }

            // Technically we won't need this buffer in some cases, but nothing that we can determine at init time.
            m_CameraStencilBufferCopy = RTHandle.Alloc(Vector2.one, depthBufferBits: DepthBits.None, colorFormat: RenderTextureFormat.R8, sRGB: false, filterMode: FilterMode.Point); // DXGI_FORMAT_R8_UINT is not supported by Unity

            if (m_Asset.renderPipelineSettings.supportSSAO)
            {
                m_AmbientOcclusionBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Bilinear, colorFormat: RenderTextureFormat.R8, sRGB: false, enableRandomWrite: true);
            }

            if (m_Asset.renderPipelineSettings.supportsMotionVectors)
            {
                m_VelocityBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: Builtin.GetVelocityBufferFormat(), sRGB: Builtin.GetVelocityBuffer_sRGBFlag());
            }

            m_DistortionBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: Builtin.GetDistortionBufferFormat(), sRGB: Builtin.GetDistortionBuffer_sRGBFlag());

            m_DeferredShadowBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: RenderTextureFormat.ARGB32, sRGB: false, enableRandomWrite: true);

            m_GaussianPyramidColorBuffer = RTHandle.Alloc(size => CalculatePyramidSize(size), filterMode: FilterMode.Trilinear, colorFormat: RenderTextureFormat.ARGBHalf, sRGB: true, useMipMap: true, autoGenerateMips: false);
            m_DepthPyramidBuffer = RTHandle.Alloc(size => CalculatePyramidSize(size), filterMode: FilterMode.Trilinear, colorFormat: RenderTextureFormat.RFloat, sRGB: false, useMipMap: true, autoGenerateMips: false, enableRandomWrite: true); // Need randomReadWrite because we downsample the first mip with a compute shader.

            if (Debug.isDebugBuild)
            {
                m_DebugColorPickerBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: RenderTextureFormat.ARGBHalf, sRGB: false);
                m_DebugFullScreenTempBuffer = RTHandle.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: RenderTextureFormat.ARGBHalf, sRGB: false);
            }
        }

        void DestroyRenderTextures()
        {
            m_GbufferManager.DestroyBuffers();
            m_DbufferManager.DestroyBuffers();

            RTHandle.Release(m_CameraColorBuffer);
            RTHandle.Release(m_CameraSssDiffuseLightingBuffer);

            RTHandle.Release(m_CameraDepthStencilBuffer);
            RTHandle.Release(m_CameraDepthBufferCopy);
            RTHandle.Release(m_CameraStencilBufferCopy);

            RTHandle.Release(m_AmbientOcclusionBuffer);
            RTHandle.Release(m_VelocityBuffer);
            RTHandle.Release(m_DistortionBuffer);
            RTHandle.Release(m_DeferredShadowBuffer);

            RTHandle.Release(m_GaussianPyramidColorBuffer);
            RTHandle.Release(m_DepthPyramidBuffer);

            RTHandle.Release(m_DebugColorPickerBuffer);
            RTHandle.Release(m_DebugFullScreenTempBuffer);
        }


        void SetRenderingFeatures()
        {
            // HD use specific GraphicsSettings
            GraphicsSettings.lightsUseLinearIntensity = true;
            GraphicsSettings.lightsUseColorTemperature = true;

            SupportedRenderingFeatures.active = new SupportedRenderingFeatures()
            {
                reflectionProbeSupportFlags = SupportedRenderingFeatures.ReflectionProbeSupportFlags.Rotation,
                defaultMixedLightingMode = SupportedRenderingFeatures.LightmapMixedBakeMode.IndirectOnly,
                supportedMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeMode.IndirectOnly | SupportedRenderingFeatures.LightmapMixedBakeMode.Shadowmask,
                supportedLightmapBakeTypes = LightmapBakeType.Baked | LightmapBakeType.Mixed | LightmapBakeType.Realtime,
                supportedLightmapsModes = LightmapsMode.NonDirectional | LightmapsMode.CombinedDirectional,
                rendererSupportsLightProbeProxyVolumes = true,
                rendererSupportsMotionVectors = true,
                rendererSupportsReceiveShadows = true,
                rendererSupportsReflectionProbes = true
            };
        }

        void InitializeDebugMaterials()
        {
            m_DebugViewMaterialGBuffer = CoreUtils.CreateEngineMaterial(m_Asset.renderPipelineResources.debugViewMaterialGBufferShader);
            m_DebugViewMaterialGBufferShadowMask = CoreUtils.CreateEngineMaterial(m_Asset.renderPipelineResources.debugViewMaterialGBufferShader);
            m_DebugViewMaterialGBufferShadowMask.EnableKeyword("SHADOWS_SHADOWMASK");
            m_DebugDisplayLatlong = CoreUtils.CreateEngineMaterial(m_Asset.renderPipelineResources.debugDisplayLatlongShader);
            m_DebugFullScreen = CoreUtils.CreateEngineMaterial(m_Asset.renderPipelineResources.debugFullScreenShader);
            m_DebugColorPicker = CoreUtils.CreateEngineMaterial(m_Asset.renderPipelineResources.debugColorPickerShader);
            m_Blit = CoreUtils.CreateEngineMaterial(m_Asset.renderPipelineResources.blit);
            m_ErrorMaterial = CoreUtils.CreateEngineMaterial("Hidden/InternalErrorShader");
        }

        void InitializeRenderStateBlocks()
        {
            m_DepthStateOpaque = new RenderStateBlock
            {
                depthState = new DepthState(true, CompareFunction.LessEqual),
                mask = RenderStateMask.Depth
            };

            // When doing a prepass, we don't need to write the depth anymore.
            // Moreover, we need to use DepthEqual because for alpha tested materials we don't do the clip in the shader anymore (otherwise HiZ does not work on PS4)
            m_DepthStateOpaqueWithPrepass = new RenderStateBlock
            {
                depthState = new DepthState(false, CompareFunction.Equal),
                mask = RenderStateMask.Depth
            };
        }

        public void OnSceneLoad()
        {
            // Recreate the textures which went NULL
            m_MaterialList.ForEach(material => material.Build(m_Asset));
        }

        public override void Dispose()
        {
            base.Dispose();

            m_LightLoop.Cleanup();

            // For debugging
            MousePositionDebug.instance.Cleanup();

            m_MaterialList.ForEach(material => material.Cleanup());

            CoreUtils.Destroy(m_CopyStencilForNoLighting);
            CoreUtils.Destroy(m_CameraMotionVectorsMaterial);

            CoreUtils.Destroy(m_DebugViewMaterialGBuffer);
            CoreUtils.Destroy(m_DebugViewMaterialGBufferShadowMask);
            CoreUtils.Destroy(m_DebugDisplayLatlong);
            CoreUtils.Destroy(m_DebugFullScreen);
            CoreUtils.Destroy(m_DebugColorPicker);
            CoreUtils.Destroy(m_Blit);
            CoreUtils.Destroy(m_ErrorMaterial);

            m_SSSBufferManager.Cleanup();
            m_SkyManager.Cleanup();

            DestroyRenderTextures();

            SupportedRenderingFeatures.active = new SupportedRenderingFeatures();
        }

        void Resize(HDCamera hdCamera)
        {
            bool resolutionChanged = (hdCamera.actualWidth != m_CurrentWidth) || (hdCamera.actualHeight != m_CurrentHeight);

            if (resolutionChanged || m_LightLoop.NeedResize())
            {
                if (m_CurrentWidth > 0 && m_CurrentHeight > 0)
                    m_LightLoop.ReleaseResolutionDependentBuffers();

                m_LightLoop.AllocResolutionDependentBuffers(hdCamera.actualWidth, hdCamera.actualHeight);
            }

            int viewId = hdCamera.camera.GetInstanceID(); // Warning: different views can use the same camera

            // Warning: (resolutionChanged == false) if you open a new Editor tab of the same size!
            if (m_VolumetricLightingPreset != VolumetricLightingPreset.Off)
                ResizeVBuffer(viewId, hdCamera.actualWidth, hdCamera.actualHeight);

            // update recorded window resolution
            m_CurrentWidth = hdCamera.actualWidth;
            m_CurrentHeight = hdCamera.actualHeight;
        }

        public void PushGlobalParams(HDCamera hdCamera, CommandBuffer cmd, DiffusionProfileSettings sssParameters)
        {
            using (new ProfilingSample(cmd, "Push Global Parameters", CustomSamplerId.PushGlobalParameters.GetSampler()))
            {
                hdCamera.SetupGlobalParams(cmd);

                m_SSSBufferManager.PushGlobalParams(cmd, sssParameters, m_FrameSettings);

                m_DbufferManager.PushGlobalParams(cmd);

                if (m_VolumetricLightingPreset != VolumetricLightingPreset.Off)
                {
                    SetVolumetricLightingData(hdCamera, cmd);
                }
            }
        }

        bool NeedDepthBufferCopy()
        {
            // For now we consider only PS4 to be able to read from a bound depth buffer.
            // TODO: test/implement for other platforms.
            return SystemInfo.graphicsDeviceType != GraphicsDeviceType.PlayStation4 &&
                    SystemInfo.graphicsDeviceType != GraphicsDeviceType.XboxOne &&
                    SystemInfo.graphicsDeviceType != GraphicsDeviceType.XboxOneD3D12;
        }

        bool NeedStencilBufferCopy()
        {
            // Currently, Unity does not offer a way to bind the stencil buffer as a texture in a compute shader.
            // Therefore, it's manually copied using a pixel shader.
            return m_LightLoop.GetFeatureVariantsEnabled();
        }

        RTHandle GetDepthTexture()
        {
            return NeedDepthBufferCopy() ? m_CameraDepthBufferCopy : m_CameraDepthStencilBuffer;
        }

        void CopyDepthBufferIfNeeded(CommandBuffer cmd)
        {
            using (new ProfilingSample(cmd, NeedDepthBufferCopy() ? "Copy DepthBuffer" : "Set DepthBuffer", CustomSamplerId.CopySetDepthBuffer.GetSampler()))
            {
                if (NeedDepthBufferCopy())
                {
                    using (new ProfilingSample(cmd, "Copy depth-stencil buffer", CustomSamplerId.CopyDepthStencilbuffer.GetSampler()))
                    {
                        cmd.CopyTexture(m_CameraDepthStencilBuffer, m_CameraDepthBufferCopy);
                    }
                }
            }
        }

        public void UpdateShadowSettings()
        {
            var shadowSettings = VolumeManager.instance.stack.GetComponent<HDShadowSettings>();

            m_ShadowSettings.maxShadowDistance = shadowSettings.maxShadowDistance;
            //m_ShadowSettings.directionalLightNearPlaneOffset = commonSettings.shadowNearPlaneOffset;
        }

        public void ConfigureForShadowMask(bool enableBakeShadowMask, CommandBuffer cmd)
        {
            // Globally enable (for GBuffer shader and forward lit (opaque and transparent) the keyword SHADOWS_SHADOWMASK
            CoreUtils.SetKeyword(cmd, "SHADOWS_SHADOWMASK", enableBakeShadowMask);

            // Configure material to use depends on shadow mask option
            m_currentRendererConfigurationBakedLighting = enableBakeShadowMask ? HDUtils.k_RendererConfigurationBakedLightingWithShadowMask : HDUtils.k_RendererConfigurationBakedLighting;
            m_currentDebugViewMaterialGBuffer = enableBakeShadowMask ? m_DebugViewMaterialGBufferShadowMask : m_DebugViewMaterialGBuffer;
        }

        CullResults m_CullResults;
        public override void Render(ScriptableRenderContext renderContext, Camera[] cameras)
        {
            base.Render(renderContext, cameras);

            if (m_FrameCount != Time.frameCount)
            {
                HDCamera.CleanUnused();
                m_FrameCount = Time.frameCount;
            }

            foreach (var camera in cameras)
            {
                if (camera == null)
                    continue;

                // First, get aggregate of frame settings base on global settings, camera frame settings and debug settings
                var additionalCameraData = camera.GetComponent<HDAdditionalCameraData>();
                // Note: the scene view camera will never have additionalCameraData
                var srcFrameSettings = (additionalCameraData && additionalCameraData.renderingPath != HDAdditionalCameraData.RenderingPath.Default)
                    ? additionalCameraData.GetFrameSettings()
                    : m_Asset.GetFrameSettings();
                FrameSettings.InitializeFrameSettings(camera, m_Asset.GetRenderPipelineSettings(), srcFrameSettings, ref m_FrameSettings);

                // This is the main command buffer used for the frame.
                var cmd = CommandBufferPool.Get("");

                // Init material if needed
                // TODO: this should be move outside of the camera loop but we have no command buffer, ask details to Tim or Julien to do this
                if (!m_IBLFilterGGX.IsInitialized())
                    m_IBLFilterGGX.Initialize(cmd);

                foreach (var material in m_MaterialList)
                    material.RenderInit(cmd);

                using (new ProfilingSample(cmd, "HDRenderPipeline::Render", CustomSamplerId.HDRenderPipelineRender.GetSampler()))
                {
                    // Do anything we need to do upon a new frame.
                    m_LightLoop.NewFrame(m_FrameSettings);

                    // If we render a reflection view or a preview we should not display any debug information
                    // This need to be call before ApplyDebugDisplaySettings()
                    if (camera.cameraType == CameraType.Reflection || camera.cameraType == CameraType.Preview)
                    {
                        // Neutral allow to disable all debug settings
                        m_CurrentDebugDisplaySettings = s_NeutralDebugDisplaySettings;
                    }
                    else
                    {
                        m_CurrentDebugDisplaySettings = m_DebugDisplaySettings;

                        using (new ProfilingSample(cmd, "Volume Update", CustomSamplerId.VolumeUpdate.GetSampler()))
                        {
                            LayerMask layerMask = -1;
                            if (additionalCameraData != null)
                            {
                                layerMask = additionalCameraData.volumeLayerMask;
                            }
                            else
                            {
                                // Temporary hack. For scene view, by default, we don't want to have the lighting override layers in the current sky.
                                // This is arbitrary and should be editable in the scene view somehow.
                                if (camera.cameraType == CameraType.SceneView)
                                {
                                    layerMask = (-1 & ~m_Asset.renderPipelineSettings.lightLoopSettings.skyLightingOverrideLayerMask);
                                }
                            }
                            VolumeManager.instance.Update(camera.transform, layerMask);
                        }
                    }

                    var postProcessLayer = camera.GetComponent<PostProcessLayer>();
                    var hdCamera = HDCamera.Get(camera, postProcessLayer, m_FrameSettings);

                    Resize(hdCamera);


                    ApplyDebugDisplaySettings(hdCamera, cmd);
                    UpdateShadowSettings();

                    // TODO: Float HDCamera setup higher in order to pass stereo into GetCullingParameters
                    ScriptableCullingParameters cullingParams;
                    if (!CullResults.GetCullingParameters(camera, out cullingParams))
                    {
                        renderContext.Submit();
                        continue;
                    }

                    m_LightLoop.UpdateCullingParameters(ref cullingParams);

#if UNITY_EDITOR
                    // emit scene view UI
                    if (camera.cameraType == CameraType.SceneView)
                    {
                        ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
                    }
#endif
                    // decal system needs to be updated with current camera
                    if (m_FrameSettings.enableDBuffer)
                        DecalSystem.instance.BeginCull(camera);

                    using (new ProfilingSample(cmd, "CullResults.Cull", CustomSamplerId.CullResultsCull.GetSampler()))
                    {
                        CullResults.Cull(ref cullingParams, renderContext, ref m_CullResults);
                    }

                    m_DbufferManager.vsibleDecalCount = 0;
                    if (m_FrameSettings.enableDBuffer)
                    {
                        m_DbufferManager.vsibleDecalCount = DecalSystem.instance.QueryCullResults();
                        DecalSystem.instance.EndCull();
                    }

                    if (m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled())
                    {
                        m_CurrentDebugDisplaySettings.UpdateMaterials();
                    }

                    renderContext.SetupCameraProperties(camera);

                    PushGlobalParams(hdCamera, cmd, diffusionProfileSettings);

                    // TODO: Find a correct place to bind these material textures
                    // We have to bind the material specific global parameters in this mode
                    m_MaterialList.ForEach(material => material.Bind());

                    if (additionalCameraData && additionalCameraData.renderingPath == HDAdditionalCameraData.RenderingPath.Unlit)
                    {
                        // TODO: Add another path dedicated to planar reflection / real time cubemap that implement simpler lighting
                        // It is up to the users to only send unlit object for this camera path

                        using (new ProfilingSample(cmd, "Forward", CustomSamplerId.Forward.GetSampler()))
                        {
                            HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraColorBuffer, m_CameraDepthStencilBuffer, ClearFlag.Color | ClearFlag.Depth);
                            RenderOpaqueRenderList(m_CullResults, camera, renderContext, cmd, HDShaderPassNames.s_ForwardName);
                            RenderTransparentRenderList(m_CullResults, camera, renderContext, cmd, HDShaderPassNames.s_ForwardName);
                        }

                        renderContext.ExecuteCommandBuffer(cmd);
                        CommandBufferPool.Release(cmd);
                        renderContext.Submit();
                        continue;
                    }

                    // Note: Legacy Unity behave like this for ShadowMask
                    // When you select ShadowMask in Lighting panel it recompile shaders on the fly with the SHADOW_MASK keyword.
                    // However there is no C# function that we can query to know what mode have been select in Lighting Panel and it will be wrong anyway. Lighting Panel setup what will be the next bake mode. But until light is bake, it is wrong.
                    // Currently to know if you need shadow mask you need to go through all visible lights (of CullResult), check the LightBakingOutput struct and look at lightmapBakeType/mixedLightingMode. If one light have shadow mask bake mode, then you need shadow mask features (i.e extra Gbuffer).
                    // It mean that when we build a standalone player, if we detect a light with bake shadow mask, we generate all shader variant (with and without shadow mask) and at runtime, when a bake shadow mask light is visible, we dynamically allocate an extra GBuffer and switch the shader.
                    // So the first thing to do is to go through all the light: PrepareLightsForGPU
                    bool enableBakeShadowMask;
                    using (new ProfilingSample(cmd, "TP_PrepareLightsForGPU", CustomSamplerId.TPPrepareLightsForGPU.GetSampler()))
                    {
                        enableBakeShadowMask = m_LightLoop.PrepareLightsForGPU(cmd, m_ShadowSettings, m_CullResults, camera) && m_FrameSettings.enableShadowMask;
                    }
                    ConfigureForShadowMask(enableBakeShadowMask, cmd);

                    ClearBuffers(hdCamera, cmd);

                    bool forcePrepassForDecals = m_DbufferManager.vsibleDecalCount > 0;
                    RenderDepthPrepass(m_CullResults, hdCamera, renderContext, cmd, forcePrepassForDecals);

                    RenderObjectsVelocity(m_CullResults, hdCamera, renderContext, cmd);

                    RenderDBuffer(hdCamera, renderContext, cmd);

                    RenderGBuffer(m_CullResults, hdCamera, renderContext, cmd);

                    // In both forward and deferred, everything opaque should have been rendered at this point so we can safely copy the depth buffer for later processing.
                    CopyDepthBufferIfNeeded(cmd);

                    RenderCameraVelocity(m_CullResults, hdCamera, renderContext, cmd);

                    // Depth texture is now ready, bind it.
                    cmd.SetGlobalTexture(HDShaderIDs._MainDepthTexture, GetDepthTexture());

                    // Caution: We require sun light here as some skies use the sun light to render, it means that UpdateSkyEnvironment must be called after PrepareLightsForGPU.
                    // TODO: Try to arrange code so we can trigger this call earlier and use async compute here to run sky convolution during other passes (once we move convolution shader to compute).
                    UpdateSkyEnvironment(hdCamera, cmd);

                    RenderPyramidDepth(camera, cmd, renderContext, FullScreenDebugMode.DepthPyramid);


                    if (m_CurrentDebugDisplaySettings.IsDebugMaterialDisplayEnabled())
                    {
                        RenderDebugViewMaterial(m_CullResults, hdCamera, renderContext, cmd);

                        PushColorPickerDebugTexture(cmd, m_CameraColorBuffer, hdCamera);
                    }
                    else
                    {
                        using (new ProfilingSample(cmd, "Render SSAO", CustomSamplerId.RenderSSAO.GetSampler()))
                        {
                            // TODO: Everything here (SSAO, Shadow, Build light list, deferred shadow, material and light classification can be parallelize with Async compute)
                            RenderSSAO(cmd, hdCamera, renderContext, postProcessLayer);
                        }

                        GPUFence buildGPULightListsCompleteFence = new GPUFence();
                        if (m_FrameSettings.enableAsyncCompute)
                        {
                            GPUFence startFence = cmd.CreateGPUFence();
                            renderContext.ExecuteCommandBuffer(cmd);
                            cmd.Clear();

                            buildGPULightListsCompleteFence = m_LightLoop.BuildGPULightListsAsyncBegin(camera, renderContext, m_CameraDepthStencilBuffer, m_CameraStencilBufferCopy, startFence, m_SkyManager.IsSkyValid());
                        }

                        using (new ProfilingSample(cmd, "Render shadows", CustomSamplerId.RenderShadows.GetSampler()))
                        {
                            m_LightLoop.RenderShadows(renderContext, cmd, m_CullResults);
                            // TODO: check if statement below still apply
                            renderContext.SetupCameraProperties(camera); // Need to recall SetupCameraProperties after RenderShadows as it modify our view/proj matrix
                        }

                        using (new ProfilingSample(cmd, "Deferred directional shadows", CustomSamplerId.RenderDeferredDirectionalShadow.GetSampler()))
                        {
                            m_LightLoop.RenderDeferredDirectionalShadow(hdCamera, m_DeferredShadowBuffer, GetDepthTexture(), cmd);
                            PushFullScreenDebugTexture(cmd, m_DeferredShadowBuffer, hdCamera, FullScreenDebugMode.DeferredShadows);
                        }

                        // TODO: Move this code inside LightLoop
                        if (m_LightLoop.GetFeatureVariantsEnabled())
                        {
                            // For material classification we use compute shader and so can't read into the stencil, so prepare it.
                            using (new ProfilingSample(cmd, "Clear and copy stencil texture", CustomSamplerId.ClearAndCopyStencilTexture.GetSampler()))
                            {
                                HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraStencilBufferCopy, ClearFlag.Color, CoreUtils.clearColorAllBlack);

                                // In the material classification shader we will simply test is we are no lighting
                                // Use ShaderPassID 1 => "Pass 1 - Write 1 if value different from stencilRef to output"
                                CoreUtils.DrawFullScreen(cmd, m_CopyStencilForNoLighting, m_CameraStencilBufferCopy, m_CameraDepthStencilBuffer, null, 1);
                            }
                        }

                        if (m_FrameSettings.enableAsyncCompute)
                        {
                            m_LightLoop.BuildGPULightListAsyncEnd(camera, cmd, buildGPULightListsCompleteFence);
                        }
                        else
                        {
                            using (new ProfilingSample(cmd, "Build Light list", CustomSamplerId.BuildLightList.GetSampler()))
                            {
                                m_LightLoop.BuildGPULightLists(camera, cmd, m_CameraDepthStencilBuffer, m_CameraStencilBufferCopy, m_SkyManager.IsSkyValid());
                            }
                        }

                        // Render the volumetric lighting.
                        // The pass requires the volume properties, the light list and the shadows, and can run async.
                        VolumetricLightingPass(hdCamera, cmd);

                        RenderDeferredLighting(hdCamera, cmd);

                        RenderForward(m_CullResults, hdCamera, renderContext, cmd, ForwardPass.Opaque);
                        RenderForwardError(m_CullResults, hdCamera, renderContext, cmd, ForwardPass.Opaque);

                        // SSS pass here handle both SSS material from deferred and forward
                        m_SSSBufferManager.SubsurfaceScatteringPass(hdCamera, cmd, diffusionProfileSettings, m_FrameSettings,
                                                                    m_CameraColorBuffer, m_CameraSssDiffuseLightingBuffer, m_CameraDepthStencilBuffer, GetDepthTexture());

                        RenderSky(hdCamera, cmd);

                        // Render pre refraction objects
                        RenderForward(m_CullResults, hdCamera, renderContext, cmd, ForwardPass.PreRefraction);
                        RenderForwardError(m_CullResults, hdCamera, renderContext, cmd, ForwardPass.PreRefraction);

                        RenderGaussianPyramidColor(camera, cmd, renderContext, true);

                        // Render all type of transparent forward (unlit, lit, complex (hair...)) to keep the sorting between transparent objects.
                        RenderForward(m_CullResults, hdCamera, renderContext, cmd, ForwardPass.Transparent);
                        RenderForwardError(m_CullResults, hdCamera, renderContext, cmd, ForwardPass.Transparent);

                        // Fill depth buffer to reduce artifact for transparent object during postprocess
                        RenderTransparentDepthPostpass(m_CullResults, hdCamera, renderContext, cmd, ForwardPass.Transparent);

                        PushFullScreenDebugTexture(cmd, m_CameraColorBuffer, hdCamera, FullScreenDebugMode.NanTracker);

                        RenderGaussianPyramidColor(camera, cmd, renderContext, false);

                        AccumulateDistortion(m_CullResults, hdCamera, renderContext, cmd);
                        RenderDistortion(cmd, m_Asset.renderPipelineResources, hdCamera);

                        PushColorPickerDebugTexture(cmd, m_CameraColorBuffer, hdCamera);

                        // Final blit
                        if (m_FrameSettings.enablePostprocess && CoreUtils.IsPostProcessingActive(postProcessLayer))
                        {
                            RenderPostProcess(hdCamera, cmd, postProcessLayer);
                        }
                        else
                        {
                            using (new ProfilingSample(cmd, "Blit to final RT", CustomSamplerId.CopyDepthForSceneView.GetSampler()))
                            {
                                // Simple blit
                                HDUtils.BlitCameraTexture(cmd, hdCamera, m_CameraColorBuffer, BuiltinRenderTextureType.CameraTarget);
                            }
                        }
                    }


#if UNITY_EDITOR
                    // During rendering we use our own depth buffer instead of the one provided by the scene view (because we need to be able to control its life cycle)
                    // In order for scene view gizmos/icons etc to be depth test correctly, we need to copy the content of our own depth buffer into the scene view depth buffer.
                    // On subtlety here is that our buffer can be bigger than the camera one so we need to copy only the corresponding portion
                    // (it's handled automatically by the copy shader because it uses a load in pxiel coordinates based on the target).
                    // This copy will also have the effect of re-binding this depth buffer correctly for subsequent editor rendering.

                    // NOTE: This needs to be done before the call to RenderDebug because debug overlays need to update the depth for the scene view as well.
                    // Make sure RenderDebug does not change the current Render Target
                    if (camera.cameraType == CameraType.SceneView)
                    {
                        using (new ProfilingSample(cmd, "Copy Depth For SceneView", CustomSamplerId.BlitToFinalRT.GetSampler()))
                        {
                            m_CopyDepth.SetTexture(HDShaderIDs._InputDepth, m_CameraDepthStencilBuffer);
                            cmd.Blit(null, BuiltinRenderTextureType.CameraTarget, m_CopyDepth);
                        }
                    }
#endif

                    RenderDebug(hdCamera, cmd);
                }

                // Caution: ExecuteCommandBuffer must be outside of the profiling bracket
                renderContext.ExecuteCommandBuffer(cmd);

                CommandBufferPool.Release(cmd);
                renderContext.Submit();
            } // For each camera
        }

        void RenderOpaqueRenderList(CullResults cull,
                                    Camera camera,
                                    ScriptableRenderContext renderContext,
                                    CommandBuffer cmd,
                                    ShaderPassName passName,
                                    RendererConfiguration rendererConfiguration = 0,
                                    RenderQueueRange? inRenderQueueRange = null,
                                    RenderStateBlock? stateBlock = null,
                                    Material overrideMaterial = null)
        {
            m_SinglePassName[0] = passName;
            RenderOpaqueRenderList(cull, camera, renderContext, cmd, m_SinglePassName, rendererConfiguration, inRenderQueueRange, stateBlock, overrideMaterial);
        }

        void RenderOpaqueRenderList(CullResults cull,
                                    Camera camera,
                                    ScriptableRenderContext renderContext,
                                    CommandBuffer cmd,
                                    ShaderPassName[] passNames,
                                    RendererConfiguration rendererConfiguration = 0,
                                    RenderQueueRange? inRenderQueueRange = null,
                                    RenderStateBlock? stateBlock = null,
                                    Material overrideMaterial = null)
        {
            if (!m_FrameSettings.enableOpaqueObjects)
                return;

            // This is done here because DrawRenderers API lives outside command buffers so we need to make call this before doing any DrawRenders
            renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            var drawSettings = new DrawRendererSettings(camera, HDShaderPassNames.s_EmptyName)
            {
                rendererConfiguration = rendererConfiguration,
                sorting = { flags = SortFlags.CommonOpaque }
            };

            for (int i = 0; i < passNames.Length; ++i)
            {
                drawSettings.SetShaderPassName(i, passNames[i]);
            }

            if (overrideMaterial != null)
                drawSettings.SetOverrideMaterial(overrideMaterial, 0);

            var filterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = inRenderQueueRange == null ? RenderQueueRange.opaque : inRenderQueueRange.Value
            };

            if (stateBlock == null)
                renderContext.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);
            else
                renderContext.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings, stateBlock.Value);
        }

        void RenderTransparentRenderList(CullResults cull,
                                         Camera camera,
                                         ScriptableRenderContext renderContext,
                                         CommandBuffer cmd,
                                         ShaderPassName passName,
                                         RendererConfiguration rendererConfiguration = 0,
                                         RenderQueueRange? inRenderQueueRange = null,
                                         RenderStateBlock? stateBlock = null,
                                         Material overrideMaterial = null)
        {
            m_SinglePassName[0] = passName;
            RenderTransparentRenderList(cull, camera, renderContext, cmd, m_SinglePassName,
                                        rendererConfiguration, inRenderQueueRange, stateBlock, overrideMaterial);
        }

        void RenderTransparentRenderList(CullResults cull,
                                         Camera camera,
                                         ScriptableRenderContext renderContext,
                                         CommandBuffer cmd,
                                         ShaderPassName[] passNames,
                                         RendererConfiguration rendererConfiguration = 0,
                                         RenderQueueRange? inRenderQueueRange = null,
                                         RenderStateBlock? stateBlock = null,
                                         Material overrideMaterial = null
                                         )
        {
            if (!m_FrameSettings.enableTransparentObjects)
                return;

            // This is done here because DrawRenderers API lives outside command buffers so we need to make call this before doing any DrawRenders
            renderContext.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            var drawSettings = new DrawRendererSettings(camera, HDShaderPassNames.s_EmptyName)
            {
                rendererConfiguration = rendererConfiguration,
                sorting = { flags = SortFlags.CommonTransparent }
            };

            for (int i = 0; i < passNames.Length; ++i)
            {
                drawSettings.SetShaderPassName(i, passNames[i]);
            }

            if (overrideMaterial != null)
                drawSettings.SetOverrideMaterial(overrideMaterial, 0);

            var filterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = inRenderQueueRange == null ? k_RenderQueue_AllTransparent : inRenderQueueRange.Value
            };

            if (stateBlock == null)
                renderContext.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);
            else
                renderContext.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings, stateBlock.Value);
        }

        void AccumulateDistortion(CullResults cullResults, HDCamera hdCamera, ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (!m_FrameSettings.enableDistortion)
                return;

            using (new ProfilingSample(cmd, "Distortion", CustomSamplerId.Distortion.GetSampler()))
            {
                cmd.SetRenderTarget(m_DistortionBuffer, m_CameraDepthStencilBuffer);
                cmd.ClearRenderTarget(false, true, Color.clear);

                // Only transparent object can render distortion vectors
                RenderTransparentRenderList(cullResults, hdCamera.camera, renderContext, cmd, HDShaderPassNames.s_DistortionVectorsName);
            }
        }

        void RenderDistortion(CommandBuffer cmd, RenderPipelineResources resources, HDCamera hdCamera)
        {
            if (!m_FrameSettings.enableDistortion)
                return;

            using (new ProfilingSample(cmd, "ApplyDistortion", CustomSamplerId.ApplyDistortion.GetSampler()))
            {
                var size = new Vector4(hdCamera.screenSize.x, hdCamera.screenSize.y, 1f / hdCamera.screenSize.x, 1f / hdCamera.screenSize.y);
                uint x, y, z;
                m_applyDistortionCS.GetKernelThreadGroupSizes(m_applyDistortionKernel, out x, out y, out z);
                cmd.SetComputeTextureParam(m_applyDistortionCS, m_applyDistortionKernel, HDShaderIDs._DistortionTexture, m_DistortionBuffer);
                cmd.SetComputeTextureParam(m_applyDistortionCS, m_applyDistortionKernel, HDShaderIDs._GaussianPyramidColorTexture, m_GaussianPyramidColorBuffer);
                cmd.SetComputeTextureParam(m_applyDistortionCS, m_applyDistortionKernel, HDShaderIDs._CameraColorTexture, m_CameraColorBuffer);
                cmd.SetComputeVectorParam(m_applyDistortionCS, HDShaderIDs._Size, size);
                cmd.SetComputeVectorParam(m_applyDistortionCS, HDShaderIDs._ZBufferParams, Shader.GetGlobalVector(HDShaderIDs._ZBufferParams));
                cmd.SetComputeVectorParam(m_applyDistortionCS, HDShaderIDs._GaussianPyramidColorMipSize, Shader.GetGlobalVector(HDShaderIDs._GaussianPyramidColorMipSize));

                cmd.DispatchCompute(m_applyDistortionCS, m_applyDistortionKernel, Mathf.CeilToInt(size.x / x), Mathf.CeilToInt(size.y / y), 1);
            }
        }

        // RenderDepthPrepass render both opaque and opaque alpha tested based on engine configuration.
        // Forward only renderer: We always render everything
        // Deferred renderer: We render a depth prepass only if engine request it. We can decide if we render everything or only opaque alpha tested object.
        // Forward opaque with deferred renderer (DepthForwardOnly pass): We always render everything
        void RenderDepthPrepass(CullResults cull, HDCamera hdCamera, ScriptableRenderContext renderContext, CommandBuffer cmd, bool forcePrepass)
        {
            // In case of deferred renderer, we can have forward opaque material. These materials need to be render in the depth buffer to correctly build the light list.
            // And they will tag the stencil to not be lit during the deferred lighting pass.

            // Guidelines: In deferred by default there is no opaque in forward. However it is possible to force an opaque material to render in forward
            // by using the pass "ForwardOnly". In this case the .shader should not have "Forward" but only a "ForwardOnly" pass.
            // It must also have a "DepthForwardOnly" and no "DepthOnly" pass as forward material (either deferred or forward only rendering) have always a depth pass.

            // In case of forward only rendering we have a depth prepass. In case of deferred renderer, it is optional
            bool addFullDepthPrepass = m_FrameSettings.enableForwardRenderingOnly || m_FrameSettings.enableDepthPrepassWithDeferredRendering;
            bool addAlphaTestedOnly = !m_FrameSettings.enableForwardRenderingOnly && m_FrameSettings.enableDepthPrepassWithDeferredRendering && m_FrameSettings.enableAlphaTestOnlyInDeferredPrepass;

            var camera = hdCamera.camera;

            using (new ProfilingSample(cmd, addAlphaTestedOnly ? "Depth Prepass alpha test" : "Depth Prepass", CustomSamplerId.DepthPrepass.GetSampler()))
            {
                HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraDepthStencilBuffer);
                if (forcePrepass || (addFullDepthPrepass && !addAlphaTestedOnly)) // Always true in case of forward rendering, use in case of deferred rendering if requesting a full depth prepass
                {
                    // We render first the opaque object as opaque alpha tested are more costly to render and could be reject by early-z (but not Hi-z as it is disable with clip instruction)
                    // This is handled automatically with the RenderQueue value (OpaqueAlphaTested have a different value and thus are sorted after Opaque)
                    RenderOpaqueRenderList(cull, camera, renderContext, cmd, m_DepthOnlyAndDepthForwardOnlyPassNames, 0, RenderQueueRange.opaque, m_DepthStateOpaque);
                }
                else // Deferred rendering with partial depth prepass
                {
                    // We always do a DepthForwardOnly pass with all the opaque (including alpha test)
                    RenderOpaqueRenderList(cull, camera, renderContext, cmd, m_DepthForwardOnlyPassNames, 0, RenderQueueRange.opaque, m_DepthStateOpaque);

                    // Render Alpha test only if requested
                    if (addAlphaTestedOnly)
                    {
                        var renderQueueRange = new RenderQueueRange { min = (int)RenderQueue.AlphaTest, max = (int)RenderQueue.GeometryLast - 1 };
                        RenderOpaqueRenderList(cull, camera, renderContext, cmd, m_DepthOnlyPassNames, 0, renderQueueRange, m_DepthStateOpaque);
                    }
                }
            }

            if (m_FrameSettings.enableTransparentPrepass)
            {
                // Render transparent depth prepass after opaque one
                using (new ProfilingSample(cmd, "Transparent Depth Prepass", CustomSamplerId.TransparentDepthPrepass.GetSampler()))
                {
                    RenderTransparentRenderList(cull, camera, renderContext, cmd, m_TransparentDepthPrepassNames);
                }
            }
        }

        // RenderGBuffer do the gbuffer pass. This is solely call with deferred. If we use a depth prepass, then the depth prepass will perform the alpha testing for opaque apha tested and we don't need to do it anymore
        // during Gbuffer pass. This is handled in the shader and the depth test (equal and no depth write) is done here.
        void RenderGBuffer(CullResults cull, HDCamera hdCamera, ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (m_FrameSettings.enableForwardRenderingOnly)
                return;

            var camera = hdCamera.camera;

            using (new ProfilingSample(cmd, m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled() ? "GBufferDebugDisplay" : "GBuffer", CustomSamplerId.GBuffer.GetSampler()))
            {
                // setup GBuffer for rendering
                HDUtils.SetRenderTarget(cmd, hdCamera, m_GbufferManager.GetBuffersRTI(), m_CameraDepthStencilBuffer);

                // Render opaque objects into GBuffer
                if (m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled())
                {
                    // When doing debug display, the shader has the clip instruction regardless of the depth prepass so we can use regular depth test.
                    RenderOpaqueRenderList(cull, camera, renderContext, cmd, HDShaderPassNames.s_GBufferDebugDisplayName, m_currentRendererConfigurationBakedLighting, RenderQueueRange.opaque, m_DepthStateOpaque);
                }
                else
                {
                    if (m_FrameSettings.enableDepthPrepassWithDeferredRendering)
                    {
                        var rangeOpaqueNoAlphaTest = new RenderQueueRange { min = (int)RenderQueue.Geometry, max = (int)RenderQueue.AlphaTest - 1 };
                        var rangeOpaqueAlphaTest = new RenderQueueRange { min = (int)RenderQueue.AlphaTest, max = (int)RenderQueue.GeometryLast - 1 };

                        // When using depth prepass for opaque alpha test only we need to use regular depth test for normal opaque objects.
                        RenderOpaqueRenderList(cull, camera, renderContext, cmd, HDShaderPassNames.s_GBufferName, m_currentRendererConfigurationBakedLighting, rangeOpaqueNoAlphaTest, m_FrameSettings.enableAlphaTestOnlyInDeferredPrepass ? m_DepthStateOpaque : m_DepthStateOpaqueWithPrepass);
                        // but for opaque alpha tested object we use a depth equal and no depth write. And we rely on the shader pass GbufferWithDepthPrepass
                        RenderOpaqueRenderList(cull, camera, renderContext, cmd, HDShaderPassNames.s_GBufferWithPrepassName, m_currentRendererConfigurationBakedLighting, rangeOpaqueAlphaTest, m_DepthStateOpaqueWithPrepass);
                    }
                    else
                    {
                        // No depth prepass, use regular depth test - Note that we will render opaque then opaque alpha tested (based on the RenderQueue system)
                        RenderOpaqueRenderList(cull, camera, renderContext, cmd, HDShaderPassNames.s_GBufferName, m_currentRendererConfigurationBakedLighting, RenderQueueRange.opaque, m_DepthStateOpaque);
                    }
                }

                m_GbufferManager.BindBufferAsTextures(cmd);
            }
        }

        void RenderDBuffer(HDCamera camera, ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (!m_FrameSettings.enableDBuffer)
                return;

            using (new ProfilingSample(cmd, "DBuffer", CustomSamplerId.DBuffer.GetSampler()))
            {
                // We need to copy depth buffer texture if we want to bind it at this stage
                CopyDepthBufferIfNeeded(cmd);

                // Depth texture is now ready, bind it.
                cmd.SetGlobalTexture(HDShaderIDs._MainDepthTexture, GetDepthTexture());

                HDUtils.SetRenderTarget(cmd, camera, m_DbufferManager.GetBuffersRTI(), m_CameraDepthStencilBuffer, ClearFlag.Color, CoreUtils.clearColorAllBlack);
                DecalSystem.instance.Render(renderContext, camera, cmd);
            }
        }

        void RenderDebugViewMaterial(CullResults cull, HDCamera hdCamera, ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            using (new ProfilingSample(cmd, "DisplayDebug ViewMaterial", CustomSamplerId.DisplayDebugViewMaterial.GetSampler()))
            {
                if (m_CurrentDebugDisplaySettings.materialDebugSettings.IsDebugGBufferEnabled() && !m_FrameSettings.enableForwardRenderingOnly)
                {
                    using (new ProfilingSample(cmd, "DebugViewMaterialGBuffer", CustomSamplerId.DebugViewMaterialGBuffer.GetSampler()))
                    {
                        CoreUtils.DrawFullScreen(cmd, m_currentDebugViewMaterialGBuffer, m_CameraColorBuffer);
                    }
                }
                else
                {
                    HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraColorBuffer, m_CameraDepthStencilBuffer, ClearFlag.All, CoreUtils.clearColorAllBlack);
                    // Render Opaque forward
                    RenderOpaqueRenderList(cull, hdCamera.camera, renderContext, cmd, m_AllForwardDebugDisplayPassNames, m_currentRendererConfigurationBakedLighting);

                    // Render forward transparent
                    RenderTransparentRenderList(cull, hdCamera.camera, renderContext, cmd, m_AllForwardDebugDisplayPassNames, m_currentRendererConfigurationBakedLighting);
                }
            }

            // Last blit
            {
                using (new ProfilingSample(cmd, "Blit DebugView Material Debug", CustomSamplerId.BlitDebugViewMaterialDebug.GetSampler()))
                {
                    HDUtils.BlitCameraTexture(cmd, hdCamera, m_CameraColorBuffer, BuiltinRenderTextureType.CameraTarget);
                }
            }
        }

        void RenderSSAO(CommandBuffer cmd, HDCamera hdCamera, ScriptableRenderContext renderContext, PostProcessLayer postProcessLayer)
        {
            var camera = hdCamera.camera;

            // Apply SSAO from PostProcessLayer
            if (m_FrameSettings.enableSSAO && postProcessLayer != null && postProcessLayer.enabled)
            {
                var settings = postProcessLayer.GetSettings<AmbientOcclusion>();

                if (settings.IsEnabledAndSupported(null))
                {
                    postProcessLayer.BakeMSVOMap(cmd, camera, m_AmbientOcclusionBuffer, GetDepthTexture(), true);

                    cmd.SetGlobalTexture(HDShaderIDs._AmbientOcclusionTexture, m_AmbientOcclusionBuffer);
                    cmd.SetGlobalVector(HDShaderIDs._AmbientOcclusionParam, new Vector4(settings.color.value.r, settings.color.value.g, settings.color.value.b, settings.directLightingStrength.value));
                    PushFullScreenDebugTexture(cmd, m_AmbientOcclusionBuffer, hdCamera, FullScreenDebugMode.SSAO);
                    return;
                }
            }

            // No AO applied - neutral is black, see the comment in the shaders
            cmd.SetGlobalTexture(HDShaderIDs._AmbientOcclusionTexture, RuntimeUtilities.blackTexture);
            cmd.SetGlobalVector(HDShaderIDs._AmbientOcclusionParam, Vector4.zero);
        }

        void RenderDeferredLighting(HDCamera hdCamera, CommandBuffer cmd)
        {
            if (m_FrameSettings.enableForwardRenderingOnly)
                return;

            m_MRTCache2[0] = m_CameraColorBuffer;
            m_MRTCache2[1] = m_CameraSssDiffuseLightingBuffer;
            var depthTexture = GetDepthTexture();

            var options = new LightLoop.LightingPassOptions();

            if (m_FrameSettings.enableSubsurfaceScattering)
            {
                // Output split lighting for materials asking for it (masked in the stencil buffer)
                options.outputSplitLighting = true;

                m_LightLoop.RenderDeferredLighting(hdCamera, cmd, m_CurrentDebugDisplaySettings, m_MRTCache2, m_CameraDepthStencilBuffer, depthTexture, options);
            }

            // Output combined lighting for all the other materials.
            options.outputSplitLighting = false;

            m_LightLoop.RenderDeferredLighting(hdCamera, cmd, m_CurrentDebugDisplaySettings, m_MRTCache2, m_CameraDepthStencilBuffer, depthTexture, options);
        }

        void UpdateSkyEnvironment(HDCamera hdCamera, CommandBuffer cmd)
        {
            m_SkyManager.UpdateEnvironment(hdCamera, m_LightLoop.GetCurrentSunLight(), cmd);
        }

        void RenderSky(HDCamera hdCamera, CommandBuffer cmd)
        {
            // Rendering the sky is the first time in the frame where we need fog parameters so we push them here for the whole frame.
            var visualEnv = VolumeManager.instance.stack.GetComponent<VisualEnvironment>();
            visualEnv.PushFogShaderParameters(cmd, m_FrameSettings);

            m_SkyManager.RenderSky(hdCamera, m_LightLoop.GetCurrentSunLight(), m_CameraColorBuffer, m_CameraDepthStencilBuffer, cmd);

            if (visualEnv.fogType != FogType.None || m_VolumetricLightingPreset != VolumetricLightingPreset.Off)
                m_SkyManager.RenderOpaqueAtmosphericScattering(cmd);
        }

        public Texture2D ExportSkyToTexture()
        {
            return m_SkyManager.ExportSkyToTexture();
        }

        // Render forward is use for both transparent and opaque objects. In case of deferred we can still render opaque object in forward.
        void RenderForward(CullResults cullResults, HDCamera hdCamera, ScriptableRenderContext renderContext, CommandBuffer cmd, ForwardPass pass)
        {
            // Guidelines: In deferred by default there is no opaque in forward. However it is possible to force an opaque material to render in forward
            // by using the pass "ForwardOnly". In this case the .shader should not have "Forward" but only a "ForwardOnly" pass.
            // It must also have a "DepthForwardOnly" and no "DepthOnly" pass as forward material (either deferred or forward only rendering) have always a depth pass.
            // The RenderForward pass will render the appropriate pass depends on the engine settings. In case of forward only rendering, both "Forward" pass and "ForwardOnly" pass
            // material will be render for both transparent and opaque. In case of deferred, both path are used for transparent but only "ForwardOnly" is use for opaque.
            // (Thus why "Forward" and "ForwardOnly" are exclusive, else they will render two times"

            string profileName;
            if (m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled())
            {
                profileName = k_ForwardPassDebugName[(int)pass];
            }
            else
            {
                profileName = k_ForwardPassName[(int)pass];
            }

            using (new ProfilingSample(cmd, profileName, CustomSamplerId.ForwardPassName.GetSampler()))
            {
                var camera = hdCamera.camera;

                m_LightLoop.RenderForward(camera, cmd, pass == ForwardPass.Opaque);

                if (pass == ForwardPass.Opaque)
                {
                    // In case of forward SSS we will bind all the required target. It is up to the shader to write into it or not.
                    if (m_FrameSettings.enableSubsurfaceScattering)
                    {
                        RenderTargetIdentifier[] m_MRTWithSSS = new RenderTargetIdentifier[2 + m_SSSBufferManager.sssBufferCount];
                        m_MRTWithSSS[0] = m_CameraColorBuffer; // Store the specular color
                        m_MRTWithSSS[1] = m_CameraSssDiffuseLightingBuffer;
                        for (int i = 0; i < m_SSSBufferManager.sssBufferCount; ++i)
                        {
                            m_MRTWithSSS[i + 2] = m_SSSBufferManager.GetSSSBuffer(i);
                        }

                        HDUtils.SetRenderTarget(cmd, hdCamera, m_MRTWithSSS, m_CameraDepthStencilBuffer);
                    }
                    else
                    {
                        HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraColorBuffer, m_CameraDepthStencilBuffer);
                    }

                    if (m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled())
                    {
                        m_ForwardAndForwardOnlyPassNames[0] = m_ForwardOnlyPassNames[0] = HDShaderPassNames.s_ForwardOnlyDebugDisplayName;
                        m_ForwardAndForwardOnlyPassNames[1] = HDShaderPassNames.s_ForwardDebugDisplayName;
                    }
                    else
                    {
                        m_ForwardAndForwardOnlyPassNames[0] = m_ForwardOnlyPassNames[0] = HDShaderPassNames.s_ForwardOnlyName;
                        m_ForwardAndForwardOnlyPassNames[1] = HDShaderPassNames.s_ForwardName;
                    }

                    var passNames = m_FrameSettings.enableForwardRenderingOnly ? m_ForwardAndForwardOnlyPassNames : m_ForwardOnlyPassNames;
                    // Forward opaque material always have a prepass (whether or not we use deferred, whether or not there is option like alpha test only) so we pass the right depth state here.
                    RenderOpaqueRenderList(cullResults, camera, renderContext, cmd, passNames, m_currentRendererConfigurationBakedLighting, null, m_DepthStateOpaqueWithPrepass);
                }
                else
                {
                    HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraColorBuffer, m_CameraDepthStencilBuffer);

                    var passNames = m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled() ? m_AllTransparentDebugDisplayPassNames : m_AllTransparentPassNames;
                    RenderTransparentRenderList(cullResults, camera, renderContext, cmd, passNames, m_currentRendererConfigurationBakedLighting, pass == ForwardPass.PreRefraction ? k_RenderQueue_PreRefraction : k_RenderQueue_Transparent);
                }
            }
        }

        // This is use to Display legacy shader with an error shader
        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        void RenderForwardError(CullResults cullResults, HDCamera hdCamera, ScriptableRenderContext renderContext, CommandBuffer cmd, ForwardPass pass)
        {
            using (new ProfilingSample(cmd, "Render Forward Error", CustomSamplerId.RenderForwardError.GetSampler()))
            {
                HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraColorBuffer, m_CameraDepthStencilBuffer);

                if (pass == ForwardPass.Opaque)
                {
                    RenderOpaqueRenderList(cullResults, hdCamera.camera, renderContext, cmd, m_ForwardErrorPassNames, 0, null, null, m_ErrorMaterial);
                }
                else
                {
                    RenderTransparentRenderList(cullResults, hdCamera.camera, renderContext, cmd, m_ForwardErrorPassNames, 0, pass == ForwardPass.PreRefraction ? k_RenderQueue_PreRefraction : k_RenderQueue_Transparent, null, m_ErrorMaterial);
                }
            }
        }

        void RenderTransparentDepthPostpass(CullResults cullResults, HDCamera hdCamera, ScriptableRenderContext renderContext, CommandBuffer cmd, ForwardPass pass)
        {
            if (!m_FrameSettings.enableTransparentPostpass)
                return;

            using (new ProfilingSample(cmd, "Render Transparent Depth Post ", CustomSamplerId.TransparentDepthPostpass.GetSampler()))
            {
                HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraDepthStencilBuffer);
                RenderTransparentRenderList(cullResults, hdCamera.camera, renderContext, cmd, m_TransparentDepthPostpassNames);
            }
        }

        void RenderObjectsVelocity(CullResults cullResults, HDCamera hdcamera, ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (!m_FrameSettings.enableMotionVectors || !m_FrameSettings.enableObjectMotionVectors)
                return;

            using (new ProfilingSample(cmd, "Objects Velocity", CustomSamplerId.ObjectsVelocity.GetSampler()))
            {
                // These flags are still required in SRP or the engine won't compute previous model matrices...
                // If the flag hasn't been set yet on this camera, motion vectors will skip a frame.
                hdcamera.camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;

                cmd.SetRenderTarget(m_VelocityBuffer, m_CameraDepthStencilBuffer);

                RenderOpaqueRenderList(cullResults, hdcamera.camera, renderContext, cmd, HDShaderPassNames.s_MotionVectorsName, RendererConfiguration.PerObjectMotionVectors);
            }
        }

        void RenderCameraVelocity(CullResults cullResults, HDCamera hdcamera, ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (!m_FrameSettings.enableMotionVectors)
                return;

            using (new ProfilingSample(cmd, "Camera Velocity", CustomSamplerId.CameraVelocity.GetSampler()))
            {
                // These flags are still required in SRP or the engine won't compute previous model matrices...
                // If the flag hasn't been set yet on this camera, motion vectors will skip a frame.
                hdcamera.camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;


                CoreUtils.DrawFullScreen(cmd, m_CameraMotionVectorsMaterial, m_VelocityBuffer, m_CameraDepthStencilBuffer, null, 0);

                PushFullScreenDebugTexture(cmd, m_VelocityBuffer, hdcamera, FullScreenDebugMode.MotionVectors);
            }
        }

        void RenderGaussianPyramidColor(Camera camera, CommandBuffer cmd, ScriptableRenderContext renderContext, bool isPreRefraction)
        {
            if (isPreRefraction)
            {
                if (!m_FrameSettings.enableRoughRefraction)
                    return;
            }
            else
            {
                // TODO: This final Gaussian pyramid can be reuse by Bloom and SSR in the future, so disable it only if there is no postprocess AND no distortion
                if (!m_FrameSettings.enableDistortion && !m_FrameSettings.enablePostprocess && !m_FrameSettings.enableSSR)
                    return;
            }

            using (new ProfilingSample(cmd, "Gaussian Pyramid Color", CustomSamplerId.GaussianPyramidColor.GetSampler()))
            {
                UpdatePyramidMips(m_GaussianPyramidColorBuffer.rt.format, m_GaussianPyramidColorMips);

                int pyramidSideSize = GetPyramidSize();
                int lodCount = GetPyramidLodCount();
                cmd.SetGlobalVector(HDShaderIDs._GaussianPyramidColorMipSize, new Vector4(pyramidSideSize, pyramidSideSize, lodCount, 0));

                cmd.SetGlobalTexture(HDShaderIDs._BlitTexture, m_CameraColorBuffer);
                CoreUtils.DrawFullScreen(cmd, m_Blit, m_GaussianPyramidColorBuffer, null, 1); // Bilinear filtering

                var last = m_GaussianPyramidColorBuffer;

                for (int i = 0; i < lodCount; i++)
                {
                    // TODO: Add proper stereo support to the compute job
                    RTHandle dest = m_GaussianPyramidColorMips[i];
                    cmd.SetComputeTextureParam(m_GaussianPyramidCS, m_GaussianPyramidKernel, "_Source", last);
                    cmd.SetComputeTextureParam(m_GaussianPyramidCS, m_GaussianPyramidKernel, "_Result", dest);
                    cmd.SetComputeVectorParam(m_GaussianPyramidCS, "_Size", new Vector4(dest.rt.width, dest.rt.height, 1f / dest.rt.width, 1f / dest.rt.height));
                    cmd.DispatchCompute(m_GaussianPyramidCS, m_GaussianPyramidKernel, dest.rt.width / 8, dest.rt.height / 8, 1);
                    cmd.CopyTexture(dest, 0, 0, m_GaussianPyramidColorBuffer, 0, i + 1);

                    last = dest;
                }

                PushFullScreenDebugTextureMip(cmd, m_GaussianPyramidColorBuffer, lodCount, isPreRefraction ? FullScreenDebugMode.PreRefractionColorPyramid : FullScreenDebugMode.FinalColorPyramid);

                cmd.SetGlobalTexture(HDShaderIDs._GaussianPyramidColorTexture, m_GaussianPyramidColorBuffer);
            }
        }

        void RenderPyramidDepth(Camera camera, CommandBuffer cmd, ScriptableRenderContext renderContext, FullScreenDebugMode debugMode)
        {
            using (new ProfilingSample(cmd, "Pyramid Depth", CustomSamplerId.PyramidDepth.GetSampler()))
            {
                UpdatePyramidMips(m_DepthPyramidBuffer.rt.format, m_DepthPyramidMips);

                int pyramidSideSize = GetPyramidSize();
                int lodCount = GetPyramidLodCount();
                cmd.SetGlobalVector(HDShaderIDs._DepthPyramidMipSize, new Vector4(pyramidSideSize, pyramidSideSize, lodCount, 0));

                m_GPUCopy.SampleCopyChannel_xyzw2x(cmd, GetDepthTexture(), m_DepthPyramidBuffer, new Vector2(m_DepthPyramidBuffer.rt.width, m_DepthPyramidBuffer.rt.height));

                RTHandle last = m_DepthPyramidBuffer;
                for (int i = 0; i < lodCount; i++)
                {
                    RTHandle dest = m_DepthPyramidMips[i];
                    cmd.SetComputeTextureParam(m_DepthPyramidCS, m_DepthPyramidKernel, "_Source", last);
                    cmd.SetComputeTextureParam(m_DepthPyramidCS, m_DepthPyramidKernel, "_Result", dest);
                    cmd.SetComputeVectorParam(m_DepthPyramidCS, "_SrcSize", new Vector4(last.rt.width, last.rt.height, 1f / last.rt.width, 1f / last.rt.height));

                    cmd.DispatchCompute(m_DepthPyramidCS, m_DepthPyramidKernel, dest.rt.width / 8, dest.rt.height / 8, 1);

                    cmd.CopyTexture(dest, 0, 0, m_DepthPyramidBuffer, 0, i + 1);
                }

                PushFullScreenDebugDepthMip(cmd, m_DepthPyramidBuffer, lodCount, debugMode);

                cmd.SetGlobalTexture(HDShaderIDs._DepthPyramidTexture, m_DepthPyramidBuffer);
            }
        }

        void RenderPostProcess(HDCamera hdcamera, CommandBuffer cmd, PostProcessLayer layer)
        {
            using (new ProfilingSample(cmd, "Post-processing", CustomSamplerId.PostProcessing.GetSampler()))
            {
                // Note: Here we don't use GetDepthTexture() to get the depth texture but m_CameraDepthStencilBuffer as the Forward transparent pass can
                // write extra data to deal with DOF/MB
                cmd.SetGlobalTexture(HDShaderIDs._CameraDepthTexture, m_CameraDepthStencilBuffer);
                cmd.SetGlobalTexture(HDShaderIDs._CameraMotionVectorsTexture, m_VelocityBuffer);

                var context = hdcamera.postprocessRenderContext;
                context.Reset();
                context.source = m_CameraColorBuffer;
                context.destination = BuiltinRenderTextureType.CameraTarget;
                context.command = cmd;
                context.camera = hdcamera.camera;
                context.sourceFormat = RenderTextureFormat.ARGBHalf;
                context.flip = true;

                layer.Render(context);
            }
        }

        public void ApplyDebugDisplaySettings(HDCamera hdCamera, CommandBuffer cmd)
        {
            if (m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled() ||
                m_CurrentDebugDisplaySettings.fullScreenDebugMode != FullScreenDebugMode.None ||
                m_CurrentDebugDisplaySettings.colorPickerDebugSettings.colorPickerMode != ColorPickerDebugMode.None)
            {
                m_ShadowSettings.enabled = m_FrameSettings.enableShadow;

                var lightingDebugSettings = m_CurrentDebugDisplaySettings.lightingDebugSettings;
                var debugAlbedo = new Vector4(lightingDebugSettings.debugLightingAlbedo.r, lightingDebugSettings.debugLightingAlbedo.g, lightingDebugSettings.debugLightingAlbedo.b, 0.0f);
                var debugSmoothness = new Vector4(lightingDebugSettings.overrideSmoothness ? 1.0f : 0.0f, lightingDebugSettings.overrideSmoothnessValue, 0.0f, 0.0f);

                cmd.SetGlobalInt(HDShaderIDs._DebugViewMaterial, (int)m_CurrentDebugDisplaySettings.GetDebugMaterialIndex());
                cmd.SetGlobalInt(HDShaderIDs._DebugLightingMode, (int)m_CurrentDebugDisplaySettings.GetDebugLightingMode());
                cmd.SetGlobalInt(HDShaderIDs._DebugMipMapMode, (int)m_CurrentDebugDisplaySettings.GetDebugMipMapMode());
                cmd.SetGlobalVector(HDShaderIDs._DebugLightingAlbedo, debugAlbedo);

                cmd.SetGlobalVector(HDShaderIDs._DebugLightingSmoothness, debugSmoothness);

                Vector2 mousePixelCoord = MousePositionDebug.instance.GetMousePosition(hdCamera.screenSize.y);
                var mouseParam = new Vector4(mousePixelCoord.x, mousePixelCoord.y, mousePixelCoord.x / hdCamera.screenSize.x, mousePixelCoord.y / hdCamera.screenSize.y);
                cmd.SetGlobalVector(HDShaderIDs._MousePixelCoord, mouseParam);

                cmd.SetGlobalTexture(HDShaderIDs._DebugFont, m_Asset.renderPipelineResources.debugFontTexture);
            }
        }

        public void PushColorPickerDebugTexture(CommandBuffer cmd, RTHandle textureID, HDCamera hdCamera)
        {
            if (m_CurrentDebugDisplaySettings.colorPickerDebugSettings.colorPickerMode != ColorPickerDebugMode.None)
            {
                HDUtils.BlitCameraTexture(cmd, hdCamera, textureID, m_DebugColorPickerBuffer);
            }
        }

        public void PushFullScreenDebugTexture(CommandBuffer cmd, RTHandle textureID, HDCamera hdCamera, FullScreenDebugMode debugMode)
        {
            if (debugMode == m_CurrentDebugDisplaySettings.fullScreenDebugMode)
            {
                m_FullScreenDebugPushed = true; // We need this flag because otherwise if no full screen debug is pushed (like for example if the corresponding pass is disabled), when we render the result in RenderDebug m_DebugFullScreenTempBuffer will contain potential garbage
                HDUtils.BlitCameraTexture(cmd, hdCamera, textureID, m_DebugFullScreenTempBuffer);
            }
        }

        void PushFullScreenDebugTextureMip(CommandBuffer cmd, RTHandle texture, int lodCount, FullScreenDebugMode debugMode)
        {
            if (debugMode == m_CurrentDebugDisplaySettings.fullScreenDebugMode)
            {
                var mipIndex = Mathf.FloorToInt(m_CurrentDebugDisplaySettings.fullscreenDebugMip * (lodCount));

                m_FullScreenDebugPushed = true; // We need this flag because otherwise if no full screen debug is pushed (like for example if the corresponding pass is disabled), when we render the result in RenderDebug m_DebugFullScreenTempBuffer will contain potential garbage
                cmd.CopyTexture(texture, 0, mipIndex, m_DebugFullScreenTempBuffer, 0, 0); // TODO: Support tex arrays
            }
        }

        void PushFullScreenDebugDepthMip(CommandBuffer cmd, RTHandle texture, int lodCount, FullScreenDebugMode debugMode)
        {
            if (debugMode == m_CurrentDebugDisplaySettings.fullScreenDebugMode)
            {
                var mipIndex = Mathf.FloorToInt(m_CurrentDebugDisplaySettings.fullscreenDebugMip * (lodCount));

                m_FullScreenDebugPushed = true; // We need this flag because otherwise if no full screen debug is pushed (like for example if the corresponding pass is disabled), when we render the result in RenderDebug m_DebugFullScreenTempBuffer will contain potential garbage
                cmd.CopyTexture(texture, 0, mipIndex, m_DebugFullScreenTempBuffer, 0, 0); // TODO: Support tex arrays
            }
        }

        void RenderDebug(HDCamera hdCamera, CommandBuffer cmd)
        {
            // We don't want any overlay for these kind of rendering
            if (hdCamera.camera.cameraType == CameraType.Reflection || hdCamera.camera.cameraType == CameraType.Preview)
                return;

            using (new ProfilingSample(cmd, "Render Debug", CustomSamplerId.RenderDebug.GetSampler()))
            {
                // First render full screen debug texture
                if (m_CurrentDebugDisplaySettings.fullScreenDebugMode != FullScreenDebugMode.None && m_FullScreenDebugPushed)
                {
                    m_FullScreenDebugPushed = false;
                    cmd.SetGlobalTexture(HDShaderIDs._DebugFullScreenTexture, m_DebugFullScreenTempBuffer);
                    // TODO: Replace with command buffer call when available
                    m_DebugFullScreen.SetFloat(HDShaderIDs._FullScreenDebugMode, (float)m_CurrentDebugDisplaySettings.fullScreenDebugMode);
                    CoreUtils.DrawFullScreen(cmd, m_DebugFullScreen, (RenderTargetIdentifier)BuiltinRenderTextureType.CameraTarget);

                    PushColorPickerDebugTexture(cmd, m_DebugFullScreenTempBuffer, hdCamera);
                }

                // Then overlays
                float x = 0;
                float overlayRatio = m_CurrentDebugDisplaySettings.debugOverlayRatio;
                float overlaySize = Math.Min(hdCamera.actualHeight, hdCamera.actualWidth) * overlayRatio;
                float y = hdCamera.actualHeight - overlaySize;

                var lightingDebug = m_CurrentDebugDisplaySettings.lightingDebugSettings;

                if (lightingDebug.displaySkyReflection)
                {
                    var skyReflection = m_SkyManager.skyReflection;
                    m_SharedPropertyBlock.SetTexture(HDShaderIDs._InputCubemap, skyReflection);
                    m_SharedPropertyBlock.SetFloat(HDShaderIDs._Mipmap, lightingDebug.skyReflectionMipmap);
                    cmd.SetViewport(new Rect(x, y, overlaySize, overlaySize));
                    cmd.DrawProcedural(Matrix4x4.identity, m_DebugDisplayLatlong, 0, MeshTopology.Triangles, 3, 1, m_SharedPropertyBlock);
                    HDUtils.NextOverlayCoord(ref x, ref y, overlaySize, overlaySize, hdCamera.actualWidth);
                }

                m_LightLoop.RenderDebugOverlay(hdCamera, cmd, m_CurrentDebugDisplaySettings, ref x, ref y, overlaySize, hdCamera.actualWidth);

                if (m_CurrentDebugDisplaySettings.colorPickerDebugSettings.colorPickerMode != ColorPickerDebugMode.None)
                {
                    ColorPickerDebugSettings colorPickerDebugSettings = m_CurrentDebugDisplaySettings.colorPickerDebugSettings;

                    // Here we have three cases:
                    // - Material debug is enabled, this is the buffer we display
                    // - Otherwise we display the HDR buffer before postprocess and distortion
                    // - If fullscreen debug is enabled we always use it

                    cmd.SetGlobalTexture(HDShaderIDs._DebugColorPickerTexture, m_DebugColorPickerBuffer); // No SetTexture with RenderTarget identifier... so use SetGlobalTexture
                    // TODO: Replace with command buffer call when available
                    m_DebugColorPicker.SetColor(HDShaderIDs._ColorPickerFontColor, colorPickerDebugSettings.fontColor);
                    var colorPickerParam = new Vector4(colorPickerDebugSettings.colorThreshold0, colorPickerDebugSettings.colorThreshold1, colorPickerDebugSettings.colorThreshold2, colorPickerDebugSettings.colorThreshold3);
                    m_DebugColorPicker.SetVector(HDShaderIDs._ColorPickerParam, colorPickerParam);
                    m_DebugColorPicker.SetInt(HDShaderIDs._ColorPickerMode, (int)colorPickerDebugSettings.colorPickerMode);

                    CoreUtils.DrawFullScreen(cmd, m_DebugColorPicker, (RenderTargetIdentifier)BuiltinRenderTextureType.CameraTarget);
                }
            }
        }

        void ClearBuffers(HDCamera hdCamera, CommandBuffer cmd)
        {
            using (new ProfilingSample(cmd, "ClearBuffers", CustomSamplerId.ClearBuffers.GetSampler()))
            {
                // We clear only the depth buffer, no need to clear the various color buffer as we overwrite them.
                // Clear depth/stencil and init buffers
                using (new ProfilingSample(cmd, "Clear Depth/Stencil", CustomSamplerId.ClearDepthStencil.GetSampler()))
                {
                    HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraColorBuffer, m_CameraDepthStencilBuffer, ClearFlag.Depth);
                }

                // Clear the diffuse SSS lighting target
                using (new ProfilingSample(cmd, "Clear SSS diffuse target", CustomSamplerId.ClearSSSDiffuseTarget.GetSampler()))
                {
                    HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraSssDiffuseLightingBuffer, ClearFlag.Color, CoreUtils.clearColorAllBlack);
                }

                // TODO: As we are in development and have not all the setup pass we still clear the color in emissive buffer and gbuffer, but this will be removed later.

                // Clear the HDR target
                using (new ProfilingSample(cmd, "Clear HDR target", CustomSamplerId.ClearHDRTarget.GetSampler()))
                {
                    Color clearColor = hdCamera.camera.backgroundColor.linear; // Need it in linear because we clear a linear fp16 texture.
                    HDUtils.SetRenderTarget(cmd, hdCamera, m_CameraColorBuffer, m_CameraDepthStencilBuffer, ClearFlag.Color, clearColor);
                }

                // Clear GBuffers
                if (!m_FrameSettings.enableForwardRenderingOnly)
                {
                    using (new ProfilingSample(cmd, "Clear GBuffer", CustomSamplerId.ClearGBuffer.GetSampler()))
                    {
                        HDUtils.SetRenderTarget(cmd, hdCamera, m_GbufferManager.GetBuffersRTI(), m_CameraDepthStencilBuffer, ClearFlag.Color, CoreUtils.clearColorAllBlack);
                    }
                }
                // END TEMP
            }
        }
    }
}
