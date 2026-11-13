using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Light/LRT Light")]
public sealed class LrtLight : MonoBehaviour
{
    public enum LightShape { Point, Spot, Area, Directional }

    [SerializeField, Tooltip("Read settings and the enabled state from a supported Unity Light on this GameObject.")]
    private bool useUnityLight = true;
    [SerializeField] private LightShape shape;
    [SerializeField, ColorUsage(false, true)] private Color color = Color.white;
    [SerializeField, Min(0f)] private float intensity = 4f;
    [SerializeField, Min(0f)] private float range = 10f;
    [SerializeField, Range(1f, 179f)] private float spotAngle = 30f;
    [SerializeField, Range(0f, 179f)] private float innerSpotAngle = 21.8f;
    [SerializeField] private Vector2 size = Vector2.one;
    [SerializeField, Min(0f), Tooltip("Scales GI injection. When syncing, this multiplies the Unity Light's Bounce Intensity.")]
    private float indirectMultiplier = 1f;

    public Vector2 Size => size;
    public Color LightColor => color;
    public float Intensity => intensity;
    public float Range => range;
    public float IndirectMultiplier => indirectMultiplier;

    internal struct Settings
    {
        public LightShape Shape;
        public Color LinearColor;
        public float Intensity;
        public float Range;
        public float SpotAngle;
        public float InnerSpotAngle;
        public Vector2 Size;
        public float IndirectMultiplier;
    }

    public bool TryGetUnityLight(out Light light)
    {
        light = null;
        if (useUnityLight && TryGetComponent(out Light source) &&
            (source.type == LightType.Point || source.type == LightType.Spot ||
             source.type == LightType.Rectangle || source.type == LightType.Directional))
            light = source;
        return light != null;
    }

    internal bool TryGetInjectionSettings(out Settings settings)
    {
        settings = default;
        if (!isActiveAndEnabled)
            return false;

        settings = ReadSettings(out Light source);
        return (source == null || source.isActiveAndEnabled) &&
            settings.Intensity > 0f && settings.IndirectMultiplier > 0f &&
            (settings.Shape == LightShape.Directional || settings.Range > 0f);
    }

    private Settings ReadSettings(out Light source)
    {
        var settings = new Settings
        {
            Shape = shape,
            LinearColor = color.linear,
            Intensity = intensity,
            Range = range,
            SpotAngle = spotAngle,
            InnerSpotAngle = Mathf.Min(innerSpotAngle, spotAngle),
            Size = size,
            IndirectMultiplier = indirectMultiplier
        };

        if (!TryGetUnityLight(out source))
            return settings;

        settings.Shape = source.type switch
        {
            LightType.Spot => LightShape.Spot,
            LightType.Rectangle => LightShape.Area,
            LightType.Directional => LightShape.Directional,
            _ => LightShape.Point
        };
        settings.LinearColor = source.color.linear;
        if (source.useColorTemperature)
            settings.LinearColor *= Mathf.CorrelatedColorTemperatureToRGB(source.colorTemperature);
        settings.Intensity = source.intensity;
        settings.Range = source.range;
        settings.IndirectMultiplier *= source.bounceIntensity;
        if (settings.Shape == LightShape.Spot)
        {
            settings.SpotAngle = source.spotAngle;
            settings.InnerSpotAngle = Mathf.Min(source.innerSpotAngle, source.spotAngle);
        }
#if UNITY_EDITOR
        if (settings.Shape == LightShape.Area)
            settings.Size = Vector2.Max(source.areaSize, Vector2.one * 0.01f);
#endif
        return settings;
    }

    private void OnValidate()
    {
        size = Vector2.Max(size, Vector2.one * 0.01f);
    }

    private void OnDrawGizmosSelected()
    {
        Settings settings = ReadSettings(out _);
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Color previousColor = Gizmos.color;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.color = settings.LinearColor.gamma;
        switch (settings.Shape)
        {
            case LightShape.Point:
                Gizmos.DrawWireSphere(Vector3.zero, settings.Range);
                break;
            case LightShape.Spot:
                Gizmos.DrawFrustum(Vector3.zero, settings.SpotAngle, settings.Range, 0f, 1f);
                break;
            case LightShape.Area:
                Gizmos.DrawWireCube(Vector3.zero, new Vector3(settings.Size.x, settings.Size.y, 0f));
                Gizmos.DrawLine(Vector3.zero, Vector3.forward * Mathf.Min(settings.Range, 1f));
                break;
            case LightShape.Directional:
                Gizmos.DrawLine(Vector3.zero, Vector3.forward);
                break;
        }
        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }
}
