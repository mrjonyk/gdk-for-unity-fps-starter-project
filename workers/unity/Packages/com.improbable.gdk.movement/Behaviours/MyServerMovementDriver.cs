﻿using System.Collections.Generic;
using System.Linq;
using Improbable;
using Improbable.Gdk.Core;
using Improbable.Gdk.GameObjectRepresentation;
using Improbable.Gdk.Movement;
using Improbable.Gdk.StandardTypes;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class MyServerMovementDriver : MonoBehaviour
{
    [Require] private ClientMovement.Requirable.Reader clientInput;
    [Require] private ServerMovement.Requirable.Writer server;
    [Require] private Position.Requirable.Writer spatialPosition;

    private CharacterController controller;
    private SpatialOSComponent spatial;
    private CommandFrameSystem commandFrame;

    private Vector3 origin;

    private int lastFrame = -1;
    private int firstFrame = -1;
    private int frameBuffer = 5;
    private int nextInputFrame = -1;

    private float clientDilation = 0f;
    private int bufferCount = 0;
    private float bufferAvg = 0;
    private const float BufferAlpha = 0.99f;
    private int emptySamples = 0;

    private float rtt = (5 - 1) * 2 * CommandFrameSystem.FrameLength;
    private const float RttAlpha = 0.95f;

    private const int PositionRate = 30;
    private int positionTick = 0;

    private ClientRequest lastInput = new ClientRequest() { Timestamp = -1 };

    private readonly List<ClientRequest> clientInputs = new List<ClientRequest>();
    private readonly Dictionary<int, MovementState> movementState = new Dictionary<int, MovementState>();

    private readonly MyMovementUtils.RestoreStateProcessor restoreState = new MyMovementUtils.RestoreStateProcessor();
    private readonly MyMovementUtils.RemoveWorkerOrigin removeWorkerOrigin = new MyMovementUtils.RemoveWorkerOrigin();
    private readonly MyMovementUtils.TeleportProcessor teleportProcessor = new MyMovementUtils.TeleportProcessor();

    private MyMovementUtils.IMovementProcessor[] movementProcessors;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        renderLine = nextRenderLine;
        nextRenderLine += 1;

        movementProcessors = new MyMovementUtils.IMovementProcessor[]
        {
            restoreState,
            teleportProcessor,
            new StandardMovement(),
            new MyMovementUtils.SprintCooldown(),
            new JumpMovement(),
            new MyMovementUtils.Gravity(),
            new MyMovementUtils.TerminalVelocity(),
            new MyMovementUtils.ApplyMovementProcessor(),
            removeWorkerOrigin,
            new MyMovementUtils.AdjustVelocity(),
        };
    }

    private void OnEnable()
    {
        spatial = GetComponent<SpatialOSComponent>();
        commandFrame = spatial.World.GetExistingManager<CommandFrameSystem>();

        teleportProcessor.Origin = spatial.Worker.Origin;
        restoreState.Origin = spatial.Worker.Origin;
        removeWorkerOrigin.Origin = spatial.Worker.Origin;

        clientInput.OnClientInput += OnClientInputReceived;

        origin = spatial.Worker.Origin;
    }

    private void OnClientInputReceived(ClientRequest request)
    {
        //Debug.LogFormat($"[{lastFrame}] Client Request: {request.Timestamp}");

        // Debug.Log($"[Server:{lastFrame}] Receive Client Input {request.Timestamp}");

        if (lastFrame < 0)
        {
            return;
        }

        if (firstFrame < 0)
        {
            firstFrame = lastFrame + frameBuffer;
            nextInputFrame = firstFrame;
            movementState[firstFrame - 1] = server.Data.Latest.MovementState;
            //Debug.Log($"[{lastFrame}] First Input received, applied on {firstFrame}, offset: {clientFrameOffset}");
        }

        request.Movement.X = nextInputFrame;
        nextInputFrame += 1;
        // Debug.Log($"[Server {lastFrame}] should get applied on frame: {request.Movement.X}");

        // Debug.Log($"[Server {lastFrame}] Add Client Input: {request.Timestamp} ({clientInputs.Count})");
        clientInputs.Add(request);

        if (firstFrame < lastFrame)
        {
            UpdateRtt(request);
        }
    }

    private void UpdateRtt(ClientRequest request)
    {
        if (request.AppliedDilation > 0)
        {
            var sample = Time.time - (request.AppliedDilation / 100000f);
            rtt = RttAlpha * rtt + (1 - RttAlpha) * sample;
            frameBuffer = Mathf.CeilToInt(rtt / (2 * CommandFrameSystem.FrameLength)) + 2;
        }
    }

    private void Update()
    {
        if (commandFrame.NewFrame)
        {
            if (firstFrame < 0)
            {
                lastFrame = commandFrame.CurrentFrame;
                return;
            }

            while (lastFrame <= commandFrame.CurrentFrame)
            {
                lastFrame += 1;

                if (lastFrame < firstFrame)
                {
                    //Debug.LogFormat($"[{lastFrame}] Skipping frame until first frame: {firstFrame}");
                    continue;
                }

                if (clientInputs.Count > 0)
                {
                    lastInput = clientInputs[0];
                    clientInputs.RemoveAt(0);
                    // Debug.Log($"[Server {lastFrame}] Dequeue Client input {lastFrame} ({clientInputs.Count})");
                }
                else
                {
                    // Debug.LogWarning($"[Server {lastFrame}] Input Missing!");
                    // Repeat last frame, but increment timestamp?
                    // Debug.LogFormat($"[Server {lastFrame}] No client input, repeat previous");
                    lastInput.Timestamp += 1;
                }

                movementState.TryGetValue(lastFrame - 1, out var previousState);
                var state = MyMovementUtils.ApplyInput(controller, lastInput, previousState, movementProcessors);
                movementState[lastFrame] = state;
                SendMovement(state, lastInput.Timestamp);

                // Remove movement state from 10 frames ago
                movementState.Remove(lastFrame - 10);
            }

            UpdateBufferAdjustment();
        }
    }

    private void SendMovement(MovementState state, int clientFrame)
    {
        // Debug.Log($"[Server:{lastFrame}] Send Response: {clientFrame}");

        var response = new ServerResponse
        {
            MovementState = state,
            Timestamp = clientFrame,
            Yaw = lastInput.CameraYaw,
            Pitch = lastInput.CameraPitch,
            Aiming = lastInput.AimPressed,
            NextDilation = (int) (clientDilation * 100000f),
            AppliedDilation = (int) (Time.time * 100000f)
        };
        server.SendServerMovement(response);
        var update = new ServerMovement.Update { Latest = response };
        server.Send(update);

        positionTick -= 1;
        if (positionTick <= 0)
        {
            var pos = state.Position.ToVector3();
            positionTick = PositionRate;
            spatialPosition.Send(new Position.Update()
            {
                Coords = new Option<Coordinates>(new Coordinates(pos.x, pos.y, pos.z))
            });
        }
    }

    private void UpdateBufferAdjustment()
    {
        bufferCount = clientInputs.Count;
        bufferAvg = BufferAlpha * bufferAvg + (1 - BufferAlpha) * bufferCount;
        if (bufferCount == 0)
        {
            emptySamples++;
        }

        if (lastFrame % 50 == 0)
        {
            var error = bufferAvg - frameBuffer;
            if (error < -0.3f)
            {
                clientDilation = -1;
            }
            else if (error > 0.3f)
            {
                clientDilation = 1;
            }

            emptySamples = 0;
        }
        else
        {
            clientDilation = 0;
        }
    }

    public void Teleport(Vector3 spawnPosition)
    {
        //Debug.LogFormat("Mark Teleport Processor with position: {0}", spawnPosition);
        teleportProcessor.Teleport(spawnPosition);
    }

    private static int nextRenderLine = 0;
    private int renderLine = 0;

    private void OnGUI()
    {
        if (!MyMovementUtils.ShowDebug)
        {
            return;
        }

        var delta = clientInputs.Count - frameBuffer;

        GUI.Label(new Rect(10, 100 + renderLine * 20, 700, 20),
            string.Format("Input Buffer Sample: {0:00}, Avg: {1:00.00}, cd: {2:00.00}, RTT: {3:00.0}, B: {4}, Empty: {5}",
                bufferCount, bufferAvg, clientDilation, rtt * 1000f, frameBuffer, emptySamples));

        GUI.Label(new Rect(10, 300, 700, 20),
            string.Format("Frame: {0}, Length: {1:00.0}, Remainder: {2:00.0}",
                commandFrame.CurrentFrame, CommandFrameSystem.FrameLength * 1000f, commandFrame.GetRemainder() * 1000f));
    }
}