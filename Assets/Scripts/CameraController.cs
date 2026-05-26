using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraController : MonoBehaviour
{
     public float lerpTime = 3.5f;
    [Range(2, 3.5f)] public float forwardDistance = 3;
    private float accelerationEffect;

    public GameObject atachedVehicle;
    private int locationIndicator = 1;
    //private CarController controllerRef;

    private Vector3 newPos;
    private Transform target;
    private GameObject focusPoint;

    public float distance = 2;

    public Vector2[] cameraPos;
    void Start()
    {
        //cameraPos = new Vector2[4];
        //cameraPos[0] = new Vector2(2, 0);
        //cameraPos[1] = new Vector2(7.5f, 0.5f);
        //cameraPos[2] = new Vector2(8.9f, 1.2f);

        focusPoint = atachedVehicle.transform.Find("focus").gameObject;

        target = focusPoint.transform;
        //controllerRef = atachedVehicle.GetComponent<CarController>();
    }

    private void FixedUpdate()
    {
        UpdateCam();
    }

    public void CycleCamera()
    {
        if (locationIndicator >= cameraPos.Length - 1 || locationIndicator < 0) locationIndicator = 0;
        else locationIndicator++;
    }
    public void UpdateCam()
    {
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            CycleCamera();
        }

        newPos = target.position - (target.forward * cameraPos[locationIndicator].x) + (target.up * cameraPos[locationIndicator].y);

        //accelerationEffect = Mathf.Lerp(accelerationEffect, controllerRef.Gforce * 3.5f, 2 * Time.deltaTime);

        transform.position = Vector3.Lerp(transform.position, focusPoint.transform.GetChild(0).transform.position, lerpTime * Time.deltaTime);

        distance = Mathf.Pow(Vector3.Distance(transform.position, newPos), forwardDistance);

        transform.position = Vector3.MoveTowards(transform.position, newPos, distance * Time.deltaTime);

        transform.GetChild(0).transform.localRotation = Quaternion.Lerp(transform.GetChild(0).transform.localRotation, Quaternion.Euler(-accelerationEffect, 0, 0), 5 * Time.deltaTime);

        transform.LookAt(target.transform);
    }
}
