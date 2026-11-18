using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class LrtTrunkScene : MonoBehaviour
{
    [Serializable]
    public struct Instance
    {
        public Transform Root;
        public LrtColorSdf Data;
    }

    private sealed class Primitive
    {
        public Transform Root;
        public LrtColorSdf Data;
        public Matrix4x4 LocalToWorld;
        public Matrix4x4 WorldToLocal;
        public Bounds WorldBounds;
        public float DistanceToWorld;
        public int Revision;
        public bool HasBounds;
        public bool Active;
        public readonly List<Trunk> Trunks = new List<Trunk>();
    }

    private sealed class Trunk
    {
        public Vector3Int Coordinate;
        public bool Dirty = true;
        public readonly List<Primitive> Primitives = new List<Primitive>();
    }

    [SerializeField, HideInInspector] private Instance[] instances = Array.Empty<Instance>();
#if UNITY_EDITOR
    [SerializeField, Range(8, 40)] private int bakeResolution = 16;
#endif
    [SerializeField, Min(0.1f), Tooltip("World-space edge length of each cubic trunk, in meters.")]
    private float trunkSizeMeters = 10f;
    [SerializeField, Range(4, 32), Tooltip("Number of probes along each axis of a trunk.")]
    private int probesPerTrunkAxis = 8;
    [SerializeField] private bool showTrunks = true;
    [SerializeField, Tooltip("World bounds of the coarse probe volume.")]
    private BoxCollider sceneBounds;
    [SerializeField, Tooltip("Trunks intersecting any of these boxes use the fine probe density.")]
    private BoxCollider[] gameplayRegions = Array.Empty<BoxCollider>();
    [SerializeField, Range(1, 16)] private int coarseProbesPerTrunkAxis = 4;

    private readonly Dictionary<Transform, Primitive> runtimeInstances = new Dictionary<Transform, Primitive>();
    private readonly Dictionary<Transform, Primitive> bakedInstances = new Dictionary<Transform, Primitive>();
    private readonly List<Transform> removed = new List<Transform>();
    private Vector4[] staging;
    private float[] nearestDistance;
    private int activeTrunkSize;
    private Trunk[] trunks;
    // The first half maps a grid trunk to a packed slot (-1 for air).
    // The second half maps packed slots back to grid trunks for light injection.
    private int[] trunkMap;
    private bool trunkMapDirty;
    internal int OccupiedTrunkCount { get; private set; }
    private Instance[] currentBake;
    private Vector3Int trunkDimensions;
    private Vector3 origin;
    private Vector3 spacing;
    private Vector3 trunkExtent;
    public int LastUpdatedTrunks { get; private set; }
    public int TrunkCount => trunks?.Length ?? 0;
    public Vector3Int TrunkDimensions => trunks != null ? trunkDimensions : Vector3Int.zero;
    public int BakedInstanceCount => instances.Length;
    public int ProbesPerTrunkAxis => Mathf.Clamp(probesPerTrunkAxis, 4, 32);
    public float TrunkSizeMeters => float.IsNaN(trunkSizeMeters) || float.IsInfinity(trunkSizeMeters)
        ? 10f : Mathf.Max(0.1f, trunkSizeMeters);

#if UNITY_EDITOR
    public int BakeResolution => bakeResolution;

    public void SetBake(Instance[] baked)
    {
        instances = baked;
        currentBake = null;
    }
#endif

    // Register instances spawned after the scene bake. Disabling a root removes its occupancy.
    public void Register(Transform root, LrtColorSdf data)
    {
        if (root == null || data == null)
            throw new ArgumentNullException(root == null ? nameof(root) : nameof(data));
        if (root.gameObject.scene != gameObject.scene || bakedInstances.ContainsKey(root) ||
            Array.Exists(instances, instance => instance.Root == root))
            throw new ArgumentException("Use a new instance in the same scene.", nameof(root));

        Unregister(root);
        runtimeInstances.Add(root, new Primitive { Root = root, Data = data });
    }

    public void Unregister(Transform root)
    {
        if (ReferenceEquals(root, null) || !runtimeInstances.TryGetValue(root, out Primitive primitive))
            return;
        RemoveFromTrunks(primitive);
        runtimeInstances.Remove(root);
    }

    internal int CoarseProbesPerTrunkAxis => Mathf.Clamp(coarseProbesPerTrunkAxis, 1, ProbesPerTrunkAxis - 1);

    internal bool GetProbeBounds(List<Bounds> regions, out Bounds bounds)
    {
        regions.Clear();
        if (sceneBounds == null || sceneBounds.gameObject.scene != gameObject.scene)
        {
            bounds = default;
            return false;
        }

        bounds = ColliderWorldBounds(sceneBounds);
        foreach (BoxCollider region in gameplayRegions)
        {
            if (region == null || region.gameObject.scene != gameObject.scene)
                continue;

            Bounds world = ColliderWorldBounds(region);
            Vector3 min = Vector3.Max(world.min, bounds.min);
            Vector3 max = Vector3.Min(world.max, bounds.max);
            if (max.x > min.x && max.y > min.y && max.z > min.z)
                regions.Add(new Bounds((min + max) * 0.5f, max - min));
        }
        return true;
    }

    private static Bounds ColliderWorldBounds(BoxCollider box)
    {
        return WorldBounds(box.transform.localToWorldMatrix, new Bounds(box.center, box.size));
    }

    internal bool RefreshBounds(out Bounds bounds)
    {
        if (currentBake != instances)
        {
            foreach (Primitive primitive in bakedInstances.Values)
                RemoveFromTrunks(primitive);
            bakedInstances.Clear();
            foreach (Instance instance in instances)
            {
                if (instance.Root != null && instance.Data != null)
                    bakedInstances[instance.Root] = new Primitive { Root = instance.Root, Data = instance.Data };
            }
            currentBake = instances;
        }

        Bounds combined = default;
        bool hasBounds = false;
        foreach (Primitive primitive in bakedInstances.Values)
            Include(primitive);
        foreach (var entry in runtimeInstances)
        {
            Include(entry.Value);
            if (entry.Key == null)
                removed.Add(entry.Key);
        }
        foreach (Transform root in removed)
            runtimeInstances.Remove(root);
        removed.Clear();
        bounds = combined;
        return hasBounds;

        void Include(Primitive primitive)
        {
            UpdatePrimitive(primitive);
            if (!primitive.HasBounds)
                return;
            if (hasBounds)
                combined.Encapsulate(primitive.WorldBounds);
            else
                combined = primitive.WorldBounds;
            hasBounds = true;
        }
    }

    internal bool UpdateCache(ref ComputeBuffer mapBuffer, ref ComputeBuffer voxels, ref ComputeBuffer injection,
        Vector3Int grid, Vector3 gridOrigin, Vector3 cellSpacing, int trunkSize, bool reset, bool cacheInjection)
    {
        if (reset || trunks == null)
            Initialize(grid, gridOrigin, cellSpacing, trunkSize);

        UpdateSlots();
        ResizeBuffers(ref mapBuffer, ref voxels, ref injection, cacheInjection);

        int updated = 0;
        for (int slot = 0; slot < OccupiedTrunkCount; slot++)
        {
            Trunk trunk = trunks[trunkMap[trunks.Length + slot]];
            if (!trunk.Dirty)
                continue;
            Rebuild(trunk);
            voxels.SetData(staging, 0, slot * staging.Length, staging.Length);
            trunk.Dirty = false;
            updated++;
        }
        bool changed = updated > 0 || trunkMapDirty;
        if (trunkMapDirty)
        {
            mapBuffer.SetData(trunkMap);
            trunkMapDirty = false;
        }
        if (changed)
            LastUpdatedTrunks = updated;
        return changed;
    }

    private void UpdateSlots()
    {
        for (int i = 0; i < trunks.Length; i++)
        {
            Trunk trunk = trunks[i];
            if (!trunk.Dirty)
                continue;

            int slot = trunkMap[i];
            if (trunk.Primitives.Count > 0)
            {
                if (slot >= 0)
                    continue;
                slot = OccupiedTrunkCount++;
                trunkMap[i] = slot;
                trunkMap[trunks.Length + slot] = i;
                trunkMapDirty = true;
                continue;
            }

            trunk.Dirty = false;
            if (slot < 0)
                continue;

            // Keep the packed slots contiguous while relocating at most one other trunk.
            int lastSlot = --OccupiedTrunkCount;
            if (slot != lastSlot)
            {
                int moved = trunkMap[trunks.Length + lastSlot];
                trunkMap[moved] = slot;
                trunkMap[trunks.Length + slot] = moved;
                trunks[moved].Dirty = true;
            }
            trunkMap[i] = -1;
            trunkMapDirty = true;
        }
    }

    private void ResizeBuffers(ref ComputeBuffer mapBuffer, ref ComputeBuffer voxels, ref ComputeBuffer injection, bool cacheInjection)
    {
        int cellsPerTrunk = staging.Length;
        int oldCapacity = voxels != null ? voxels.count / cellsPerTrunk : 0;
        int capacity = oldCapacity;
        if (OccupiedTrunkCount > oldCapacity)
            capacity = Mathf.Max(OccupiedTrunkCount, Mathf.Min(trunks.Length, oldCapacity + oldCapacity / 4));
        else if (OccupiedTrunkCount <= oldCapacity / 2)
            capacity = OccupiedTrunkCount;

        // Bind a single dummy voxel when every trunk is empty.
        int voxelCount = Mathf.Max(1, capacity * cellsPerTrunk);
        ComputeBuffer newMap = null;
        ComputeBuffer newVoxels = null;
        ComputeBuffer newInjection = null;
        try
        {
            if (mapBuffer == null)
                newMap = new ComputeBuffer(trunkMap.Length, sizeof(int));
            if (voxels == null || voxels.count != voxelCount)
            {
                newVoxels = new ComputeBuffer(voxelCount, 4 * sizeof(float));
                if (cacheInjection)
                    newInjection = new ComputeBuffer(voxelCount * 6, 4 * sizeof(float));
            }
        }
        catch
        {
            newMap?.Release();
            newVoxels?.Release();
            newInjection?.Release();
            throw;
        }

        if (newMap != null)
        {
            mapBuffer = newMap;
            trunkMapDirty = true;
        }
        if (newVoxels != null)
        {
            voxels?.Release();
            if (cacheInjection)
            {
                injection?.Release();
                injection = newInjection;
            }
            voxels = newVoxels;
            for (int slot = 0; slot < OccupiedTrunkCount; slot++)
                trunks[trunkMap[trunks.Length + slot]].Dirty = true;
        }
    }

    private void Initialize(Vector3Int grid, Vector3 gridOrigin, Vector3 cellSpacing, int trunkSize)
    {
        if (staging == null || activeTrunkSize != trunkSize)
        {
            int cellsPerTrunk = trunkSize * trunkSize * trunkSize;
            staging = new Vector4[cellsPerTrunk];
            nearestDistance = new float[cellsPerTrunk];
        }
        activeTrunkSize = trunkSize;
        origin = gridOrigin;
        spacing = cellSpacing;
        trunkDimensions = grid / trunkSize;
        trunkExtent = spacing * trunkSize;
        trunks = new Trunk[trunkDimensions.x * trunkDimensions.y * trunkDimensions.z];
        trunkMap = new int[trunks.Length * 2];
        for (int i = 0; i < trunks.Length; i++)
            trunkMap[i] = -1;
        OccupiedTrunkCount = 0;
        trunkMapDirty = true;
        for (int z = 0; z < trunkDimensions.z; z++)
        for (int y = 0; y < trunkDimensions.y; y++)
        for (int x = 0; x < trunkDimensions.x; x++)
        {
            var coordinate = new Vector3Int(x, y, z);
            trunks[Index(coordinate)] = new Trunk { Coordinate = coordinate };
        }

        foreach (Primitive primitive in bakedInstances.Values)
            Reindex(primitive);
        foreach (Primitive primitive in runtimeInstances.Values)
            Reindex(primitive);

        void Reindex(Primitive primitive)
        {
            primitive.Trunks.Clear();
            IndexPrimitive(primitive);
        }
    }

    private void UpdatePrimitive(Primitive primitive)
    {
        Transform root = primitive.Root;
        if (root == null || root.gameObject.scene != gameObject.scene || primitive.Data == null || !primitive.Data.IsValid)
        {
            RemoveFromTrunks(primitive);
            primitive.HasBounds = false;
            primitive.Active = false;
            return;
        }

        Matrix4x4 matrix = root.localToWorldMatrix;
        bool active = root.gameObject.activeInHierarchy;
        if (primitive.HasBounds && primitive.LocalToWorld.Equals(matrix) && primitive.Revision == primitive.Data.Revision)
        {
            if (primitive.Active != active)
            {
                RemoveFromTrunks(primitive);
                primitive.Active = active;
                IndexPrimitive(primitive);
            }
            return;
        }

        RemoveFromTrunks(primitive);
        primitive.LocalToWorld = matrix;
        primitive.Revision = primitive.Data.Revision;
        primitive.HasBounds = Mathf.Abs(matrix.determinant) >= 1e-10f;
        primitive.Active = active;
        if (!primitive.HasBounds)
            return;
        primitive.WorldToLocal = root.worldToLocalMatrix;
        Matrix4x4 inverse = primitive.WorldToLocal;
        primitive.DistanceToWorld = 1f / Mathf.Max(RowLength(inverse, 0), RowLength(inverse, 1), RowLength(inverse, 2));
        primitive.WorldBounds = WorldBounds(matrix, primitive.Data.Bounds);
        IndexPrimitive(primitive);
    }

    private void IndexPrimitive(Primitive primitive)
    {
        if (trunks == null || !primitive.Active || !primitive.HasBounds)
            return;
        Vector3Int min = Vector3Int.Max(Vector3Int.zero, TrunkCoordinate(primitive.WorldBounds.min));
        Vector3Int max = Vector3Int.Min(trunkDimensions - Vector3Int.one, TrunkCoordinate(primitive.WorldBounds.max));
        for (int z = min.z; z <= max.z; z++)
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            Trunk trunk = trunks[Index(new Vector3Int(x, y, z))];
            trunk.Primitives.Add(primitive);
            trunk.Dirty = true;
            primitive.Trunks.Add(trunk);
        }
    }

    private static float RowLength(Matrix4x4 m, int row)
    {
        return new Vector3(m[row, 0], m[row, 1], m[row, 2]).magnitude;
    }

    private static void RemoveFromTrunks(Primitive primitive)
    {
        foreach (Trunk trunk in primitive.Trunks)
        {
            trunk.Primitives.Remove(primitive);
            trunk.Dirty = true;
        }
        primitive.Trunks.Clear();
    }

    public static Bounds WorldBounds(Matrix4x4 matrix, Bounds local)
    {
        Vector3 e = local.extents;
        Vector3 extent = new Vector3(
            Mathf.Abs(matrix.m00) * e.x + Mathf.Abs(matrix.m01) * e.y + Mathf.Abs(matrix.m02) * e.z,
            Mathf.Abs(matrix.m10) * e.x + Mathf.Abs(matrix.m11) * e.y + Mathf.Abs(matrix.m12) * e.z,
            Mathf.Abs(matrix.m20) * e.x + Mathf.Abs(matrix.m21) * e.y + Mathf.Abs(matrix.m22) * e.z);
        return new Bounds(matrix.MultiplyPoint3x4(local.center), extent * 2f);
    }

    private Vector3Int TrunkCoordinate(Vector3 position)
    {
        Vector3 p = position - origin;
        return Vector3Int.FloorToInt(new Vector3(p.x / trunkExtent.x, p.y / trunkExtent.y, p.z / trunkExtent.z));
    }

    private int Index(Vector3Int coordinate)
    {
        return coordinate.x + trunkDimensions.x * (coordinate.y + trunkDimensions.y * coordinate.z);
    }

    private void Rebuild(Trunk trunk)
    {
        Vector3 first = origin + Vector3.Scale(trunk.Coordinate, trunkExtent) + spacing * 0.5f;
        float shell = 0f;
        Array.Clear(staging, 0, staging.Length);
        for (int i = 0; i < nearestDistance.Length; i++)
            nearestDistance[i] = shell;

        Vector3 inverseSpacing = new Vector3(1f / spacing.x, 1f / spacing.y, 1f / spacing.z);
        var last = Vector3Int.one * (activeTrunkSize - 1);
        foreach (Primitive primitive in trunk.Primitives)
        {
            Vector3Int min = Vector3Int.Max(Vector3Int.zero,
                Vector3Int.CeilToInt(Vector3.Scale(primitive.WorldBounds.min - first, inverseSpacing)));
            Vector3Int max = Vector3Int.Min(last,
                Vector3Int.FloorToInt(Vector3.Scale(primitive.WorldBounds.max - first, inverseSpacing)));

            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                Vector3 world = first + Vector3.Scale(new Vector3(x, y, z), spacing);
                if (!primitive.Data.TrySample(primitive.WorldToLocal.MultiplyPoint3x4(world), out float distance, out Vector3 color))
                    continue;
                int index = x + activeTrunkSize * (y + activeTrunkSize * z);
                distance *= primitive.DistanceToWorld;
                if (distance > nearestDistance[index])
                    continue;
                nearestDistance[index] = distance;
                staging[index] = new Vector4(color.x, color.y, color.z, 1f);
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!showTrunks || trunks == null)
            return;
        Color previous = Gizmos.color;
        var controller = GetComponent<LocalRadianceTransferDemo>();
        bool mixed = controller != null && controller.UsesMixedProbeDensity;
        Color blue = new Color(0.1f, 0.9f, 1f, 0.65f);
        Color white = new Color(1f, 1f, 1f, 0.2f);
        for (int i = 0; i < trunks.Length; i++)
        {
            Trunk trunk = trunks[i];
            bool highlight = mixed ? controller.IsFineTrunk(i) : trunk.Primitives.Count > 0;
            Gizmos.color = highlight ? blue : white;
            Gizmos.DrawWireCube(origin + Vector3.Scale((Vector3)trunk.Coordinate + Vector3.one * 0.5f, trunkExtent), trunkExtent);
        }

        if (sceneBounds != null)
        {
            Matrix4x4 previousMatrix = Gizmos.matrix;
            DrawBox(sceneBounds, new Color(0.95f, 0.7f, 0.15f));
            foreach (BoxCollider region in gameplayRegions)
                if (region != null)
                    DrawBox(region, new Color(0.2f, 1f, 0.55f));
            Gizmos.matrix = previousMatrix;
        }
        Gizmos.color = previous;
    }

    private static void DrawBox(BoxCollider box, Color color)
    {
        Gizmos.color = color;
        Gizmos.matrix = box.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(box.center, box.size);
    }
}
