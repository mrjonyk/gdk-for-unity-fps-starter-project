﻿using Fps;
using UnityEngine;

[RequireComponent(typeof(FpsAnimator), typeof(MyProxyMovementDriver))]
public class FpsProxyDriver : MonoBehaviour
{
    [SerializeField] public GameObject ControllerProxy;

    private FpsAnimator animator;
    private MyProxyMovementDriver movement;

    private void Awake()
    {
        animator = GetComponent<FpsAnimator>();
        movement = GetComponent<MyProxyMovementDriver>();
    }

    private void Start()
    {
        ControllerProxy.transform.parent = null;
        ControllerProxy.name = $"{name} Controller Proxy";
        movement.Controller = ControllerProxy.GetComponent<CharacterController>();
    }

    private void Update()
    {
        animator.SetAiming(movement.GetAiming());
        animator.SetGrounded(MyMovementUtils.IsGrounded(movement.Controller));
        var vel = movement.GetVelocity();
        vel.y = 0;
        animator.SetMovement(vel);
        animator.SetPitch(movement.GetPitch());

        transform.position = ControllerProxy.transform.position;
        transform.rotation = ControllerProxy.transform.rotation;
    }
}