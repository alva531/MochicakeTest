using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

[RequireComponent(typeof(Rigidbody))]
public class PathFollower : MonoBehaviour
{
    [Header("Spline")]
    public SplineContainer splineContainer;

    [Header("Movement")]
    public float baseSpeed = 5f;
    public float gravityInfluence = 3f;   // Extra speed on downward slopes
    public float brakingForce = 2f;
    public float maxSpeed = 30f;
    public float minSpeed = 1f;

    [Header("Alignment")]
    public float alignmentSpeed = 10f;    // How fast sled rotates to match path
    public float bankAngle = 25f;         // Max tilt on curves
    public float bankSmoothing = 5f;

    [Header("Offset")]
    public float heightOffset = 0.1f;     // Sled sits slightly above spline

    // ── State ────────────────────────────────────────────────────────────────
    private Rigidbody _rb;
    private float _t;                     // Normalized position along spline [0,1]
    private float _currentSpeed;
    private float _currentBank;
    private bool _braking;

    // ── Cached spline length ──────────────────────────────────────────────────
    private float _splineLength;

    void Start()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;           // We drive position manually
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        _currentSpeed = baseSpeed;
        _splineLength = splineContainer.CalculateLength();
    }

    void Update()
    {
        _braking = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.S);
    }

    void FixedUpdate()
    {
        if (splineContainer == null) return;

        UpdateSpeed();
        AdvanceAlongSpline();
        AlignToSpline();
    }

    // ─────────────────────────────────────────────────────────────────────────
    void UpdateSpeed()
    {
        // Sample the forward tangent to get slope
        splineContainer.Spline.Evaluate(_t, out float3 pos, out float3 tangent, out float3 up);

        float3 worldTangent = splineContainer.transform.TransformDirection(tangent);
        float slopeAngle = Vector3.Dot(worldTangent.ToVector3().normalized, Vector3.down);

        // Gravity: speed up going down, slow down going up
        float gravityDelta = slopeAngle * gravityInfluence * Time.fixedDeltaTime;
        _currentSpeed += gravityDelta;

        // Braking
        if (_braking)
            _currentSpeed -= brakingForce * Time.fixedDeltaTime;

        _currentSpeed = Mathf.Clamp(_currentSpeed, minSpeed, maxSpeed);
    }

    void AdvanceAlongSpline()
    {
        float tDelta = (_currentSpeed * Time.fixedDeltaTime) / _splineLength;
        _t = Mathf.Clamp01(_t + tDelta);

        // Sample spline world position
        splineContainer.Spline.Evaluate(_t, out float3 localPos, out _, out float3 localUp);

        Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);
        worldPos += transform.up * heightOffset;   // hover sled above rail

        _rb.MovePosition(worldPos);

        if (_t >= 1f)
            OnReachedEnd();
    }

    void AlignToSpline()
    {
        splineContainer.Spline.Evaluate(_t, out _, out float3 localTangent, out float3 localUp);

        Vector3 worldForward = splineContainer.transform.TransformDirection(localTangent).normalized;
        Vector3 worldUp      = splineContainer.transform.TransformDirection(localUp).normalized;

        // Base rotation to face forward along spline
        Quaternion targetRot = Quaternion.LookRotation(worldForward, worldUp);

        // Banking: tilt into curves by comparing current vs previous forward
        float turnDelta = Vector3.SignedAngle(
            transform.forward, worldForward, worldUp);
        float targetBank = Mathf.Clamp(-turnDelta * (bankAngle / 10f), -bankAngle, bankAngle);
        _currentBank = Mathf.Lerp(_currentBank, targetBank, bankSmoothing * Time.fixedDeltaTime);

        targetRot *= Quaternion.AngleAxis(_currentBank, Vector3.forward);

        _rb.MoveRotation(Quaternion.Slerp(
            _rb.rotation, targetRot, alignmentSpeed * Time.fixedDeltaTime));
    }

    void OnReachedEnd()
    {
        // TODO: trigger finish, loop, etc.
        Debug.Log("Sled reached the end of the track!");
        enabled = false;
    }
}

// Helper — Unity.Mathematics float3 → UnityEngine.Vector3
public static class Float3Ext
{
    public static Vector3 ToVector3(this Unity.Mathematics.float3 f)
        => new Vector3(f.x, f.y, f.z);
}
