using UnityEngine;
using TMPro;

/// <summary>
/// Displays real-time FPS and frame time (ms) using two assignable UGUI Text components.
/// Attach to any GameObject in your scene and assign the text fields in the Inspector.
/// </summary>
public class FPSCounter : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("Text component that will display the FPS value.")]
    [SerializeField] private TMP_Text fpsText;

    [Tooltip("Text component that will display the frame time in milliseconds.")]
    [SerializeField] private TMP_Text msText;

    [Header("Settings")]
    [Tooltip("How often (in seconds) the display updates.")]
    [SerializeField] private float updateInterval = 0.1f;

    [Tooltip("Color thresholds for FPS coloring (optional visual feedback).")]
    [SerializeField] private bool colorCodeOutput = true;
    [SerializeField] private int goodFpsThreshold = 60;
    [SerializeField] private int okFpsThreshold = 30;

    // ── Runtime state ────────────────────────────────────────────────────────
    private float _timer;
    private int _frameCount;
    private float _accumulatedTime;

    // Cached color strings to avoid per-frame string allocation
    private static readonly string ColorGood = "#00FF88";
    private static readonly string ColorOk = "#FFD000";
    private static readonly string ColorBad = "#FF4444";

    // ── Unity lifecycle ───────────────────────────────────────────────────────
    private void Awake()
    {
        if (fpsText == null || msText == null)
        {
            Debug.LogWarning("[FPSCounter] One or both Text references are not assigned in the Inspector.", this);
        }
    }

    void Start()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 300;
    }
    private void Update()
    {
        float deltaTime = Time.unscaledDeltaTime;

        _accumulatedTime += deltaTime;
        _frameCount++;
        _timer += deltaTime;

        if (_timer >= updateInterval)
        {
            float avgMs = (_accumulatedTime / _frameCount) * 1000f;
            float avgFps = _frameCount / _accumulatedTime;

            UpdateDisplay(avgFps, avgMs);

            // Reset accumulators
            _timer = 0f;
            _frameCount = 0;
            _accumulatedTime = 0f;
        }
    }

    // ── Display helpers ───────────────────────────────────────────────────────
    private void UpdateDisplay(float fps, float ms)
    {
        if (colorCodeOutput)
        {
            string color = fps >= goodFpsThreshold ? ColorGood
                         : fps >= okFpsThreshold ? ColorOk
                                                   : ColorBad;

            if (fpsText != null)
                fpsText.text = $"<color={color}>{fps:F0} FPS</color>";

            if (msText != null)
                msText.text = $"<color={color}>{ms:F2} ms</color>";
        }
        else
        {
            if (fpsText != null)
                fpsText.text = $"{fps:F0} FPS";

            if (msText != null)
                msText.text = $"{ms:F2} ms";
        }
    }
}