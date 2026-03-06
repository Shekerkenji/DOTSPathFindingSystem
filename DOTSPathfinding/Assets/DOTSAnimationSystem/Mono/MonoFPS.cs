using UnityEngine;
using TMPro;

public class MonoFPS : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI fpsText;
    [SerializeField] private TextMeshProUGUI animatorText;

    [Header("Refresh")]
    [SerializeField] private float refreshRate = 0.5f;

    private float timer;

    private void Update()
    {
        timer += Time.unscaledDeltaTime;

        if (timer >= refreshRate)
        {
            UpdateStats();
            timer = 0f;
        }
    }

    private void UpdateStats()
    {
        // FPS
        float fps = 1f / Time.unscaledDeltaTime;
        fpsText.text = $"FPS: {Mathf.RoundToInt(fps)}";

        // Animator count (modern + faster)
        int animatorCount = Object.FindObjectsByType<Animator>(FindObjectsSortMode.None).Length;
        animatorText.text = $"Animators: {animatorCount}";
    }
}