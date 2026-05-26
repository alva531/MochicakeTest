using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

[RequireComponent(typeof(Rigidbody))]
public class SledController : MonoBehaviour
{
    // ───────────────────────────────────────────────── Inspector ────────────
    [Header("Starting spline (optional)")]
    [Tooltip("Leave empty to start airborne and land on the first track hit.")]
    public SplineContainer startingSpline;

    [Header("Speed")]
    public float baseSpeed        = 8f;
    public float maxSpeed         = 40f;
    public float minSpeed         = 1f;
    public float gravityInfluence = 4f;
    public float brakingForce     = 5f;

    [Header("Alignment on track")]
    public float alignSpeed       = 12f;
    public float maxBankAngle     = 30f;
    public float bankSmoothing = 6f;
    
    [Header("Lateral offset")]
    [Tooltip("Max allowed distance left/right of spline center.")]
    public float maxLateralOffset = 1.8f;
    [Tooltip("How fast the sled drifts back to center (world units/s).")]
    public float centeringSpeed = 2f;
    
    float _lateralOffset = 0f;

    [Header("Cliff detach")]
    [Tooltip("Dot product of upcoming tangent vs Vector3.down to auto-detach. "
           + "–0.35 ≈ 20° past horizontal.")]
    public float cliffDropThreshold = -0.35f;

    [Header("Reattach")]
    public float snapSpeed          = 10f;
    public float reattachRadius     = 3.5f;
    [Tooltip("Velocity must point within this angle of the spline tangent to snap.")]
    public float maxReattachAngle = 50f;
    public float jumpReattachCooldown = 1.2f;
    [Header("Height offset on track")]
    public float trackHeightOffset = 0.08f;
    
    float _reattachCooldownTimer = 0f;

    // ───────────────────────────────────────────────── State ────────────────
    public enum State { OnTrack, Airborne, Reattaching }
    public State           CurrentState { get; private set; } = State.Airborne;
    public float           CurrentSpeed { get; private set; }
    public SplineContainer ActiveSpline { get; private set; }

    // ───────────────────────────────────────────────── Private ──────────────
    Rigidbody _rb;
    float     _t;
    float     _bank;

    // Reattach
    SplineContainer _snapSpline;
    float           _snapT;
    float           _snapBlend;

    // ───────────────────────────────────────────────── Unity ────────────────
    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        CurrentSpeed = baseSpeed;

        if (startingSpline != null)
        {
            ActiveSpline = startingSpline;
            SetKinematic(true);
            CurrentState = State.OnTrack;
        }
        else
        {
            SetKinematic(false);   // start in free-fall until first collision
        }
    }

    void FixedUpdate()
    {
        if (_reattachCooldownTimer > 0f)
            _reattachCooldownTimer -= Time.fixedDeltaTime;

        switch (CurrentState)
        {
            case State.OnTrack:     TickOnTrack();     break;
            case State.Reattaching: TickReattaching(); break;
        }
    }

    // ───────────────────────────────────────────────── On Track ─────────────
    void TickOnTrack()
    {
        float len = ActiveSpline.CalculateLength();

        ActiveSpline.Spline.Evaluate(_t,
            out float3 localPos, out float3 localTangent, out float3 localUp);

        Vector3 wTangent = ActiveSpline.transform
            .TransformDirection((Vector3)localTangent).normalized;
        Vector3 wUp = ActiveSpline.transform
            .TransformDirection((Vector3)localUp).normalized;

        // ── Speed ────────────────────────────────────────────────────────────
        float slope = Vector3.Dot(wTangent, Vector3.down);
        CurrentSpeed += slope * gravityInfluence * Time.fixedDeltaTime;

        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
            CurrentSpeed -= brakingForce * Time.fixedDeltaTime;

        CurrentSpeed = Mathf.Clamp(CurrentSpeed, minSpeed, maxSpeed);

        // ── Advance ──────────────────────────────────────────────────────────
        _t = Mathf.Clamp01(_t + (CurrentSpeed * Time.fixedDeltaTime) / len);

        // ── Drift back to center ─────────────────────────────────────────────────
        _lateralOffset = Mathf.MoveTowards(
            _lateralOffset, 0f,
            centeringSpeed * (CurrentSpeed / baseSpeed) * Time.fixedDeltaTime);

        // ── Position (center + lateral offset along spline right) ───────────────
        Vector3 splineRight = Vector3.Cross(wTangent, wUp).normalized;

        Vector3 wPos = ActiveSpline.transform.TransformPoint((Vector3)localPos)
                    + splineRight  * _lateralOffset
                    + transform.up * trackHeightOffset;

        _rb.MovePosition(wPos);

        // ── Rotation + banking ────────────────────────────────────────────────
        float turn = Vector3.SignedAngle(transform.forward, wTangent, wUp);
        _bank = Mathf.Lerp(_bank,
            Mathf.Clamp(-turn * (maxBankAngle / 10f), -maxBankAngle, maxBankAngle),
            bankSmoothing * Time.fixedDeltaTime);

        Quaternion targetRot = Quaternion.LookRotation(wTangent, wUp)
                             * Quaternion.AngleAxis(_bank, Vector3.forward);
        _rb.MoveRotation(Quaternion.Slerp(
            _rb.rotation, targetRot, alignSpeed * Time.fixedDeltaTime));

        // ── Cliff check ──────────────────────────────────────────────────────
        if (CheckCliffAhead(len))
        {
            Detach(wTangent * CurrentSpeed);
            return;
        }

        if (_t >= 1f) OnReachedEnd();
    }

    // ───────────────────────────────────────────────── Reattaching ──────────
    void TickReattaching()
    {
        _snapBlend += Time.fixedDeltaTime * (snapSpeed / reattachRadius);
        _snapBlend  = Mathf.Clamp01(_snapBlend);

        float len = _snapSpline.CalculateLength();

        _snapSpline.Spline.Evaluate(_snapT,
            out float3 localPos, out float3 localTangent, out float3 localUp);

        Vector3 wTangent = _snapSpline.transform
            .TransformDirection((Vector3)localTangent).normalized;
        Vector3 wUp      = _snapSpline.transform
            .TransformDirection((Vector3)localUp).normalized;
        Vector3 right    = Vector3.Cross(wTangent, wUp).normalized;

        // Target is the offset position, not center — sled arrives on the side
        Vector3 wPos = _snapSpline.transform.TransformPoint((Vector3)localPos)
                    + right    * _lateralOffset
                    + wUp      * trackHeightOffset;

        _rb.MovePosition(Vector3.Lerp(transform.position, wPos, _snapBlend));
        _rb.MoveRotation(Quaternion.Slerp(_rb.rotation,
            Quaternion.LookRotation(wTangent, wUp),
            alignSpeed * Time.fixedDeltaTime));

        // Use CurrentSpeed — _rb.velocity is 0 while kinematic
        _snapT = Mathf.Clamp01(_snapT + (CurrentSpeed * Time.fixedDeltaTime) / len);

        if (_snapBlend >= 1f) CompleteReattach();
    }

    // ───────────────────────────────────────────────── Collision ────────────

    /// <summary>
    /// Called by the physics engine when the sled's collider touches anything.
    /// If the surface belongs to a SplineContainer, begin reattaching to it.
    /// Works for both the original track and any new track landed on mid-air.
    /// </summary>
    void OnCollisionEnter(Collision collision)
    {
        if (CurrentState != State.Airborne) return;
        if (_reattachCooldownTimer > 0f)  return;

        SplineContainer candidate =
            collision.gameObject.GetComponent<SplineContainer>();

        if (candidate == null) return;

        // Find the nearest point on that specific spline
        Vector3 localQuery = candidate.transform
            .InverseTransformPoint(transform.position);

        SplineUtility.GetNearestPoint(
            candidate.Spline, localQuery,
            out float3 nearestLocal, out float nearT);

        Vector3 nearestWorld = candidate.transform
            .TransformPoint((Vector3)nearestLocal);

        // Reject if too far (e.g. a wall with a SplineContainer somewhere above)
        if (Vector3.Distance(transform.position, nearestWorld) > reattachRadius)
            return;

        // Reject if we're flying the wrong way relative to the spline
        if (!IsVelocityAligned(candidate, nearT)) return;

        BeginReattach(candidate, nearT);
    }

    // ───────────────────────────────────────────────── Transitions ──────────
    void Detach(Vector3 launchVelocity)
    {
        SetKinematic(false);
        _rb.velocity = launchVelocity;
        CurrentState = State.Airborne;
        Debug.Log($"[Sled] Cliff detach → Airborne  {launchVelocity.magnitude:F1} m/s");
    }

    void BeginReattach(SplineContainer target, float nearT)
    {
        // Capture actual physics speed before SetKinematic zeroes the velocity
        CurrentSpeed = Mathf.Clamp(_rb.velocity.magnitude, minSpeed, maxSpeed);
    
        target.Spline.Evaluate(nearT,
            out float3 centerLocal, out float3 tanLocal, out float3 upLocal);

        Vector3 centerWorld = target.transform.TransformPoint((Vector3)centerLocal);
        Vector3 wTan  = target.transform.TransformDirection((Vector3)tanLocal).normalized;
        Vector3 wUp   = target.transform.TransformDirection((Vector3)upLocal).normalized;
        Vector3 right = Vector3.Cross(wTan, wUp).normalized;

        _lateralOffset = Vector3.Dot(transform.position - centerWorld, right);
        _lateralOffset = Mathf.Clamp(_lateralOffset, -maxLateralOffset, maxLateralOffset);

        SetKinematic(true);
        _snapSpline  = target;
        _snapT       = nearT;
        _snapBlend   = 0f;
        CurrentState = State.Reattaching;

        string tag = target != ActiveSpline ? "  ★ NEW TRACK" : "";
        Debug.Log($"[Sled] Reattach → {target.name}  t={nearT:F3}  lateral={_lateralOffset:F2}{tag}");
    }

    void CompleteReattach()
    {
        ActiveSpline = _snapSpline;
        _t           = _snapT;
        // CurrentSpeed already set in BeginReattach — don't overwrite it
        _bank        = 0f;
        CurrentState = State.OnTrack;

        Debug.Log($"[Sled] OnTrack → {ActiveSpline.name}  " +
                $"spd={CurrentSpeed:F1}  lateralOffset={_lateralOffset:F2}");
    }

    // ───────────────────────────────────────────────── Helpers ──────────────
    bool CheckCliffAhead(float splineLength)
    {
        float lookAhead = Mathf.Clamp01(_t + 0.02f / splineLength);
        ActiveSpline.Spline.Evaluate(lookAhead,
            out _, out float3 aheadTangent, out _);
        Vector3 worldAhead = ActiveSpline.transform
            .TransformDirection((Vector3)aheadTangent).normalized;
        return Vector3.Dot(worldAhead, Vector3.down) < cliffDropThreshold;
    }

    bool IsVelocityAligned(SplineContainer sc, float t)
    {
        if (_rb.velocity.sqrMagnitude < 0.1f) return true;
        sc.Spline.Evaluate(t, out _, out float3 localTan, out _);
        Vector3 wTan = sc.transform
            .TransformDirection((Vector3)localTan).normalized;
        return Vector3.Angle(_rb.velocity.normalized, wTan) < maxReattachAngle;
    }

    void SetKinematic(bool kinematic)
    {
        _rb.isKinematic   = kinematic;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
    }

    void OnReachedEnd()
    {
        Debug.Log($"[Sled] End of {ActiveSpline.name} — launching off");
        ActiveSpline.Spline.Evaluate(1f, out _, out float3 localTan, out _);
        Vector3 wTan = ActiveSpline.transform
            .TransformDirection((Vector3)localTan).normalized;
        Detach(wTan * CurrentSpeed);
    }

    public void ForceJump(float upBoost = 0f)
    {
        if (CurrentState != State.OnTrack) return;

        ActiveSpline.Spline.Evaluate(_t,
            out _, out float3 localTan, out float3 localUp);
        Vector3 wTan = ActiveSpline.transform
            .TransformDirection((Vector3)localTan).normalized;
        Vector3 wUp  = ActiveSpline.transform
            .TransformDirection((Vector3)localUp).normalized;

        Detach(wTan * CurrentSpeed + wUp * upBoost);
        _reattachCooldownTimer = jumpReattachCooldown;
    }

    // ───────────────────────────────────────────────── Gizmos ───────────────
#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.2f);
        Gizmos.DrawSphere(transform.position, reattachRadius);
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.7f);
        Gizmos.DrawWireSphere(transform.position, reattachRadius);

        if (Application.isPlaying && ActiveSpline != null)
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 1.5f,
                $"{CurrentState}  |  {ActiveSpline.name}\n" +
                $"t={_t:F3}  spd={CurrentSpeed:F1}");
    }
#endif
}