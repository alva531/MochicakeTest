using UnityEngine;
using System.Collections;
using System;

/// <summary>
/// Swipe mid-drag to start a trick. Hold to keep spinning.
/// On release, the current 360° cycle completes before returning to rest.
/// Trick direction is locked for the entire press — can only change after release.
/// Landing mid-spin loses the whole combo.
/// The mesh snaps back to rest as soon as the sled touches the ground.
/// </summary>
public class TrickController : MonoBehaviour
{
    // ─────────────────────────────────────────────────────── Inspector ──────
    [Header("References")]
    public SledController sled;
    [Tooltip("Child Transform that holds the visible mesh. Only this rotates.")]
    public Transform meshRoot;

    [Header("Swipe detection")]
    [Tooltip("Minimum drag distance in pixels before the trick fires.")]
    public float swipeThreshold   = 80f;
    [Tooltip("Max seconds from press to threshold crossing. 0 = no limit.")]
    public float swipeMaxDuration = 0.4f;

    [Header("Trick animation")]
    [Tooltip("Seconds to complete one full 360° spin.")]
    public float trickDuration       = 0.55f;
    [Tooltip("Seconds to smoothly return to rest pose on landing.")]
    public float landingSnapDuration = 0.2f;

    [Header("Scoring")]
    public int   baseScore       = 100;
    [Tooltip("Score multiplier per trick in the same combo chain.")]
    public float comboMultiplier = 1.5f;

    // ─────────────────────────────────────────────────────── Events ─────────
    /// <summary>Fired every time a full 360° completes. (comboIndex, scoreGained)</summary>
    public event Action<int, int> OnTrickCompleted;
    /// <summary>Fired on mid-trick landing. (comboCountLost)</summary>
    public event Action<int>      OnComboLost;
    /// <summary>Fired on clean landing with pending score. (totalScoreBanked)</summary>
    public event Action<int>      OnComboFinished;

    // ─────────────────────────────────────────────────────── Public ─────────
    public bool IsTrickRunning => _spinActive;
    public int  ComboCount     => _comboCount;
    public int  ComboScore     => _comboScore;

    // ─────────────────────────────────────────────────────── Private ─────────
    enum SwipeDir
    {
        None,
        Up, Down, Left, Right,
        UpRight, UpLeft, DownRight, DownLeft
    }

    // Input
    Vector2 _pressPos;
    float   _pressTime;
    bool    _pointerDown;
    bool    _gestureConsumed;

    // Trick state
    Coroutine _trickCoroutine;
    Coroutine _snapCoroutine;
    bool      _spinActive;

    // Combo
    int _comboCount;
    int _comboScore;

    // Rest pose captured on Start
    Quaternion _restRotation;

    // ─────────────────────────────────────────────────────── Unity ──────────
    void Reset() => sled = GetComponent<SledController>();

    void Start()
    {
        _restRotation = meshRoot.localRotation;
    }

    void Update()
    {
        ReadInput();
    }

    // ─────────────────────────────────────────────────────── Input ──────────
    void ReadInput()
    {
        bool pressed  = Input.GetMouseButtonDown(0);
        bool released = Input.GetMouseButtonUp(0);

#if UNITY_IOS || UNITY_ANDROID
        if (Input.touchCount > 0)
        {
            Touch t  = Input.GetTouch(0);
            pressed  = t.phase == TouchPhase.Began;
            released = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
        }
#endif

        if (pressed)
        {
            _pressPos        = Input.mousePosition;
            _pressTime       = Time.time;
            _pointerDown     = true;
            _gestureConsumed = false;
        }

        // Mid-drag detection — fires once per press as soon as threshold is crossed.
        // _trickCoroutine check locks the direction: while a trick is running no new
        // direction can be registered until the pointer is released and the spin ends.
        if (_pointerDown && !_gestureConsumed && _trickCoroutine == null)
        {
            float elapsed = Time.time - _pressTime;

            if (swipeMaxDuration > 0f && elapsed > swipeMaxDuration)
            {
                _gestureConsumed = true; // too slow, discard
            }
            else
            {
                Vector2 drag = (Vector2)Input.mousePosition - _pressPos;
                if (drag.magnitude >= swipeThreshold)
                {
                    _gestureConsumed = true;
                    TryStartTrick(drag);
                }
            }
        }

        if (released)
        {
            _pointerDown     = false;
            _gestureConsumed = false;
            // SpinLoop's while loop exits next frame — Phase 2 (completion) begins
        }
    }

    void TryStartTrick(Vector2 drag)
    {
        if (sled.CurrentState != SledController.State.Airborne) return;

        SwipeDir dir = Classify(drag);
        if (dir == SwipeDir.None) return;

        _trickCoroutine = StartCoroutine(SpinLoop(dir));
    }

    // ─────────────────────────────────────────────────────── Spin loop ───────
    IEnumerator SpinLoop(SwipeDir dir)
    {
        _spinActive = true;

        Vector3    axis        = AxisFor(dir);
        Quaternion startRot    = meshRoot.localRotation;
        float      anglePerSec = 360f / trickDuration;
        float      totalAngle  = 0f;
        int        spinsGiven  = 0;

        // ── Phase 1: spin continuously while holding AND airborne ────────────
        while (_pointerDown && sled.CurrentState == SledController.State.Airborne)
        {
            totalAngle += anglePerSec * Time.deltaTime;
            meshRoot.localRotation = startRot * Quaternion.AngleAxis(totalAngle, axis);
            AwardSpins(ref spinsGiven, totalAngle);
            yield return null;
        }

        // ── Did we land during Phase 1? ───────────────────────────────────────
        if (sled.CurrentState != SledController.State.Airborne)
        {
            StartSnapToRest();    // begin rotating back immediately
            _trickCoroutine = null;
            // _spinActive stays true — OnLanded() handles combo penalty
            yield break;
        }

        // ── Phase 2: pointer released — finish the current 360° cycle ────────
        float remainder    = totalAngle % 360f;
        float nextBoundary = totalAngle + (360f - remainder);

        if (remainder > 0.5f) // skip if already essentially at a boundary
        {
            while (totalAngle < nextBoundary)
            {
                // Land during completion swing?
                if (sled.CurrentState != SledController.State.Airborne)
                {
                    StartSnapToRest();    // begin rotating back immediately
                    _trickCoroutine = null;
                    // _spinActive stays true — OnLanded() handles combo penalty
                    yield break;
                }

                totalAngle += anglePerSec * Time.deltaTime;
                totalAngle  = Mathf.Min(totalAngle, nextBoundary); // don't overshoot
                meshRoot.localRotation = startRot * Quaternion.AngleAxis(totalAngle, axis);
                AwardSpins(ref spinsGiven, totalAngle);
                yield return null;
            }
        }

        // ── Clean finish: smoothly back to rest pose ──────────────────────────
        StartSnapToRest();
        _spinActive     = false;
        _trickCoroutine = null;
        // Combo stays alive — banked when the sled lands
    }

    // ─────────────────────────────────────────────────────── Landing ─────────

    /// <summary>Called by SledController.CompleteReattach().</summary>
    public void OnLanded()
    {
        if (_spinActive)
        {
            if (_trickCoroutine != null)
            {
                StopCoroutine(_trickCoroutine);
                _trickCoroutine = null;
            }

            _spinActive  = false;
            _pointerDown = false;

            // Snap may already be running (started in SpinLoop on landing detection).
            // Only start it here if it isn't — avoids restarting from a further-along rotation.
            if (_snapCoroutine == null)
                StartSnapToRest();

            int lostCombo = _comboCount;
            _comboCount   = 0;
            _comboScore   = 0;

            if (lostCombo > 0)
            {
                OnComboLost?.Invoke(lostCombo);
                Debug.Log($"[Tricks] Mid-trick landing — combo x{lostCombo} lost.");
            }
        }
        else if (_comboCount > 0)
        {
            int banked  = _comboScore;
            int combo   = _comboCount;
            _comboCount = 0;
            _comboScore = 0;

            OnComboFinished?.Invoke(banked);
            Debug.Log($"[Tricks] Clean landing — combo x{combo} = {banked} pts banked.");
        }
    }

    // ─────────────────────────────────────────────────────── Snap helpers ────

    void StartSnapToRest()
    {
        if (_snapCoroutine != null) StopCoroutine(_snapCoroutine);
        _snapCoroutine = StartCoroutine(SnapToRest(landingSnapDuration));
    }

    IEnumerator SnapToRest(float duration)
    {
        Quaternion from    = meshRoot.localRotation;
        float      elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            meshRoot.localRotation = Quaternion.Slerp(
                from, _restRotation, elapsed / duration);
            yield return null;
        }

        meshRoot.localRotation = _restRotation;
        _snapCoroutine = null;
    }

    // ─────────────────────────────────────────────────────── Score helpers ───

    void AwardSpins(ref int spinsGiven, float totalAngle)
    {
        int fullSpins = Mathf.FloorToInt(totalAngle / 360f);
        while (spinsGiven < fullSpins)
        {
            spinsGiven++;
            _comboCount++;
            int gained = Mathf.RoundToInt(
                baseScore * Mathf.Pow(comboMultiplier, _comboCount - 1));
            _comboScore += gained;
            OnTrickCompleted?.Invoke(_comboCount, gained);
        }
    }

    // ─────────────────────────────────────────────────────── Classify ────────

    static SwipeDir Classify(Vector2 d)
    {
        if (d.magnitude < 0.001f) return SwipeDir.None;

        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;

        int sector = Mathf.RoundToInt(angle / 45f) % 8;
        return sector switch
        {
            0 => SwipeDir.Right,
            1 => SwipeDir.UpRight,
            2 => SwipeDir.Up,
            3 => SwipeDir.UpLeft,
            4 => SwipeDir.Left,
            5 => SwipeDir.DownLeft,
            6 => SwipeDir.Down,
            7 => SwipeDir.DownRight,
            _ => SwipeDir.None
        };
    }

    static Vector3 AxisFor(SwipeDir dir)
    {
        Vector3 flip  =  Vector3.right;
        Vector3 flop  = -Vector3.right;
        Vector3 spinR =  Vector3.up;
        Vector3 spinL = -Vector3.up;

        return dir switch
        {
            SwipeDir.Up        => flop,
            SwipeDir.Down      => flip,
            SwipeDir.Right     => spinR,
            SwipeDir.Left      => spinL,
            SwipeDir.UpRight   => (flop + spinR).normalized,
            SwipeDir.UpLeft    => (flop + spinL).normalized,
            SwipeDir.DownRight => (flip + spinR).normalized,
            SwipeDir.DownLeft  => (flip + spinL).normalized,
            _                  => Vector3.up
        };
    }
}
