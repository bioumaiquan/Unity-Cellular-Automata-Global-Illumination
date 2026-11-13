using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class LocalRadianceTransferDemo : MonoBehaviour
{
    private const int CoefficientsPerProbe = 4;
    private const int ProbeStride = 4 * sizeof(float);
    public const int MaxProbeCount = 1024 * 1024;
    private const GraphicsFormat VolumeFormat = GraphicsFormat.R16G16B16A16_SFloat;
    private const int SkyThreadsPerGroup = 64;

    private enum SkyQuality
    {
        [InspectorName("Low (5 directions)")] Low = 5,
        [InspectorName("Medium (9 directions)")] Medium = 9,
        [InspectorName("High (17 directions)")] High = 17
    }

    private static readonly Vector4 OmnidirectionalCone = new Vector4(0f, 0f, 0f, 1f);
    private static readonly string[] Views = { "Combined", "Direct", "Bounce only", "Sky visibility" };

    [StructLayout(LayoutKind.Sequential)]
    private struct PunctualLightData
    {
        public Vector4 PositionAndInvRangeSq;
        public Vector4 Radiance;
        public Vector4 SpotCone;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AreaLightData
    {
        public Vector4 PositionAndInvRangeSq;
        public Vector4 Radiance;
        public Vector4 RightAndHalfWidth;
        public Vector4 UpAndHalfHeight;
    }

    [SerializeField] private ComputeShader propagation;
    [SerializeField] private LrtTrunkScene trunkScene;
    [SerializeField] private Transform demoProp;
    [SerializeField] private Light demoLight;
    [SerializeField] private bool showProbes;
    [SerializeField, Range(0.02f, 0.25f), Tooltip("Marker radius as a fraction of the smallest probe spacing.")]
    private float probeMarkerRadius = 0.075f;
    [SerializeField, Range(1, 8), Tooltip("Draw every Nth probe along each axis.")]
    private int probeDrawStride = 1;
    [FormerlySerializedAs("skySampleRadius")]
    [SerializeField, Range(0f, 1f)] private float probeSampleRadius = 0.5f;
    [SerializeField, Range(0f, 1f), Tooltip("Sky occlusion strength in the Sky visibility view.")]
    private float skyOcclusionStrength = 0.5f;
    [SerializeField] private SkyQuality skyVisibilityQuality = SkyQuality.Medium;
    [SerializeField, Min(SkyThreadsPerGroup)] private int skyProbesPerFrame = 16384;
    [SerializeField, Range(1, 4)] private int stepsPerFrame = 2;
    [SerializeField, Min(1)] private int propagationFrameInterval = 1;
    [SerializeField, Min(1)] private int propagationIterations = 256;
    [SerializeField] private LrtDemoHud gameHud;

    private readonly List<PunctualLightData> punctualLights = new List<PunctualLightData>();
    private readonly List<AreaLightData> areaLights = new List<AreaLightData>();
    private readonly Vector4[] ambientCoefficients = new Vector4[7];
    private bool skyboxProbeRequested;
    private Material lastEnvironmentSkybox;
    private float lastEnvironmentIntensity;
    private double nextSkyboxProbeRequest;
#if UNITY_EDITOR
    private double skyboxProbeRefreshDeadline;
#endif
    private LrtLight[] sceneLights;
    private Texture2D blueNoise;
    private float nextLightSearch;
    private ComputeBuffer punctualLightBuffer;
    private ComputeBuffer areaLightBuffer;

    private RenderTexture volume;
    private RenderTexture fineVolume;
    private ComputeBuffer probeLayoutBuffer;
    private Texture2D probeLayoutTexture;
    private LrtProbeLayout probeLayout;
    private int publishKernel;
    private bool mixedOutputDirty;
    private int lastRequestedCoarseEdge;
    private readonly List<Bounds> gameplayBounds = new List<Bounds>();
    private readonly List<Bounds> lastGameplayBounds = new List<Bounds>();
    private Material probeMarkerMaterial;
    private MaterialPropertyBlock probeMarkerProperties;
    private int skyKernel;
    private int skyRayCount;
    private int skyNextCell;
    private bool skyUpdateRequested;
    private ComputeBuffer voxels;
    private ComputeBuffer trunkMap;
    private bool cacheUpdateFailed;
    private ComputeBuffer injection;
    private int injectionKernel;
    private bool injectionDirty;
    private bool resetTrunkCache;
    private Vector3 gridOrigin;
    private Vector3 gridExtent;
    private Vector3 lastRequestedOrigin;
    private Vector3 lastRequestedExtent;

    private int activeTrunkSize;
    private int lastRequestedTrunkSize;
    private ComputeBuffer read;
    private ComputeBuffer write;
    private Vector3Int activeGrid;
    private Vector3 cellSpacing;
    private Vector3Int lastRequestedGrid;
    private Vector3Int dispatchGroups;
    private bool gridSupported;
    private LrtTrunkScene activeTrunkScene;
    private ComputeShader activePropagation;
    private int propagateKernel;
    private int view;
    private bool geometryDirty;
    private int remainingIterations;
    private Vector4 sunDirection;
    private Vector4 sunColor;

    public Vector3Int RequestedGridDimensions => lastRequestedGrid;
    public int ActiveFineProbeCount => probeLayout?.FineProbeCount ?? 0;
    public int ActiveCoarseProbeCount => probeLayout?.CoarseProbeCount ?? 0;
    public bool UsesMixedProbeDensity => probeLayout != null;
    internal bool IsFineTrunk(int index) => probeLayout.Bricks[index].Edge == probeLayout.FineEdge;
    public string GridLimitWarning { get; private set; }

    private void OnEnable()
    {
        activeTrunkScene = trunkScene;
        activePropagation = propagation;
        skyboxProbeRequested = false;
        nextSkyboxProbeRequest = 0.0;
        if (propagation == null || trunkScene == null)
            return;

        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports3DTextures ||
            !SystemInfo.IsFormatSupported(VolumeFormat, FormatUsage.LoadStore) ||
            !SystemInfo.IsFormatSupported(VolumeFormat, FormatUsage.Linear))
        {
            Debug.LogWarning("The Local Radiance Transfer demo requires compute shaders and filterable RGBA16F 3D UAVs.", this);
            return;
        }

        blueNoise = Resources.Load<Texture2D>("BlueNoise");
        if (blueNoise != null)
        {
            Shader.SetGlobalTexture("_LrtBlueNoise", blueNoise);
            Shader.SetGlobalVector("_LrtBlueNoiseSize", new Vector4(blueNoise.width, blueNoise.height, 0f, 0f));
        }
        geometryDirty = true;
        sceneLights = null;
        nextLightSearch = 0f;
        lastRequestedGrid = Vector3Int.zero;
        GridLimitWarning = null;
        gridSupported = true;
        if (TryResizeGrid() && RefreshInputs())
        {
            AdvanceSkyVisibility();
            AdvancePropagation(16);
            PublishMixedProbes();
        }
    }

    private bool TryResizeGrid()
    {
        int trunkSize = trunkScene.ProbesPerTrunkAxis;
        int coarseEdge = 0;
        float edge = trunkScene.TrunkSizeMeters;
        Vector3Int trunkCounts = Vector3Int.one;
        Vector3 requestedOrigin = Vector3.zero;
        bool hasGeometryBounds = trunkScene.RefreshBounds(out Bounds bounds);
        if (trunkScene.GetProbeBounds(gameplayBounds, out Bounds authoredBounds))
        {
            bounds = authoredBounds;
            coarseEdge = trunkScene.CoarseProbesPerTrunkAxis;
        }
        if (coarseEdge > 0 || hasGeometryBounds)
        {
            // The automatic layout leaves a cell of space around its geometry.
            float margin = coarseEdge > 0 ? 0f : edge / trunkSize;
            Vector3 min = (bounds.min - Vector3.one * margin) / edge;
            Vector3 max = (bounds.max + Vector3.one * margin) / edge;
            var first = new Vector3(Mathf.Floor(min.x), Mathf.Floor(min.y), Mathf.Floor(min.z));
            var end = new Vector3(Mathf.Ceil(max.x), Mathf.Ceil(max.y), Mathf.Ceil(max.z));
            Vector3 counts = Vector3.Max(end - first, Vector3.one);
            // Bound the intermediate count so oversized scenes reach the normal grid limit check.
            trunkCounts = Vector3Int.CeilToInt(Vector3.Min(counts, Vector3.one * (MaxProbeCount / trunkSize + 1)));
            requestedOrigin = first * edge;
        }
        Vector3Int size = trunkCounts * trunkSize;
        Vector3 requestedExtent = (Vector3)trunkCounts * edge;
        if (size == lastRequestedGrid && trunkSize == lastRequestedTrunkSize &&
            coarseEdge == lastRequestedCoarseEdge && SameGameplayBounds() &&
            lastRequestedOrigin.Equals(requestedOrigin) && lastRequestedExtent.Equals(requestedExtent))
            return false;
        lastRequestedGrid = size;
        lastRequestedTrunkSize = trunkSize;
        lastRequestedCoarseEdge = coarseEdge;
        lastGameplayBounds.Clear();
        lastGameplayBounds.AddRange(gameplayBounds);
        lastRequestedOrigin = requestedOrigin;
        lastRequestedExtent = requestedExtent;

        GridLimitWarning = GetGridLimitWarning(size, coarseEdge > 0);
        if (GridLimitWarning != null)
        {
            Debug.LogWarning($"LRT grid {size}: {GridLimitWarning}", this);
            return false;
        }

        LrtProbeLayout newLayout = null;
        int cellCount = size.x * size.y * size.z;
        if (coarseEdge > 0)
        {
            newLayout = new LrtProbeLayout(size / trunkSize, requestedOrigin, trunkScene.TrunkSizeMeters,
                trunkSize, coarseEdge, gameplayBounds, SystemInfo.maxTexture3DSize);
            cellCount = newLayout.ProbeCount;
            if (newLayout.CoarseTiles == Vector3Int.zero || newLayout.FineTiles == Vector3Int.zero ||
                (long)cellCount * 6 * ProbeStride > SystemInfo.maxGraphicsBufferSize)
            {
                GridLimitWarning = "The packed probe atlases or light injection buffer exceed this device's limits.";
                Debug.LogWarning($"LRT grid {size}: {GridLimitWarning}", this);
                return false;
            }
        }

        int coefficientCount = cellCount * CoefficientsPerProbe;
        RenderTexture newVolume = null;
        RenderTexture newFineVolume = null;
        ComputeBuffer newLayoutBuffer = null;
        Texture2D newLayoutTexture = null;
        ComputeBuffer newInjection = null;
        ComputeBuffer newRead = null;
        ComputeBuffer newWrite = null;
        try
        {
            if (newLayout != null)
            {
                Vector3Int coarseSize = newLayout.CoarseBricks == 0 ? Vector3Int.one : newLayout.CoarseTiles * (coarseEdge + 2);
                Vector3Int fineSize = newLayout.FineBricks == 0 ? Vector3Int.one : newLayout.FineTiles * (trunkSize + 2);
                newVolume = CreateVolume(coarseSize, CoefficientsPerProbe, VolumeFormat, "LRT coarse probe bricks");
                newFineVolume = CreateVolume(fineSize, CoefficientsPerProbe, VolumeFormat, "LRT fine probe bricks");
                newLayoutBuffer = new ComputeBuffer(newLayout.Bricks.Length, 4 * sizeof(int));
                newLayoutBuffer.SetData(newLayout.Bricks);
                // Fragment shaders read the edge and slot through a texture on GLES.
                int layoutWidth = Mathf.Min(newLayout.Bricks.Length, SystemInfo.maxTextureSize);
                int layoutHeight = 1 + (newLayout.Bricks.Length - 1) / layoutWidth;
                if (layoutHeight > SystemInfo.maxTextureSize)
                    throw new InvalidOperationException("The probe layout texture exceeds this device's size limit.");

                newLayoutTexture = new Texture2D(layoutWidth, layoutHeight, GraphicsFormat.R8G8B8A8_UNorm,
                    TextureCreationFlags.None)
                {
                    name = "LRT probe layout",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                var layoutTexels = newLayoutTexture.GetRawTextureData<Color32>();
                // The slot fits in RGB (bounded by MaxProbeCount), and the edge fits in A.
                for (int i = 0; i < newLayout.Bricks.Length; i++)
                {
                    var brick = newLayout.Bricks[i];
                    layoutTexels[i] = new Color32((byte)(brick.Slot & 255), (byte)((brick.Slot >> 8) & 255),
                        (byte)(brick.Slot >> 16), (byte)brick.Edge);
                }
                newLayoutTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                newInjection = new ComputeBuffer(cellCount * 6, ProbeStride);
            }
            else
                newVolume = CreateVolume(size, CoefficientsPerProbe, VolumeFormat, "LRT spherical harmonics probe volume");

            newRead = new ComputeBuffer(coefficientCount, ProbeStride);
            newWrite = new ComputeBuffer(coefficientCount, ProbeStride);
            var initialCoefficients = new Vector4[coefficientCount];
            // Until a probe's first sky batch, its mean visibility is fully open.
            for (int i = 0; i < coefficientCount; i += CoefficientsPerProbe)
                initialCoefficients[i].w = 1f;
            newRead.SetData(initialCoefficients);
        }
        catch (Exception error)
        {
            DestroyVolume(newVolume);
            DestroyVolume(newFineVolume);
            newLayoutBuffer?.Release();
            DestroyTexture(newLayoutTexture);
            newInjection?.Release();
            newRead?.Release();
            newWrite?.Release();
            Debug.LogWarning($"LRT could not allocate grid {size}: {error.Message}", this);
            return false;
        }

        ReleaseGrid();
        volume = newVolume;
        fineVolume = newFineVolume;
        probeLayout = newLayout;
        probeLayoutBuffer = newLayoutBuffer;
        probeLayoutTexture = newLayoutTexture;
        injection = newInjection;
        propagateKernel = propagation.FindKernel(probeLayout != null ? "PropagateMixed" : "PropagateTrunks");
        injectionKernel = propagation.FindKernel(probeLayout != null ? "InjectMixed" : "InjectTrunks");
        skyKernel = propagation.FindKernel(probeLayout != null ? "TraceSkyMixed" : "TraceSkyVisibility");
        if (probeLayout != null)
            publishKernel = propagation.FindKernel("PublishMixed");
        resetTrunkCache = true;
        gridOrigin = requestedOrigin;
        gridExtent = requestedExtent;
        read = newRead;
        write = newWrite;
        activeGrid = size;
        activeTrunkSize = trunkSize;
        cellSpacing = Vector3.one * (trunkScene.TrunkSizeMeters / trunkSize);
        // Uniform propagation uses 4 × 4 × 4 threads and rejects out-of-range cells.
        dispatchGroups = new Vector3Int((size.x + 3) / 4, (size.y + 3) / 4, (size.z + 3) / 4);
        geometryDirty = true;

        Shader.SetGlobalTexture("_LrtVolume", volume);
        Shader.SetGlobalInt("_LrtMixedEnabled", probeLayout != null ? 1 : 0);
        if (probeLayout != null)
        {
            Shader.SetGlobalTexture("_LrtFineVolume", fineVolume);
            Shader.SetGlobalBuffer("_LrtProbeLayout", probeLayoutBuffer);
            Shader.SetGlobalTexture("_LrtProbeLayoutTexture", probeLayoutTexture);
            Shader.SetGlobalInteger("_LrtProbeLayoutTextureWidth", probeLayoutTexture.width);
            Shader.SetGlobalVector("_LrtProbeTrunkDimensions", (Vector3)(size / trunkSize));
            Shader.SetGlobalVector("_LrtCoarseAtlasSize", new Vector4(volume.width, volume.height, volume.volumeDepth));
            Shader.SetGlobalVector("_LrtFineAtlasSize", new Vector4(fineVolume.width, fineVolume.height, fineVolume.volumeDepth));
            Shader.SetGlobalVector("_LrtCoarseTiles", (Vector3)probeLayout.CoarseTiles);
            Shader.SetGlobalVector("_LrtFineTiles", (Vector3)probeLayout.FineTiles);
            Shader.SetGlobalFloat("_LrtProbeTrunkEdge", trunkScene.TrunkSizeMeters);
            Shader.SetGlobalInt("_LrtFineProbeEdge", probeLayout.FineEdge);
        }
        Shader.SetGlobalVector("_LrtCellSpacing", cellSpacing);
        Shader.SetGlobalVector("_LrtGridExtent", gridExtent);
        Shader.SetGlobalVector("_LrtGridDimensions", new Vector4(size.x, size.y, size.z, 0));
        Shader.SetGlobalVector("_LrtGridOrigin", gridOrigin);
        return true;
    }

    private bool SameGameplayBounds()
    {
        if (gameplayBounds.Count != lastGameplayBounds.Count)
            return false;
        for (int i = 0; i < gameplayBounds.Count; i++)
            if (!gameplayBounds[i].Equals(lastGameplayBounds[i]))
                return false;
        return true;
    }

    private static string GetGridLimitWarning(Vector3Int size, bool mixed)
    {
        if (size.x <= 0 || size.y <= 0 || size.z <= 0)
            return "Each grid axis must contain at least one probe.";

        long cells = (long)size.x * size.y * size.z;
        if (cells > MaxProbeCount)
            return mixed ? $"The fine geometry grid exceeds the limit of {MaxProbeCount:N0} cells."
                : $"The requested grid exceeds the limit of {MaxProbeCount:N0} probes.";

        int maxDimension = SystemInfo.maxTexture3DSize;
        int maxDepth = maxDimension / CoefficientsPerProbe;
        if (!mixed && (size.x > maxDimension || size.y > maxDimension || size.z > maxDepth))
            return $"This device supports a probe grid up to {maxDimension} × {maxDimension} × {maxDepth} due to its 3D texture limit.";

        int largestBufferBytesPerProbe = (mixed ? 1 : 6) * ProbeStride;
        long bufferProbeLimit = SystemInfo.maxGraphicsBufferSize / largestBufferBytesPerProbe;
        if (cells > bufferProbeLimit)
            return $"This device supports up to {bufferProbeLimit:N0} probes due to its graphics buffer limit.";

        return null;
    }

    private void Update()
    {
        if (activePropagation != propagation || activeTrunkScene != trunkScene)
        {
            OnDisable();
            OnEnable();
            return;
        }
        if (!gridSupported)
            return;
        bool resized = TryResizeGrid();
        if (volume == null)
            return;
        if (!RefreshInputs())
            return;
        AdvanceSkyVisibility();
        if (!Application.isPlaying || resized || Time.frameCount % Mathf.Max(1, propagationFrameInterval) == 0)
        {
            int steps = Application.isPlaying ? stepsPerFrame : 8;
            AdvancePropagation(resized ? 16 : steps);
        }
        PublishMixedProbes();
    }

    private void LateUpdate()
    {
        if (!showProbes || volume == null || !SystemInfo.supportsInstancing)
            return;

        if (probeMarkerMaterial == null)
        {
            Shader shader = Resources.Load<Shader>("LrtProbeMarkers");
            if (shader == null || !shader.isSupported)
                return;
            probeMarkerMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            probeMarkerProperties = new MaterialPropertyBlock();
        }

        if (probeLayout != null)
        {
            DrawProbeMarkers(Vector3Int.one * probeLayout.CoarseEdge, probeLayout.CoarseBricks, probeLayout.CoarseEdge, 0);
            DrawProbeMarkers(Vector3Int.one * probeLayout.FineEdge, probeLayout.FineBricks, probeLayout.FineEdge, probeLayout.CoarseBricks);
        }
        else
            DrawProbeMarkers(activeGrid, 1, 0, 0);
#if UNITY_EDITOR
        if (!Application.isPlaying)
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
    }

    private void DrawProbeMarkers(Vector3Int grid, int bricks, int edge, int trunkOffset)
    {
        if (bricks == 0)
            return;

        int stride = Mathf.Clamp(probeDrawStride, 1, 8);
        Vector3Int dimensions = (grid + Vector3Int.one * (stride - 1)) / stride;
        float spacing = edge > 0 ? trunkScene.TrunkSizeMeters / edge : Mathf.Min(cellSpacing.x, cellSpacing.y, cellSpacing.z);
        float radius = spacing * probeMarkerRadius;
        probeMarkerProperties.SetInt("_LrtMarkerEdge", edge);
        probeMarkerProperties.SetInt("_LrtMarkerTrunkOffset", trunkOffset);
        probeMarkerProperties.SetVector("_LrtProbeDrawDimensions", new Vector4(dimensions.x, dimensions.y, dimensions.z));
        probeMarkerProperties.SetFloat("_LrtProbeMarkerRadius", radius);
        probeMarkerProperties.SetInt("_LrtProbeDrawStride", stride);
        var renderParams = new RenderParams(probeMarkerMaterial)
        {
            worldBounds = new Bounds(gridOrigin + gridExtent * 0.5f, gridExtent + Vector3.one * (2f * radius)),
            matProps = probeMarkerProperties,
            layer = gameObject.layer,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            lightProbeUsage = LightProbeUsage.Off,
            reflectionProbeUsage = ReflectionProbeUsage.Off
        };
        Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, bricks * dimensions.x * dimensions.y * dimensions.z);
    }

    private bool RefreshInputs()
    {
        try
        {
            bool cacheChanged = trunkScene.UpdateCache(ref trunkMap, ref voxels, ref injection,
                activeGrid, gridOrigin, cellSpacing, activeTrunkSize, resetTrunkCache, probeLayout == null);
            geometryDirty |= cacheChanged;
            skyUpdateRequested |= cacheChanged;
            resetTrunkCache = false;
            cacheUpdateFailed = false;
        }
        catch (Exception error)
        {
            if (!cacheUpdateFailed)
                Debug.LogWarning($"LRT could not update the trunk geometry cache: {error.Message}", this);
            cacheUpdateFailed = true;
            return false;
        }
        if (RefreshLights() || geometryDirty)
        {
            remainingIterations = Mathf.Max(1, propagationIterations);
            injectionDirty = true;
        }

        UploadEnvironmentLighting();
        Shader.SetGlobalFloat("_LrtSkySampleRadius", blueNoise != null ? probeSampleRadius : 0f);
        Shader.SetGlobalFloat("_LrtSkyOcclusionStrength", Mathf.Clamp01(skyOcclusionStrength));
        Shader.SetGlobalFloat("_LrtTrunkDebug", view);
        return true;
    }

    private void AdvanceSkyVisibility()
    {
        int rayCount = skyVisibilityQuality == SkyQuality.High ? 17 :
            skyVisibilityQuality == SkyQuality.Low ? 5 : 9;
        if (rayCount != skyRayCount)
        {
            skyRayCount = rayCount;
            skyNextCell = 0;
            skyUpdateRequested = true;
        }
        if (skyNextCell == 0)
        {
            if (!skyUpdateRequested)
                return;
            skyUpdateRequested = false;
        }

        int cellCount = probeLayout?.ProbeCount ?? activeGrid.x * activeGrid.y * activeGrid.z;
        int batchSize = Mathf.Clamp(skyProbesPerFrame, SkyThreadsPerGroup, MaxProbeCount);
        int end = Mathf.Min(cellCount, skyNextCell + batchSize);
        SetComputeGrid();
        propagation.SetInt("_SkyFirstCell", skyNextCell);
        propagation.SetInt("_SkyEndCell", end);
        propagation.SetInt("_SkyRayCount", skyRayCount);
        propagation.SetBuffer(skyKernel, "_TrunkMap", trunkMap);
        propagation.SetBuffer(skyKernel, "_Voxels", voxels);
        // Dispatches are ordered. The active SH buffer is the source for both passes.
        propagation.SetBuffer(skyKernel, "_SkySH", read);
        if (probeLayout != null)
        {
            SetMixedGrid(skyKernel);
            mixedOutputDirty = true;
        }
        else
            propagation.SetTexture(skyKernel, "_LrtVolume", volume);
        int groups = (end - skyNextCell + SkyThreadsPerGroup - 1) / SkyThreadsPerGroup;
        propagation.Dispatch(skyKernel, groups, 1, 1);
        skyNextCell = end == cellCount ? 0 : end;
#if UNITY_EDITOR
        if (!Application.isPlaying && (skyNextCell > 0 || skyUpdateRequested))
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
    }

    private void AdvancePropagation(int stepLimit)
    {
        int steps = Mathf.Min(stepLimit, remainingIterations);
        if (steps <= 0)
            return;

        Tick(steps);
        remainingIterations -= steps;
#if UNITY_EDITOR
        if (!Application.isPlaying && remainingIterations > 0)
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
    }

    private void SetComputeGrid()
    {
        propagation.SetInts("_GridSize", activeGrid.x, activeGrid.y, activeGrid.z);
        Vector3Int size = trunkScene.TrunkDimensions;
        propagation.SetInts("_TrunkGridSize", size.x, size.y, size.z);
        propagation.SetInt("_TrunkSize", activeTrunkSize);
        propagation.SetInt("_CellsPerTrunk", activeTrunkSize * activeTrunkSize * activeTrunkSize);
        propagation.SetVector("_LrtGridOrigin", gridOrigin);
        propagation.SetVector("_LrtCellSpacing", cellSpacing);
    }

    private void SetMixedGrid(int kernel)
    {
        propagation.SetBuffer(kernel, "_LrtProbeLayout", probeLayoutBuffer);
        propagation.SetInt("_MixedProbeCount", probeLayout.ProbeCount);
        propagation.SetInt("_MixedCoarseProbeCount", probeLayout.CoarseProbeCount);
        propagation.SetInt("_MixedCoarseTrunks", probeLayout.CoarseBricks);
        propagation.SetInt("_MixedCoarseEdge", probeLayout.CoarseEdge);
        propagation.SetFloat("_LrtProbeTrunkEdge", trunkScene.TrunkSizeMeters);
    }

    private void PublishMixedProbes()
    {
        if (!mixedOutputDirty || probeLayout == null)
            return;

        SetComputeGrid();
        SetMixedGrid(publishKernel);
        Vector3Int coarse = probeLayout.CoarseTiles;
        Vector3Int fine = probeLayout.FineTiles;
        propagation.SetInts("_MixedCoarseTiles", coarse.x, coarse.y, coarse.z);
        propagation.SetInts("_MixedFineTiles", fine.x, fine.y, fine.z);
        propagation.SetInt("_MixedPublishCount", probeLayout.PublishCellCount);
        propagation.SetTexture(publishKernel, "_LrtVolume", volume);
        propagation.SetTexture(publishKernel, "_LrtFineVolume", fineVolume);
        propagation.SetBuffer(publishKernel, "_TrunkMap", trunkMap);
        propagation.SetBuffer(publishKernel, "_Voxels", voxels);
        propagation.SetBuffer(publishKernel, "_Read", read);
        propagation.Dispatch(publishKernel, (probeLayout.PublishCellCount + 63) / 64, 1, 1);
        mixedOutputDirty = false;
    }

    private void Tick(int steps)
    {
        SetComputeGrid();
        propagation.SetVector("_LrtSunDirection", sunDirection);
        propagation.SetVector("_LrtSunColor", sunColor);

        geometryDirty = false;

        int occupiedTrunks = trunkScene.OccupiedTrunkCount;
        if (injectionDirty && occupiedTrunks > 0)
        {
            int occupiedCells = probeLayout?.ProbeCount ?? occupiedTrunks * activeTrunkSize * activeTrunkSize * activeTrunkSize;
            if (probeLayout != null)
                SetMixedGrid(injectionKernel);
            propagation.SetInt("_OccupiedCells", occupiedCells);
            propagation.SetBuffer(injectionKernel, "_TrunkMap", trunkMap);
            propagation.SetBuffer(injectionKernel, "_Voxels", voxels);
            propagation.SetBuffer(injectionKernel, "_Injection", injection);
            propagation.SetBuffer(injectionKernel, "_LrtPunctualLights", punctualLightBuffer);
            propagation.SetBuffer(injectionKernel, "_LrtAreaLights", areaLightBuffer);
            propagation.Dispatch(injectionKernel, (occupiedCells + 63) / 64, 1, 1);
        }
        propagation.SetBuffer(propagateKernel, "_TrunkMap", trunkMap);
        propagation.SetBuffer(propagateKernel, "_InjectionRead", injection);
        injectionDirty = false;
        propagation.SetBuffer(propagateKernel, "_Voxels", voxels);
        if (probeLayout != null)
            SetMixedGrid(propagateKernel);
        else
            propagation.SetTexture(propagateKernel, "_LrtVolume", volume);
        for (int i = 0; i < steps; i++)
        {
            propagation.SetBuffer(propagateKernel, "_Read", read);
            propagation.SetBuffer(propagateKernel, "_Write", write);
            if (probeLayout != null)
                propagation.Dispatch(propagateKernel, (probeLayout.ProbeCount + 63) / 64, 1, 1);
            else
                propagation.Dispatch(propagateKernel, dispatchGroups.x, dispatchGroups.y, dispatchGroups.z);
            (read, write) = (write, read);
        }
        mixedOutputDirty |= probeLayout != null;
    }

    private bool RefreshLights()
    {
        if (sceneLights == null || !Application.isPlaying || Time.realtimeSinceStartup >= nextLightSearch)
        {
            sceneLights = FindObjectsByType<LrtLight>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
            nextLightSearch = Time.realtimeSinceStartup + 0.5f;
        }

        int punctualCount = 0;
        int areaCount = 0;
        bool punctualChanged = punctualLightBuffer == null;
        bool areaChanged = areaLightBuffer == null;
        Vector4 direction = Vector4.zero;
        Vector4 directionalColor = Vector4.zero;
        foreach (LrtLight light in sceneLights)
        {
            if (light == null || light.gameObject.scene != gameObject.scene ||
                !light.TryGetInjectionSettings(out LrtLight.Settings settings))
                continue;

            Transform source = light.transform;
            Vector4 color = settings.LinearColor * (settings.Intensity * settings.IndirectMultiplier);
            color.w = 0f;
            if (settings.Shape == LrtLight.LightShape.Directional)
            {
                // The propagation kernels support one directional source.
                if (direction.Equals(Vector4.zero) && (color.x > 0f || color.y > 0f || color.z > 0f))
                {
                    direction = -source.forward;
                    directionalColor = color;
                }
                continue;
            }

            Vector4 position = source.position;
            position.w = 1f / Mathf.Max(0.0001f, settings.Range * settings.Range);
            if (settings.Shape == LrtLight.LightShape.Area)
            {
                areaChanged |= SetAreaLight(areaCount++, position, color, source, settings.Size);
                continue;
            }

            Vector4 spotCone = OmnidirectionalCone;
            if (settings.Shape == LrtLight.LightShape.Spot)
            {
                float outerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * settings.SpotAngle);
                float innerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * settings.InnerSpotAngle);
                float scale = 1f / Mathf.Max(0.001f, innerCos - outerCos);
                spotCone = -source.forward * scale;
                spotCone.w = -outerCos * scale;
            }
            punctualChanged |= SetPunctualLight(punctualCount++, position, color, spotCone);
        }

        if (punctualCount < punctualLights.Count)
        {
            punctualLights.RemoveRange(punctualCount, punctualLights.Count - punctualCount);
            punctualChanged = true;
        }
        if (areaCount < areaLights.Count)
        {
            areaLights.RemoveRange(areaCount, areaLights.Count - areaCount);
            areaChanged = true;
        }
        if (punctualChanged)
            UploadPunctualLights();
        if (areaChanged)
            UploadAreaLights();

        bool directionalChanged = !sunDirection.Equals(direction) || !sunColor.Equals(directionalColor);
        sunDirection = direction;
        sunColor = directionalColor;
        return punctualChanged || areaChanged || directionalChanged;
    }

    private bool SetPunctualLight(int index, Vector4 position, Vector4 color, Vector4 spotCone)
    {
        var data = new PunctualLightData
        {
            PositionAndInvRangeSq = position,
            Radiance = color,
            SpotCone = spotCone
        };
        if (index == punctualLights.Count)
        {
            punctualLights.Add(data);
            return true;
        }

        PunctualLightData previous = punctualLights[index];
        punctualLights[index] = data;
        return !previous.PositionAndInvRangeSq.Equals(position) || !previous.Radiance.Equals(color) ||
               !previous.SpotCone.Equals(spotCone);
    }

    private void UploadPunctualLights()
    {
        if (punctualLightBuffer == null || punctualLightBuffer.count < punctualLights.Count)
        {
            var newBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(Mathf.Max(1, punctualLights.Count)), 12 * sizeof(float));
            punctualLightBuffer?.Release();
            punctualLightBuffer = newBuffer;
        }
        if (punctualLights.Count > 0)
            punctualLightBuffer.SetData(punctualLights);

        propagation.SetInt("_LrtPunctualLightCount", punctualLights.Count);
    }

    private bool SetAreaLight(int index, Vector4 position, Vector4 color, Transform source, Vector2 size)
    {
        Vector4 right = source.right;
        Vector4 up = source.up;
        right.w = size.x * 0.5f;
        up.w = size.y * 0.5f;
        var data = new AreaLightData
        {
            PositionAndInvRangeSq = position,
            Radiance = color,
            RightAndHalfWidth = right,
            UpAndHalfHeight = up
        };
        if (index == areaLights.Count)
        {
            areaLights.Add(data);
            return true;
        }

        AreaLightData previous = areaLights[index];
        areaLights[index] = data;
        return !previous.PositionAndInvRangeSq.Equals(position) || !previous.Radiance.Equals(color) ||
               !previous.RightAndHalfWidth.Equals(right) || !previous.UpAndHalfHeight.Equals(up);
    }

    private void UploadAreaLights()
    {
        int count = areaLights.Count;
        if (areaLightBuffer == null || areaLightBuffer.count < count)
        {
            var newBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(Mathf.Max(1, count)), 16 * sizeof(float));
            areaLightBuffer?.Release();
            areaLightBuffer = newBuffer;
        }
        if (count > 0)
            areaLightBuffer.SetData(areaLights);

        propagation.SetInt("_LrtAreaLightCount", count);
    }

    private void UploadEnvironmentLighting()
    {
        AmbientMode mode = RenderSettings.ambientMode;
        Shader.SetGlobalInt("_LrtAmbientMode", (int)mode);
        if (mode == AmbientMode.Skybox)
            RefreshSkyboxProbe();
        else
            skyboxProbeRequested = false;

        if (mode == AmbientMode.Flat || mode == AmbientMode.Trilight)
        {
            Color sky = mode == AmbientMode.Flat ? RenderSettings.ambientLight : RenderSettings.ambientSkyColor;
            Shader.SetGlobalVector("_LrtAmbientSky", sky.linear);
            Shader.SetGlobalVector("_LrtAmbientEquator", RenderSettings.ambientEquatorColor.linear);
            Shader.SetGlobalVector("_LrtAmbientGround", RenderSettings.ambientGroundColor.linear);
            return;
        }

        // Unity's skybox ambient probe already incorporates Environment Lighting intensity.
        var sh = new SHCoefficients(RenderSettings.ambientProbe);
        ambientCoefficients[0] = sh.SHAr;
        ambientCoefficients[1] = sh.SHAg;
        ambientCoefficients[2] = sh.SHAb;
        ambientCoefficients[3] = sh.SHBr;
        ambientCoefficients[4] = sh.SHBg;
        ambientCoefficients[5] = sh.SHBb;
        ambientCoefficients[6] = sh.SHC;
        Shader.SetGlobalVectorArray("_LrtAmbientSH", ambientCoefficients);
    }

    private void RefreshSkyboxProbe()
    {
        Material skybox = RenderSettings.skybox;
        float intensity = RenderSettings.ambientIntensity;
        double now = Time.realtimeSinceStartupAsDouble;
        bool needsRefresh = !skyboxProbeRequested || skybox != lastEnvironmentSkybox ||
                            !intensity.Equals(lastEnvironmentIntensity);
        if (needsRefresh && now >= nextSkyboxProbeRequest)
        {
            skyboxProbeRequested = true;
            lastEnvironmentSkybox = skybox;
            lastEnvironmentIntensity = intensity;
            DynamicGI.UpdateEnvironment();
            nextSkyboxProbeRequest = now + 0.25;
#if UNITY_EDITOR
            skyboxProbeRefreshDeadline = now + 1.0;
#endif
        }

#if UNITY_EDITOR
        // ExecuteAlways may be idle while Unity asynchronously updates the ambient probe.
        if (!Application.isPlaying && (needsRefresh || now < skyboxProbeRefreshDeadline))
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
    }

    internal bool HasProbeGrid => volume != null;
    internal bool HasDemoProp => demoProp != null;
    internal bool HasDemoLight => demoLight != null;
    internal bool IsDemoPropVisible => demoProp != null && demoProp.gameObject.activeSelf;
    internal bool IsDemoLightOn => demoLight != null && demoLight.enabled;
    internal int DebugView { get => view; set => view = value; }
    internal bool ShowProbes { get => showProbes; set => showProbes = value; }
    internal string TrunkStatus => trunkScene != null
        ? $"{trunkScene.TrunkCount} trunks  /  {trunkScene.LastUpdatedTrunks} rebuilt in last update"
        : "";
    internal string PropagationSettings => $"{stepsPerFrame} propagation step(s) every {propagationFrameInterval} frame(s)";
    internal string PropagationStatus => remainingIterations > 0
        ? $"{remainingIterations} propagation steps remaining"
        : "Propagation idle";
    internal string SkyVisibilityStatus => skyNextCell > 0 || skyUpdateRequested
        ? "Updating sky visibility"
        : "Sky visibility ready";

    internal string ProbeGridTitle
    {
        get
        {
            Vector3Int size = activeGrid;
            return probeLayout != null ? $"LOCAL RADIANCE TRANSFER   /   {probeLayout.ProbeCount:N0} probes" :
                !HasProbeGrid ? "LOCAL RADIANCE TRANSFER" :
                $"LOCAL RADIANCE TRANSFER   /   {size.x} × {size.y} × {size.z} probes";
        }
    }

    internal void ToggleDemoProp()
    {
        if (HasDemoProp)
            demoProp.gameObject.SetActive(!IsDemoPropVisible);
    }

    internal void ToggleDemoLight()
    {
        if (HasDemoLight)
            demoLight.enabled = !IsDemoLightOn;
    }

    private void OnGUI()
    {
        if (!Application.isPlaying || gameHud != null)
            return;

        GUILayout.BeginArea(new Rect(16, 16, 400, 160), GUI.skin.box);
        GUILayout.Label(ProbeGridTitle);
        if (volume != null)
        {
            view = GUILayout.Toolbar(view, Views);
            if (trunkScene != null)
            {
                GUILayout.Label(TrunkStatus);
                if (demoProp != null || demoLight != null)
                {
                    GUILayout.BeginHorizontal();
                    if (HasDemoProp && GUILayout.Button(IsDemoPropVisible ? "Hide building" : "Show building"))
                        ToggleDemoProp();
                    if (HasDemoLight && GUILayout.Button(IsDemoLightOn ? "Turn lamp off" : "Turn lamp on"))
                        ToggleDemoLight();
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(PropagationSettings);
            showProbes = GUILayout.Toggle(showProbes, "Show probes");
            GUILayout.Label(PropagationStatus);
            if (trunkScene != null)
                GUILayout.Label(SkyVisibilityStatus);
        }
        else
        {
            GUILayout.Label("Probe grid unavailable. See the Unity Console.");
        }

        GUILayout.EndArea();
    }

    private void OnDisable()
    {
        gridSupported = false;
        if (probeMarkerMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(probeMarkerMaterial);
            else
                DestroyImmediate(probeMarkerMaterial);
            probeMarkerMaterial = null;
        }
        if (volume != null)
        {
            Shader.SetGlobalFloat("_LrtTrunkDebug", 0);
            Shader.SetGlobalVector("_LrtCellSpacing", Vector4.zero);
            Shader.SetGlobalTexture("_LrtVolume", null);
        }
        ReleaseGrid();
        punctualLightBuffer?.Release();
        punctualLightBuffer = null;
        areaLightBuffer?.Release();
        areaLightBuffer = null;
    }

    private void ReleaseGrid()
    {
        DestroyVolume(volume);
        DestroyVolume(fineVolume);
        volume = null;
        fineVolume = null;
        probeLayoutBuffer?.Release();
        probeLayoutBuffer = null;
        probeLayout = null;
        mixedOutputDirty = false;
        Shader.SetGlobalInt("_LrtMixedEnabled", 0);
        Shader.SetGlobalTexture("_LrtFineVolume", null);
        Shader.SetGlobalTexture("_LrtProbeLayoutTexture", null);
        DestroyTexture(probeLayoutTexture);
        probeLayoutTexture = null;
        skyRayCount = 0;
        skyNextCell = 0;
        skyUpdateRequested = false;
        voxels?.Release();
        trunkMap?.Release();
        trunkMap = null;
        cacheUpdateFailed = false;
        injection?.Release();
        injection = null;
        read?.Release();
        write?.Release();
        voxels = null;
        read = null;
        write = null;
    }

    private static RenderTexture CreateVolume(Vector3Int size, int depthMultiplier, GraphicsFormat format, string name)
    {
        var texture = new RenderTexture(size.x, size.y, 0, format)
        {
            name = name,
            dimension = TextureDimension.Tex3D,
            volumeDepth = size.z * depthMultiplier,
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        if (texture.Create())
            return texture;

        DestroyVolume(texture);
        throw new InvalidOperationException($"Could not create {name}.");
    }

    private static void DestroyVolume(RenderTexture texture)
    {
        if (texture == null)
            return;
        texture.Release();
        DestroyTexture(texture);
    }

    private static void DestroyTexture(Texture texture)
    {
        if (texture == null)
            return;
        if (Application.isPlaying)
            Destroy(texture);
        else
            DestroyImmediate(texture);
    }
}
