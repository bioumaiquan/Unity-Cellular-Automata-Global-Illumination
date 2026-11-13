using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class LrtDemoHud : MonoBehaviour
{
    private const int CappedFrameRate = 60;
    private const float FpsSampleInterval = 0.5f;
    private const float StatusRefreshInterval = 0.1f;
    private static readonly Color InactiveViewColor = new Color(0.19f, 0.23f, 0.28f);
    private static readonly Color ActiveViewColor = new Color(0.17f, 0.40f, 0.54f);

    [SerializeField] private LocalRadianceTransferDemo demo;
    [SerializeField] private DemoDayNightCycle dayNight;
    [SerializeField] private RectTransform debugPanel;
    [SerializeField] private GameObject probeControls;
    [SerializeField] private GameObject gridUnavailable;
    [SerializeField] private Text gridTitle;
    [SerializeField] private Button[] viewButtons;
    [SerializeField] private Text trunkStatus;
    [SerializeField] private Button buildingButton;
    [SerializeField] private Text buildingLabel;
    [SerializeField] private Button lampButton;
    [SerializeField] private Text lampLabel;
    [SerializeField] private Text propagationSettings;
    [SerializeField] private Toggle showProbes;
    [SerializeField] private Text propagationStatus;
    [SerializeField] private Text skyVisibilityStatus;
    [SerializeField] private Text fpsLabel;
    [SerializeField] private Button dayNightButton;
    [SerializeField] private Text dayNightLabel;

    private float fpsSampleTime;
    private int fpsSampleFrames;
    private float nextStatusRefresh;
    private bool hadProbeGrid;

    private void Awake()
    {
        for (int i = 0; i < viewButtons.Length; i++)
        {
            int viewIndex = i;
            viewButtons[i].onClick.AddListener(() => demo.DebugView = viewIndex);
        }

        buildingButton.onClick.AddListener(demo.ToggleDemoProp);
        lampButton.onClick.AddListener(demo.ToggleDemoLight);
        showProbes.onValueChanged.AddListener(value => demo.ShowProbes = value);
        dayNightButton.onClick.AddListener(dayNight.ToggleTimeOfDay);
    }

    private void Start()
    {
        ApplyFrameRate(false);
        CreateFrameRateToggle();
    }

    private void CreateFrameRateToggle()
    {
        var fpsPanel = (RectTransform)fpsLabel.transform.parent;
        const float toggleWidth = 108f;
        fpsPanel.sizeDelta += Vector2.right * toggleWidth;
        fpsPanel.anchoredPosition -= Vector2.right * (toggleWidth * (1f - fpsPanel.pivot.x));

        // Use the same checkbox visuals and touch target as the probe control.
        var toggle = Instantiate(showProbes, fpsPanel, false);
        toggle.name = "Uncap FPS";
        toggle.group = null;
        toggle.SetIsOnWithoutNotify(false);
        toggle.onValueChanged.AddListener(ApplyFrameRate);

        var toggleRect = (RectTransform)toggle.transform;
        toggleRect.anchoredPosition = Vector2.right * fpsLabel.rectTransform.rect.width;
        toggleRect.sizeDelta = new Vector2(toggleWidth, fpsPanel.rect.height);
        var label = toggle.GetComponentInChildren<Text>(true);
        label.text = "Uncap FPS";
        label.rectTransform.sizeDelta = new Vector2(toggleWidth - label.rectTransform.anchoredPosition.x,
            fpsPanel.rect.height);

        var checkbox = toggle.targetGraphic.rectTransform;
        checkbox.anchoredPosition = new Vector2(checkbox.anchoredPosition.x,
            (checkbox.rect.height - fpsPanel.rect.height) * 0.5f);
    }

    private static void ApplyFrameRate(bool uncapped)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        int refreshRate = Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
        Application.targetFrameRate = uncapped && refreshRate > 0 ? 1000 : CappedFrameRate;
#else
        Application.targetFrameRate = uncapped ? -1 : CappedFrameRate;
#endif
    }

    private void LateUpdate()
    {
        debugPanel.gameObject.SetActive(demo.isActiveAndEnabled);
        if (demo.isActiveAndEnabled)
            RefreshDebugPanel();

        dayNightButton.gameObject.SetActive(dayNight.isActiveAndEnabled);
        dayNightLabel.text = dayNight.TargetIsNight ? "Switch to Day" : "Switch to Night";
        SampleFrameRate();
    }

    private void RefreshDebugPanel()
    {
        bool hasProbeGrid = demo.HasProbeGrid;
        if (probeControls.activeSelf != hasProbeGrid)
        {
            probeControls.SetActive(hasProbeGrid);
            gridUnavailable.SetActive(!hasProbeGrid);
            debugPanel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                hasProbeGrid ? ((RectTransform)probeControls.transform).rect.height : 64f);
        }

        if (hasProbeGrid)
        {
            for (int i = 0; i < viewButtons.Length; i++)
                viewButtons[i].targetGraphic.color = i == demo.DebugView ? ActiveViewColor : InactiveViewColor;

            buildingButton.gameObject.SetActive(demo.HasDemoProp);
            buildingLabel.text = demo.IsDemoPropVisible ? "Hide building" : "Show building";
            lampButton.gameObject.SetActive(demo.HasDemoLight);
            lampLabel.text = demo.IsDemoLightOn ? "Turn lamp off" : "Turn lamp on";
            showProbes.SetIsOnWithoutNotify(demo.ShowProbes);
        }

        if (hadProbeGrid == hasProbeGrid && Time.unscaledTime < nextStatusRefresh)
            return;

        hadProbeGrid = hasProbeGrid;
        nextStatusRefresh = Time.unscaledTime + StatusRefreshInterval;
        gridTitle.text = demo.ProbeGridTitle;
        if (hasProbeGrid)
        {
            trunkStatus.text = demo.TrunkStatus;
            propagationSettings.text = demo.PropagationSettings;
            propagationStatus.text = demo.PropagationStatus;
            skyVisibilityStatus.text = demo.SkyVisibilityStatus;
        }
    }

    private void SampleFrameRate()
    {
        fpsSampleTime += Time.unscaledDeltaTime;
        fpsSampleFrames++;
        if (fpsSampleTime < FpsSampleInterval)
            return;

        fpsLabel.text = $"FPS: {fpsSampleFrames / fpsSampleTime:F1}";
        fpsSampleTime = 0f;
        fpsSampleFrames = 0;
    }
}
