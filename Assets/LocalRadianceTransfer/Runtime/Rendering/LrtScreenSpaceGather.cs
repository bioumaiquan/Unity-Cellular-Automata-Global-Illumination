using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class LrtScreenSpaceGather : ScriptableRendererFeature
{
    private const GraphicsFormat GatherFormat = GraphicsFormat.R16G16B16A16_SFloat;
    private const GraphicsFormat DepthNormalFormat = GraphicsFormat.B10G11R11_UFloatPack32;
    private static readonly int CellSpacingId = Shader.PropertyToID("_LrtCellSpacing");

    [SerializeField] private Shader gatherShader;
    [SerializeField, Range(0.25f, 1f)] private float resolutionScale = 0.5f;

    private Material material;
    private GatherPass pass;
    private DepthNormalPass depthNormalPass;

    public override void Create()
    {
        Dispose(true);
        var depthFormat = SystemInfo.GetCompatibleFormat(
            SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil), FormatUsage.Render);
        if (gatherShader == null || !gatherShader.isSupported ||
            !SystemInfo.IsFormatSupported(GatherFormat, FormatUsage.Render) ||
            !SystemInfo.IsFormatSupported(GatherFormat, FormatUsage.Linear) ||
            !SystemInfo.IsFormatSupported(DepthNormalFormat, FormatUsage.Render) ||
            !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth) ||
            depthFormat == GraphicsFormat.None)
        {
            Debug.LogWarning($"LRT Screen Space Gather disabled. " +
                $"Device={SystemInfo.graphicsDeviceName}, API={SystemInfo.graphicsDeviceType}, " +
                $"shaderSupported={gatherShader != null && gatherShader.isSupported}, " +
                $"normalFormat={DepthNormalFormat}, depthFormat={depthFormat}.");
            return;
        }

        material = CoreUtils.CreateEngineMaterial(gatherShader);
        depthNormalPass = new DepthNormalPass(depthFormat);
        pass = new GatherPass(material, depthNormalPass);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null || renderingData.cameraData.isPreviewCamera ||
            Shader.GetGlobalFloat(LocalRadianceTransferDemo.DebugViewId) == LocalRadianceTransferDemo.NativeUrpView ||
            Shader.GetGlobalVector(CellSpacingId).x <= 0f)
            return;

        pass.resolutionScale = resolutionScale;
        depthNormalPass.resolutionScale = resolutionScale;
        renderer.EnqueuePass(depthNormalPass);
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        pass = null;
        depthNormalPass?.Dispose();
        depthNormalPass = null;
        CoreUtils.Destroy(material);
        material = null;
    }

    private sealed class DepthNormalPass : ScriptableRenderPass
    {
        private static readonly int TextureId = Shader.PropertyToID("_LrtDepthNormalTexture");
        private static readonly int DepthTextureId = Shader.PropertyToID("_LrtDepthNormalDepth");
        private static readonly ShaderTagId PassTag = new ShaderTagId("LrtDepthNormals");
        private readonly ProfilingSampler sampler = new ProfilingSampler("LRT Depth Normal");
        private readonly GraphicsFormat depthFormat;
        private RTHandle target;
        private RTHandle depth;
        private bool allocationWarningLogged;
        public float resolutionScale;
        public bool IsReady { get; private set; }

        public DepthNormalPass(GraphicsFormat depthFormat)
        {
            this.depthFormat = depthFormat;
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            IsReady = false;
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.width = Mathf.Max(1, Mathf.CeilToInt(descriptor.width * resolutionScale));
            descriptor.height = Mathf.Max(1, Mathf.CeilToInt(descriptor.height * resolutionScale));
            descriptor.graphicsFormat = DepthNormalFormat;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.msaaSamples = 1;
            descriptor.bindMS = false;
            descriptor.useDynamicScale = false;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.enableRandomWrite = false;
            descriptor.memoryless = RenderTextureMemoryless.None;
            RenderingUtils.ReAllocateIfNeeded(ref target, descriptor, FilterMode.Point,
                TextureWrapMode.Clamp, name: "_LrtDepthNormalTexture");
            descriptor.graphicsFormat = GraphicsFormat.None;
            descriptor.depthStencilFormat = depthFormat;
            RenderingUtils.ReAllocateIfNeeded(ref depth, descriptor, FilterMode.Point,
                TextureWrapMode.Clamp, name: "_LrtDepthNormalDepth");
            ConfigureTarget(target, depth);
            ConfigureClear(ClearFlag.All, Color.clear);
            ConfigureColorStoreAction(RenderBufferStoreAction.Store);
            ConfigureDepthStoreAction(RenderBufferStoreAction.Store);
            IsReady = target.rt.IsCreated() && depth.rt.IsCreated();
            if (!IsReady && !allocationWarningLogged)
            {
                Debug.LogWarning($"LRT depth/normal allocation failed. " +
                    $"Size={descriptor.width}x{descriptor.height}, depthFormat={depthFormat}, " +
                    $"API={SystemInfo.graphicsDeviceType}. Gather is skipped.");
                allocationWarningLogged = true;
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!IsReady)
                return;
            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, sampler))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                var drawing = CreateDrawingSettings(PassTag, ref renderingData,
                    renderingData.cameraData.defaultOpaqueSortFlags);
                var filtering = new FilteringSettings(RenderQueueRange.opaque,
                    renderingData.cameraData.camera.cullingMask);
                context.DrawRenderers(renderingData.cullResults, ref drawing, ref filtering);
                cmd.SetGlobalTexture(TextureId, target.nameID);
                cmd.SetGlobalTexture(DepthTextureId, depth.nameID, RenderTextureSubElement.Depth);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void Dispose()
        {
            target?.Release();
            depth?.Release();
            target = null;
            depth = null;
            IsReady = false;
        }
    }

    private sealed class GatherPass : ScriptableRenderPass
    {
        private static readonly int TextureId = Shader.PropertyToID("_LrtScreenSpaceGI");
        private static readonly int ReadyId = Shader.PropertyToID("_LrtScreenSpaceGIReady");
        private static readonly Vector4 FullscreenScaleBias = new Vector4(1f, 1f, 0f, 0f);
        private readonly ProfilingSampler sampler = new ProfilingSampler("LRT Screen Space Gather");
        private readonly Material material;
        private readonly DepthNormalPass depthNormalPass;
        private RTHandle target;

        public float resolutionScale;

        public GatherPass(Material material, DepthNormalPass depthNormalPass)
        {
            this.material = material;
            this.depthNormalPass = depthNormalPass;
            // Read the normal target and its stored depth attachment before opaque shading.
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            cmd.SetGlobalFloat(ReadyId, 0f);
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.width = Mathf.Max(1, Mathf.CeilToInt(descriptor.width * resolutionScale));
            descriptor.height = Mathf.Max(1, Mathf.CeilToInt(descriptor.height * resolutionScale));
            descriptor.graphicsFormat = GatherFormat;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.msaaSamples = 1;
            descriptor.bindMS = false;
            descriptor.useDynamicScale = false;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.enableRandomWrite = false;
            descriptor.memoryless = RenderTextureMemoryless.None;

            RenderingUtils.ReAllocateIfNeeded(ref target, descriptor, FilterMode.Bilinear,
                TextureWrapMode.Clamp, name: "_LrtScreenSpaceGI");
            ConfigureTarget(target);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!depthNormalPass.IsReady)
                return;
            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, sampler))
            {
                Blitter.BlitTexture(cmd, FullscreenScaleBias, material, 0);
                cmd.SetGlobalTexture(TextureId, target.nameID);
                cmd.SetGlobalFloat(ReadyId, 1f);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.SetGlobalFloat(ReadyId, 0f);
        }

        public void Dispose()
        {
            target?.Release();
            target = null;
        }
    }
}
