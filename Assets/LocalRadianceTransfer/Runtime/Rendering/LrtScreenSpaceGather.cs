using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class LrtScreenSpaceGather : ScriptableRendererFeature
{
    private const GraphicsFormat GatherFormat = GraphicsFormat.R16G16B16A16_SFloat;
    private static readonly int CellSpacingId = Shader.PropertyToID("_LrtCellSpacing");

    [SerializeField] private Shader gatherShader;
    [SerializeField, Range(0.25f, 1f)] private float resolutionScale = 0.5f;

    private Material material;
    private GatherPass pass;

    public override void Create()
    {
        Dispose(true);
        if (gatherShader == null || !gatherShader.isSupported ||
            !SystemInfo.IsFormatSupported(GatherFormat, FormatUsage.Render) ||
            !SystemInfo.IsFormatSupported(GatherFormat, FormatUsage.Linear))
            return;

        material = CoreUtils.CreateEngineMaterial(gatherShader);
        pass = new GatherPass(material);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null || renderingData.cameraData.isPreviewCamera ||
            Shader.GetGlobalVector(CellSpacingId).x <= 0f)
            return;

        pass.resolutionScale = resolutionScale;
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        pass = null;
        CoreUtils.Destroy(material);
        material = null;
    }

    private sealed class GatherPass : ScriptableRenderPass
    {
        private static readonly int TextureId = Shader.PropertyToID("_LrtScreenSpaceGI");
        private static readonly int ReadyId = Shader.PropertyToID("_LrtScreenSpaceGIReady");
        private static readonly Vector4 FullscreenScaleBias = new Vector4(1f, 1f, 0f, 0f);
        private readonly ProfilingSampler sampler = new ProfilingSampler("LRT Screen Space Gather");
        private readonly Material material;
        private RTHandle target;

        public float resolutionScale;

        public GatherPass(Material material)
        {
            this.material = material;
            // Run after the depth prepass and its optional depth-priming copy.
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
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
