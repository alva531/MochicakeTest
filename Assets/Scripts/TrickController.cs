using UnityEngine;
using System.Collections;
using System;

/// <summary>
/// Detects the swipe direction mid-drag (as soon as the threshold is crossed),
/// starts spinning immediately, and keeps spinning as long as the pointer is held.
/// 
/// Each completed 360° awards score. Landing mid-spin loses the whole combo.
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
    public float swipeThreshold = 80f;
    [Tooltip("Max seconds from press to threshold crossing. 0 = no limit.")]
    public float swipeMaxDuration = 0.4f;

    [Header("Trick animation")]
    [Tooltip("Seconds to complete one full 360° spin.")]
    public float trickDuration = 0.55f;

    [Header("Scoring")]
    public int baseScore = 100;
    [Tooltip("Score multiplier per trick in the same combo chain.")]
    public float comboMultiplier = 1.5f;

    // ─────────────────────────────────────────────────────── Events ─────────
    /// <summary>Fired every time a full 360° completes. (combo index, score gained)</summary>
    public event Action<int, int> OnTrickCompleted;
    /// <summary>Fired on mid-trick landing. (combo count lost)</summary>
    public event Action<int> OnComboLost;
    /// <summary>Fired on clean landing with pending score. (total score banked)</summary>
    public event Action<int> OnComboFinished;

    // ─────────────────────────────────────────────────────── Public ─────────
    public bool IsTrickRunning => _spinActive;
    public int ComboCount => _comboCount;
    public int ComboScore => _comboScore;

    // ─────────────────────────────────────────────────────── Private ─────────
    enum SwipeDir
    {
        None,
        Up, Down, Left, Right,          // cardinals
        UpRight, UpLeft, DownRight, DownLeft  // diagonals
    }

    // Input tracking
    Vector2 _pressPos;
    float _pressTime;
    bool _pointerDown;
    bool _gestureConsumed;   // true once a swipe fires, prevents re-trigger same press

    // Trick state
    Coroutine _trickCoroutine;
    bool _spinActive;      // true while coroutine is running

    // Combo
    int _comboCount;
    int _comboScore;


    Quaternion _restRotation;

    // ─────────────────────────────────────────────────────── Unity ──────────

    void Start()
    {
        _restRotation = meshRoot.localRotation;
    }

    void Reset() => sled = GetComponent<SledController>();

    void Update()
    {
        ReadInput();
    }

    // ─────────────────────────────────────────────────────── Input ──────────
    void ReadInput()
    {
        bool pressed = Input.GetMouseButtonDown(0);
        bool released = Input.GetMouseButtonUp(0);

#if UNITY_IOS || UNITY_ANDROID
        if (Input.touchCount > 0)
        {
            Touch t   = Input.GetTouch(0);
            pressed   = t.phase == TouchPhase.Began;
            released  = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
        }
#endif

        if (pressed)
        {
            _pressPos = Input.mousePosition;
            _pressTime = Time.time;
            _pointerDown = true;
            _gestureConsumed = false;
        }

        // ── Mid-drag detection: fire as soon as threshold is crossed ─────────
        if (_pointerDown && !_gestureConsumed && _trickCoroutine == null)
        {
            float elapsed = Time.time - _pressTime;

            // Expired — consume so it never triggers
            if (swipeMaxDuration > 0f && elapsed > swipeMaxDuration)
            {
                _gestureConsumed = true;
            }
            else
            {
                Vector2 delta = (Vector2)Input.mousePosition - _pressPos;
                if (delta.magnitude >= swipeThreshold)
                {
                    _gestureConsumed = true;
                    TryStartTrick(delta);
                }
            }
        }

        if (released)
        {
            _pointerDown = false;
            _gestureConsumed = false;
            // The coroutine's while loop will exit next frame because _pointerDown is false
        }
    }

    void TryStartTrick(Vector2 delta)
    {
        if (sled.CurrentState != SledController.State.Airborne) return;

        SwipeDir dir = Classify(delta);
        if (dir == SwipeDir.None) return;

        _trickCoroutine = StartCoroutine(SpinLoop(dir));
    }

    // ─────────────────────────────────────────────────────── Spin loop ───────
    IEnumerator SpinLoop(SwipeDir dir)
    {
        _spinActive = true;

        Vector3 axis = AxisFor(dir);
        Quaternion startRot = meshRoot.localRotation;
        float anglePerSec = 360f / trickDuration;
        float totalAngle = 0f;
        int spinsAwarded = 0;   // how many full 360s scored this hold

        // ── Keep spinning while holding AND airborne ─────────────────────────
        while (_pointerDown && sled.CurrentState == SledController.State.Airborne)
        {
            totalAngle += anglePerSec * Time.deltaTime;
            meshRoot.localRotation = startRot * Quaternion.AngleAxis(totalAngle, axis);

            // Award score each time we cross a 360° boundary
            int fullSpins = Mathf.FloorToInt(totalAngle / 360f);
            while (spinsAwarded < fullSpins)
            {
                spinsAwarded++;
                _comboCount++;
                int gained = Mathf.RoundToInt(
                    baseScore * Mathf.Pow(comboMultiplier, _comboCount - 1));
                _comboScore += gained;
                OnTrickCompleted?.Invoke(_comboCount, gained);
            }

            yield return null;
        }

        // ── Why did the loop exit? ────────────────────────────────────────────
        bool landedMidTrick = sled.CurrentState != SledController.State.Airborne;

        if (landedMidTrick)
        {
            // OnLanded() will call us — leave _spinActive = true so it knows
            // Don't touch meshRoot here; OnLanded snaps it
            _trickCoroutine = null;
            // _spinActive intentionally left true for OnLanded to read
        }
        else
        {
            // Pointer released cleanly while still airborne
            meshRoot.localRotation = startRot;  // snap back to pre-trick rotation
            _spinActive = false;
            _trickCoroutine = null;
            // Combo stays alive — will be banked on landing
        }
    }

    // ─────────────────────────────────────────────────────── Landing ─────────

    /// <summary>
    /// Call from SledController.CompleteReattach().
    /// Mid-trick landing  →  combo lost.
    /// Clean landing      →  combo banked.
    /// </summary>
    public void OnLanded()
    {
        if (_spinActive)
        {
            // Interrupt the coroutine if it's still somehow running
            if (_trickCoroutine != null)
            {
                StopCoroutine(_trickCoroutine);
                _trickCoroutine = null;
            }

            _spinActive = false;
            _pointerDown = false;

            // Snap mesh back instantly
            meshRoot.localRotation = _restRotation;

            int lostCombo = _comboCount;
            _comboCount = 0;
            _comboScore = 0;

            if (lostCombo > 0)
            {
                OnComboLost?.Invoke(lostCombo);
                Debug.Log($"[Tricks] Mid-trick landing — combo x{lostCombo} lost.");
            }
        }
        else if (_comboCount > 0)
        {
            // Landed cleanly after releasing — bank accumulated score
            int banked = _comboScore;
            int combo = _comboCount;
            _comboCount = 0;
            _comboScore = 0;

            OnComboFinished?.Invoke(banked);
            Debug.Log($"[Tricks] Clean landing — combo x{combo} = {banked} pts banked.");
        }
    }

    // ─────────────────────────────────────────────────────── Helpers ─────────
    static SwipeDir Classify(Vector2 d)
    {
        if (d.magnitude < 0.001f) return SwipeDir.None;

        // Angle in degrees: 0 = right, 90 = up, going counter-clockwise
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;

        // 8 sectors of 45°, each centred on a direction
        //   0° = Right, 45° = UpRight, 90° = Up, 135° = UpLeft,
        // 180° = Left, 225° = DownLeft, 270° = Down, 315° = DownRight
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
        // Cardinal base axes
        Vector3 flip = Vector3.right;   // backflip  (Down swipe)
        Vector3 flop = -Vector3.right;   // frontflip (Up swipe)
        Vector3 spinR = Vector3.up;      // right corkscrew
        Vector3 spinL = -Vector3.up;      // left corkscrew

        return dir switch
        {
            SwipeDir.Up => flop,
            SwipeDir.Down => flip,
            SwipeDir.Right => spinR,
            SwipeDir.Left => spinL,
            SwipeDir.UpRight => (flop + spinR).normalized,
            SwipeDir.UpLeft => (flop + spinL).normalized,
            SwipeDir.DownRight => (flip + spinR).normalized,
            SwipeDir.DownLeft => (flip + spinL).normalized,
            _ => Vector3.up
        };
    }
}