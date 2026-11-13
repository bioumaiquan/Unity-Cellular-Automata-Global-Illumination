using System;
using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(-100)]
[DisallowMultipleComponent]
public sealed class DemoDayNightCycle : MonoBehaviour
{
    [Serializable]
    private struct Lighting
    {
        public Color mainLightColor;
        [Min(0f)] public float mainLightIntensity;
        public Color skyTint;
        [Min(0f)] public float skyExposure;
        [Min(0f), Tooltip("Lighting > Environment Lighting > Intensity Multiplier.")]
        public float ambientIntensity;
    }

    [SerializeField] private Light mainLight;
    [SerializeField, Tooltip("Lamps turn on in list order at dusk and turn off in reverse order at dawn.")]
    private GameObject[] pointLights = Array.Empty<GameObject>();
    [SerializeField] private Lighting day = new Lighting
    {
        mainLightColor = new Color(1f, 0.95f, 0.85f),
        mainLightIntensity = 3f,
        skyTint = new Color(0.5f, 0.5f, 0.5f),
        skyExposure = 1f,
        ambientIntensity = 1f
    };
    [SerializeField] private Lighting night = new Lighting
    {
        mainLightColor = new Color(0.23948011f, 0.46956837f, 0.81886786f),
        mainLightIntensity = 0.65f,
        skyTint = new Color(0.16234244f, 0.28967643f, 0.5698112f),
        skyExposure = 0.4f,
        ambientIntensity = 1f
    };
    [SerializeField, Min(0f), Tooltip("Seconds for a full day-to-night or night-to-day transition.")]
    private float transitionDuration = 4f;
    [SerializeField] private bool startAtNight;

    private static readonly int SkyTint = Shader.PropertyToID("_Tint");
    private static readonly int SkyExposure = Shader.PropertyToID("_Exposure");
    private const float EnvironmentRefreshInterval = 0.25f;

    private Material originalSkybox;
    private Material runtimeSkybox;
    private float originalAmbientIntensity;
    private bool hasSkyTint;
    private bool hasSkyExposure;
    private bool targetIsNight;
    private float nightAmount;
    private bool environmentDirty;
    private double nextEnvironmentRefresh;

    internal bool TargetIsNight => targetIsNight;

    internal void ToggleTimeOfDay() => targetIsNight = !targetIsNight;

    private void Awake()
    {
        originalAmbientIntensity = RenderSettings.ambientIntensity;
        originalSkybox = RenderSettings.skybox;
        if (originalSkybox != null)
        {
            runtimeSkybox = new Material(originalSkybox);
            hasSkyTint = runtimeSkybox.HasProperty(SkyTint);
            hasSkyExposure = runtimeSkybox.HasProperty(SkyExposure);
            RenderSettings.skybox = runtimeSkybox;
        }

        targetIsNight = startAtNight;
        nightAmount = targetIsNight ? 1f : 0f;
        ApplyLighting();
    }

    private void Update()
    {
        float target = targetIsNight ? 1f : 0f;
        if (nightAmount != target)
        {
            nightAmount = transitionDuration > 0f
                ? Mathf.MoveTowards(nightAmount, target, Time.unscaledDeltaTime / transitionDuration)
                : target;
            ApplyLighting();
        }

        double now = Time.unscaledTimeAsDouble;
        if (environmentDirty && now >= nextEnvironmentRefresh)
        {
            DynamicGI.UpdateEnvironment();
            environmentDirty = false;
            nextEnvironmentRefresh = now + EnvironmentRefreshInterval;
        }
    }

    private void ApplyLighting()
    {
        float blend = Mathf.SmoothStep(0f, 1f, nightAmount);
        if (mainLight != null)
        {
            mainLight.color = Color.Lerp(day.mainLightColor, night.mainLightColor, blend);
            mainLight.intensity = Mathf.Lerp(day.mainLightIntensity, night.mainLightIntensity, blend);
        }

        float ambientIntensity = Mathf.Lerp(day.ambientIntensity, night.ambientIntensity, blend);
        bool ambientIntensityChanged = RenderSettings.ambientIntensity != ambientIntensity;
        if (ambientIntensityChanged)
            RenderSettings.ambientIntensity = ambientIntensity;

        if (runtimeSkybox != null)
        {
            if (hasSkyTint)
                runtimeSkybox.SetColor(SkyTint, Color.Lerp(day.skyTint, night.skyTint, blend));
            if (hasSkyExposure)
                runtimeSkybox.SetFloat(SkyExposure, Mathf.Lerp(day.skyExposure, night.skyExposure, blend));
        }

        bool skyboxChanged = runtimeSkybox != null && (hasSkyTint || hasSkyExposure);
        if ((ambientIntensityChanged || skyboxChanged) && RenderSettings.ambientMode == AmbientMode.Skybox)
            environmentDirty = true;

        for (int i = 0; i < pointLights.Length; i++)
        {
            GameObject lamp = pointLights[i];
            bool lampOn = nightAmount >= (i + 1f) / (pointLights.Length + 1f);
            if (lamp != null && lamp.activeSelf != lampOn)
                lamp.SetActive(lampOn);
        }
    }

    private void OnDestroy()
    {
        RenderSettings.ambientIntensity = originalAmbientIntensity;
        if (runtimeSkybox != null)
        {
            if (RenderSettings.skybox == runtimeSkybox)
                RenderSettings.skybox = originalSkybox;
            Destroy(runtimeSkybox);
        }
    }
}
