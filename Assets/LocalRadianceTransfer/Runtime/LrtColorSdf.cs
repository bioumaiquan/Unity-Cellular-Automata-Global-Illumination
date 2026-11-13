using UnityEngine;

// One root-local, quantized distance and diffuse RGB sample per voxel.
[PreferBinarySerialization]
public sealed class LrtColorSdf : ScriptableObject
{
    [SerializeField] private Bounds bounds;
    [SerializeField] private Vector3Int dimensions;
    [SerializeField] private float distanceScale;
    [SerializeField] private byte[] samples;
    [SerializeField] private int revision;

    public Bounds Bounds => bounds;
    public int Revision => revision;
    public bool IsValid => samples != null && samples.Length == 4 * dimensions.x * dimensions.y * dimensions.z
                           && dimensions.x >= 2 && dimensions.y >= 2 && dimensions.z >= 2 && distanceScale > 0f;

#if UNITY_EDITOR
    public void SetBake(Bounds localBounds, Vector3Int size, float scale, byte[] data)
    {
        bounds = localBounds;
        dimensions = size;
        distanceScale = scale;
        samples = data;
        revision++;
    }

    public void ReplaceBake(LrtColorSdf source)
    {
        SetBake(source.bounds, source.dimensions, source.distanceScale, source.samples);
    }
#endif

    public bool TrySample(Vector3 local, out float distance, out Vector3 color)
    {
        distance = float.PositiveInfinity;
        color = Vector3.zero;
        if (!bounds.Contains(local))
            return false;

        Vector3 offset = local - bounds.min;
        Vector3 size = bounds.size;
        Vector3 grid = new Vector3(offset.x * dimensions.x / size.x, offset.y * dimensions.y / size.y,
            offset.z * dimensions.z / size.z) - Vector3.one * 0.5f;
        int x = Mathf.Clamp(Mathf.FloorToInt(grid.x), 0, dimensions.x - 2);
        int y = Mathf.Clamp(Mathf.FloorToInt(grid.y), 0, dimensions.y - 2);
        int z = Mathf.Clamp(Mathf.FloorToInt(grid.z), 0, dimensions.z - 2);
        float tx = Mathf.Clamp01(grid.x - x);
        float ty = Mathf.Clamp01(grid.y - y);
        float tz = Mathf.Clamp01(grid.z - z);
        int strideY = dimensions.x * 4;
        int strideZ = dimensions.y * strideY;
        int index = x * 4 + y * strideY + z * strideZ;
        float near = Mathf.Lerp(
            Mathf.Lerp(samples[index], samples[index + 4], tx),
            Mathf.Lerp(samples[index + strideY], samples[index + strideY + 4], tx), ty);
        int farIndex = index + strideZ;
        float far = Mathf.Lerp(
            Mathf.Lerp(samples[farIndex], samples[farIndex + 4], tx),
            Mathf.Lerp(samples[farIndex + strideY], samples[farIndex + strideY + 4], tx), ty);
        distance = (Mathf.Lerp(near, far, tz) - 128f) * distanceScale;

        int nearest = index + (tx >= 0.5f ? 4 : 0) + (ty >= 0.5f ? strideY : 0) + (tz >= 0.5f ? strideZ : 0);
        color = new Vector3(samples[nearest + 1], samples[nearest + 2], samples[nearest + 3]) * (1f / 255f);
        return true;
    }
}
