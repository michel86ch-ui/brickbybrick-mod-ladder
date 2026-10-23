using System;
using ECM2;
using MultiplayerTemplate.Core.Input;
using UnityEngine;

namespace Ladder
{
    /// <summary>
    /// Climbing for the local player, after the ladder example that ships with the game's character controller (ECM2,
    /// LadderClimbAbility): a custom movement mode that moves the player along the ladder. Forward goes up while one
    /// faces the wall (or looks along it) and down while one faces away, back the other way. Walking into the ladder
    /// facing the wall grabs it; at the top one steps off onto the surface behind the edge, at the bottom onto the
    /// floor, jump lets go. From the top: stand where one steps off, face outwards and walk forward. The game's own look
    /// and placing keep working while on the ladder.
    /// </summary>
    internal sealed class LadderClimber : MonoBehaviour
    {
        private const int ClimbMode = 42;           // the game uses no custom movement modes (measured)
        private const float GrabTime = 0.2f, StepTime = 0.45f, NoGrabAfterLettingGo = 0.6f;

        private enum Phase { None, Grab, Climb, StepOff, StepOn }

        internal LadderInstance Ladder;
        private Character character;
        private Func<IInputReader> input;
        private Phase phase;
        private float s, t;
        private Vector3 from, to;
        private float noGrabUntil;

        internal bool IsClimbing => character != null && character.movementMode == Character.MovementMode.Custom
            && character.customMovementMode == ClimbMode;

        /// <summary>Feet in front of the ladder: the player's capsule radius and 15 cm (the lower section stands out 14 cm).</summary>
        private float Offset => character.characterMovement.radius + 0.15f;

        internal void Init(Character c, Func<IInputReader> inputReader)
        {
            character = c;
            input = inputReader;
            character.MovementModeChanged += OnMovementModeChanged;
            character.CustomMovementModeUpdated += OnCustomMovementModeUpdated;
        }

        private void OnDestroy()
        {
            if (character != null)
            {
                character.MovementModeChanged -= OnMovementModeChanged;
                character.CustomMovementModeUpdated -= OnCustomMovementModeUpdated;
            }
        }

        private void Update()
        {
            if (character == null)
            {
                return;
            }
            if (Ladder == null)
            {
                if (IsClimbing)
                {
                    LetGo(Vector3.zero);
                }
                return;
            }
            IInputReader inp = input();
            if (inp == null || !inp.GameplayInputEnabled)
            {
                return;
            }
            if (IsClimbing)
            {
                if (phase == Phase.Climb && inp.JumpPressed)
                {
                    LetGo(Ladder.Front * 1.5f);
                }
                return;
            }
            if (inp.Move.y < 0.3f || character.IsCrouched() || Time.time < noGrabUntil)
            {
                return;
            }
            Vector3 pos = character.GetPosition();
            Vector3 facing = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            float highest = Ladder.HighestParam(Offset);
            float along = Ladder.ParamOf(pos), front = Ladder.FrontOf(pos);
            if (Vector3.Dot(facing, Ladder.Dir) > 0.4f && Mathf.Abs(Ladder.SideOf(pos)) < 0.45f && front > 0f && front < Offset + 0.6f
                && along > -0.6f && along < highest + 0.3f)
            {
                s = Mathf.Clamp(along, 0f, highest);
                Begin(Phase.Grab, Ladder.Path(s, Offset));
            }
            else if (Ladder.HasExit && Vector3.Dot(facing, -Ladder.Dir) > 0.4f && Mathf.Abs(pos.y - Ladder.Exit.y) < 0.4f
                && Vector3.ProjectOnPlane(pos - Ladder.Exit, Vector3.up).magnitude < 0.8f)
            {
                s = highest;
                Begin(Phase.StepOn, Ladder.Path(s, Offset));
            }
        }

        private void Begin(Phase p, Vector3 target)
        {
            phase = p;
            from = character.GetPosition();
            to = target;
            t = 0f;
            character.SetMovementMode(Character.MovementMode.Custom, ClimbMode);
            if (p == Phase.StepOn)
            {
                character.characterMovement.detectCollisions = false;   // over the parapet onto the ladder
            }
        }

        private void LetGo(Vector3 push)
        {
            phase = Phase.None;
            noGrabUntil = Time.time + NoGrabAfterLettingGo;      // holding forward must not grab it again at once
            character.characterMovement.detectCollisions = true;
            character.SetMovementMode(Character.MovementMode.Falling);
            character.SetVelocity(push);
        }

        private void OnMovementModeChanged(Character.MovementMode prevMovementMode, int prevCustomMode)
        {
            if (IsClimbing)
            {
                character.StopJumping();
                character.EnableGroundConstraint(false);
            }
            else if (prevMovementMode == Character.MovementMode.Custom && prevCustomMode == ClimbMode)
            {
                phase = Phase.None;
                character.characterMovement.detectCollisions = true;
                character.EnableGroundConstraint(true);
            }
        }

        private void OnCustomMovementModeUpdated(float deltaTime)
        {
            if (!IsClimbing || deltaTime <= 0f)
            {
                return;
            }
            if (Ladder == null)
            {
                LetGo(Vector3.zero);
                return;
            }
            Vector3 pos = character.GetPosition();
            Vector3 target;
            switch (phase)
            {
                case Phase.Grab:
                case Phase.StepOn:
                case Phase.StepOff:
                    t += deltaTime;
                    float k = Mathf.Clamp01(t / (phase == Phase.Grab ? GrabTime : StepTime));
                    float smooth = k * k * (3f - 2f * k);
                    target = Vector3.Lerp(from, to, smooth) + Vector3.up * (phase == Phase.Grab ? 0f : 0.3f * Mathf.Sin(k * Mathf.PI));
                    if (k >= 1f)
                    {
                        if (phase == Phase.StepOff)
                        {
                            character.SetPosition(to);
                            LetGo(Vector3.zero);
                            Plugin.Log.LogInfo("Stepped off the ladder at the top.");
                            return;
                        }
                        phase = Phase.Climb;
                        character.characterMovement.detectCollisions = true;
                    }
                    break;
                default:
                    IInputReader inp = input();
                    Vector3 facing = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                    float move = (inp != null && inp.GameplayInputEnabled ? inp.Move.y : 0f) * (Vector3.Dot(facing, Ladder.Dir) < -0.2f ? -1f : 1f);
                    float highest = Ladder.HighestParam(Offset);
                    if (move > 0.2f && s >= highest - 0.001f && Ladder.HasExit)
                    {
                        phase = Phase.StepOff;
                        from = pos;
                        to = Ladder.Exit;
                        t = 0f;
                        character.characterMovement.detectCollisions = false;   // over the edge onto the surface
                        target = pos;
                        break;
                    }
                    if (move < -0.2f && s <= 0.001f)
                    {
                        LetGo(Vector3.zero);
                        return;
                    }
                    s = Mathf.Clamp(s + move * Plugin.ClimbSpeed.Value * deltaTime, 0f, highest);
                    target = Ladder.Path(s, Offset);
                    break;
            }
            character.SetVelocity((target - pos) / deltaTime);
        }
    }
}
