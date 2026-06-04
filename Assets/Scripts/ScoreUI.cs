using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
 
/// <summary>
/// Displays trick score, combo counter, banked total, and feedback popups.
/// </summary>
public class ScoreUI : MonoBehaviour
{
    // ─────────────────────────────────────────────────────── Inspector ──────
    [Header("References")]
    public TrickController  tricks;
 
    [Header("Text elements")]
    public TMP_Text totalScoreText;  
    public TMP_Text comboLabelText;   // "COMBO x3"
    public TMP_Text comboScoreText;   // running points in current combo
    public TMP_Text trickPopupText;   // "+225" per trick
    public TMP_Text feedbackText;     // "COMBO LOST!" / "BANKED!"
 
    [Header("Combo panel")]
    [Tooltip("CanvasGroup wrapping comboLabel + comboScore for fade in/out.")]
    public CanvasGroup comboPanel;
 
    [Header("Timing")]
    public float trickPopupDuration  = 1.1f;
    public float feedbackDuration    = 1.4f;
    public float comboFadeSpeed      = 6f;
 
    [Header("Colours")]
    public Color colourNormal   = new Color(1f,   1f,   1f,   1f);
    public Color colourCombo    = new Color(1f,   0.85f,0.2f, 1f);   // gold
    public Color colourLost     = new Color(1f,   0.25f,0.25f,1f);   // red
    public Color colourBanked   = new Color(0.3f, 1f,   0.5f, 1f);   // green
 
    // ─────────────────────────────────────────────────────── Private ─────────
    int        _totalScore;
    Coroutine  _popupCoroutine;
    Coroutine  _feedbackCoroutine;
 
    // ─────────────────────────────────────────────────────── Unity ──────────
    void Start()
    {
        // Subscribe to trick events
        if (tricks != null)
        {
            tricks.OnTrickCompleted += HandleTrickCompleted;
            tricks.OnComboLost      += HandleComboLost;
            tricks.OnComboFinished  += HandleComboFinished;
        }
 
        // Initial state
        if (comboPanel)    comboPanel.alpha    = 0f;
        if (trickPopupText) trickPopupText.alpha = 0f;
        if (feedbackText)   feedbackText.alpha   = 0f;
 
        RefreshTotalScore();
    }
 
    void OnDestroy()
    {
        if (tricks != null)
        {
            tricks.OnTrickCompleted -= HandleTrickCompleted;
            tricks.OnComboLost      -= HandleComboLost;
            tricks.OnComboFinished  -= HandleComboFinished;
        }
    }
 
    void Update()
    {
        // Keep combo panel visible and updated while tricks are running
        if (comboPanel == null) return;
 
        bool showCombo = tricks != null && tricks.IsTrickRunning;
        float targetAlpha = showCombo ? 1f : 0f;
        comboPanel.alpha = Mathf.MoveTowards(
            comboPanel.alpha, targetAlpha, comboFadeSpeed * Time.deltaTime);
 
        if (showCombo)
        {
            if (comboLabelText) comboLabelText.text = $"COMBO  x{tricks.ComboCount}";
            if (comboScoreText) comboScoreText.text  = tricks.ComboScore.ToString("N0");
        }
    }
 
    // ─────────────────────────────────────────────────────── Handlers ────────
    void HandleTrickCompleted(int combo, int gained)
    {
        // Flash "+score" popup
        ShowTrickPopup($"+{gained}", combo);
    }
 
    void HandleComboLost(int lostCombo)
    {
        ShowFeedback("COMBO LOST!", colourLost);
    }
 
    void HandleComboFinished(int banked)
    {
        _totalScore += banked;
        RefreshTotalScore();
        ShowFeedback($"+{banked:N0}  BANKED!", colourBanked);
    }
 
    // ─────────────────────────────────────────────────────── Popups ──────────
    void ShowTrickPopup(string message, int combo)
    {
        if (trickPopupText == null) return;
        if (_popupCoroutine != null) StopCoroutine(_popupCoroutine);
        _popupCoroutine = StartCoroutine(AnimateTrickPopup(message, combo));
    }
 
    IEnumerator AnimateTrickPopup(string message, int combo)
    {
        trickPopupText.text  = message;
        // Colour gets warmer and larger as combo grows
        float t = Mathf.InverseLerp(1, 10, combo);
        trickPopupText.color = Color.Lerp(colourNormal, colourCombo, t);
        trickPopupText.transform.localScale = Vector3.one * Mathf.Lerp(1f, 1.5f, t);
 
        // Fade in
        float elapsed = 0f;
        float half    = trickPopupDuration * 0.35f;
        while (elapsed < half)
        {
            elapsed += Time.deltaTime;
            trickPopupText.alpha = Mathf.Lerp(0f, 1f, elapsed / half);
            yield return null;
        }
 
        // Hold
        yield return new WaitForSeconds(trickPopupDuration * 0.3f);
 
        // Fade out
        elapsed = 0f;
        float fadeOut = trickPopupDuration * 0.35f;
        while (elapsed < fadeOut)
        {
            elapsed += Time.deltaTime;
            trickPopupText.alpha = Mathf.Lerp(1f, 0f, elapsed / fadeOut);
            yield return null;
        }
 
        trickPopupText.alpha = 0f;
        _popupCoroutine = null;
    }
 
    void ShowFeedback(string message, Color colour)
    {
        if (feedbackText == null) return;
        if (_feedbackCoroutine != null) StopCoroutine(_feedbackCoroutine);
        _feedbackCoroutine = StartCoroutine(AnimateFeedback(message, colour));
    }
 
    IEnumerator AnimateFeedback(string message, Color colour)
    {
        feedbackText.text  = message;
        feedbackText.color = colour;
 
        // Punch scale
        feedbackText.transform.localScale = Vector3.one * 1.3f;
 
        float elapsed = 0f;
        float fadeIn  = 0.12f;
        while (elapsed < fadeIn)
        {
            elapsed += Time.deltaTime;
            feedbackText.alpha = Mathf.Lerp(0f, 1f, elapsed / fadeIn);
            feedbackText.transform.localScale = Vector3.Lerp(
                Vector3.one * 1.3f, Vector3.one, elapsed / fadeIn);
            yield return null;
        }
 
        feedbackText.alpha = 1f;
        feedbackText.transform.localScale = Vector3.one;
 
        // Hold
        yield return new WaitForSeconds(feedbackDuration * 0.6f);
 
        // Fade out
        elapsed = 0f;
        float fadeOut = feedbackDuration * 0.4f;
        while (elapsed < fadeOut)
        {
            elapsed += Time.deltaTime;
            feedbackText.alpha = Mathf.Lerp(1f, 0f, elapsed / fadeOut);
            yield return null;
        }
 
        feedbackText.alpha = 0f;
        _feedbackCoroutine = null;
    }
 
    // ─────────────────────────────────────────────────────── Helpers ─────────
    void RefreshTotalScore()
    {
        if (totalScoreText) totalScoreText.text = _totalScore.ToString("N0");
    }
}
 