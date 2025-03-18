using Cinemachine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    GameObject player;
    Transform playerTransform;

    public float speed = 5f;

    void Start()
    {
        player = GameObject.Find("Player");
        playerTransform = player.transform;
    }

    void Update()
    {
        Vector3 dir = Vector3.zero;

        if (Input.GetKey(KeyCode.W)) dir += Camera.main.transform.forward;
        if (Input.GetKey(KeyCode.S)) dir -= Camera.main.transform.forward;
        if (Input.GetKey(KeyCode.D)) dir += Camera.main.transform.right;
        if (Input.GetKey(KeyCode.A)) dir -= Camera.main.transform.right;

        dir = new Vector3(dir.x, 0, dir.z);
        playerTransform.position += dir.normalized * Time.deltaTime * speed;
    }
}
