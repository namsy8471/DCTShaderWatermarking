using UnityEngine;
using Cinemachine;

public class CameraController
{
    public CinemachineFreeLook freeLookCam;
    public float yMouseSensitivity = 1f;
    public float xMouseSensitivity = 1f;

    public CameraController(CinemachineFreeLook freeLookCam)
    {
        this.freeLookCam = freeLookCam;
    }

    public void LateUpdate()
    {
        float horizontal = Input.GetAxis("Mouse X") * xMouseSensitivity;
        float vertical = Input.GetAxis("Mouse Y") * yMouseSensitivity;

        freeLookCam.m_XAxis.Value += horizontal;
        freeLookCam.m_YAxis.Value += vertical;
    }
}
