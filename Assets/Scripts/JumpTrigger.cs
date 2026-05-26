using UnityEngine;

/// <summary>
/// Attach to a Trigger collider at the end of a ramp or cliff.
/// Automatically forces the sled into Airborne state with a configurable
/// boost angle so it sails off cleanly instead of clipping the edge.
/// </summary>
public class JumpTrigger : MonoBehaviour
{
    [Tooltip("Extra upward boost applied on launch (0 = pure tangent direction)")]
    public float upwardBoost = 3f;

    void OnTriggerEnter(Collider other)
    {
        var sled = other.GetComponent<SledController>();
        if (sled == null || sled.CurrentState != SledController.State.OnTrack)
            return;

        // Force a jump via the public method below
        sled.ForceJump(upwardBoost);
    }
}