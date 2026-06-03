using System;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Attach to a Trigger collider at the end of a ramp or cliff.
/// Automatically forces the sled into Airborne state with a configurable
/// boost angle so it sails off cleanly instead of clipping the edge.
/// </summary>
public class EventTrigger : MonoBehaviour
{
    public bool ActivatesNextTrack = false;

    [Tooltip("Extra upward boost applied on launch (0 = pure tangent direction)")]
    public float upwardBoost = 3f;

    public SplineContainer nextSpline;

    void OnTriggerEnter(Collider other)
    {
        Debug.Log("Triggered by: " + other.name);
        var sled = other.GetComponent<SledController>();
        if (!ActivatesNextTrack)
        {
            if (sled == null || sled.CurrentState != SledController.State.OnTrack)
                return;
            sled.ForceJump(upwardBoost);
            Destroy(sled.LastSpline.gameObject, 5f);
        }
        else if (ActivatesNextTrack)
        {
            nextSpline.gameObject.SetActive(true);
        }
    }
}