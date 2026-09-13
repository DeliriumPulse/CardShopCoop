using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Shared box placement and physics helpers used by every family and by the engine.
    ///
    /// A remote box never runs a second networked physics simulation. The owner of a
    /// <see cref="BoxPossession.Placing"/> box is reconstructed from its avatar camera;
    /// a <see cref="BoxPossession.Free"/> box is committed to the host's authoritative
    /// pose. These helpers are the single implementation of that rule so the item, card,
    /// and furniture families cannot drift apart.
    /// </summary>
    public static class BoxPlacement
    {
        private sealed class RemoteMotion
        {
            public Vector3 From;
            public Vector3 To;
            public float FromYaw;
            public float ToYaw;
            public float Age;
            public float Duration;
            public bool Arc;
            // dead-reckon push mode (a player is physically pushing the box): the target is the
            // streamed pose, extrapolated along the streamed velocity between frames. The body is
            // kinematic for the duration; it resumes physics when the stream goes stale or a
            // reliable Free snapshot cancels the entry.
            public bool DeadReckon;
            public Vector3 Target;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public float LastUpdate;
            public float TargetYaw;
        }

        // Push-smoothing tuning. DeadReckonMaxSpeed is the clamped correction speed (a generous
        // max keeps lag to about a frame while smoothing the per-frame wire steps); PushSnapDistance
        // is the correction size above which we hard-snap instead of dragging the box across.
        private const float DeadReckonMaxSpeed = 12f;
        private const float PushSnapDistance = 1.0f;
        private const float MaxWireHorizontal = 1000f;
        private const float MaxWireHeight = 100f;
        private const float MaxWireVelocity = 1000f;

        private static readonly Dictionary<InteractablePackagingBox, RemoteMotion> RemoteMotions
            = new Dictionary<InteractablePackagingBox, RemoteMotion>();

        private sealed class PlacementIntent
        {
            public bool Moving;
            public int AvatarId;
        }

        private static readonly Dictionary<InteractablePackagingBox, PlacementIntent> PlacementIntents
            = new Dictionary<InteractablePackagingBox, PlacementIntent>();
        private static readonly HashSet<InteractablePackagingBox> PlacementPhysicsDisabled
            = new HashSet<InteractablePackagingBox>();
        private static readonly HashSet<InteractablePackagingBox> PlacementInvariantLogged
            = new HashSet<InteractablePackagingBox>();
        private static readonly HashSet<InteractablePackagingBox> PlacementRayLogged
            = new HashSet<InteractablePackagingBox>();

        // A throw's impulse (vanilla ThrowBox: AddForce(forward * 450)) is not reflected in
        // Rigidbody.velocity until the next FixedUpdate, so sampling it on the throw frame
        // reads ~0. Mark the box at the throw and defer its release report until the velocity
        // is real (or a few frames have passed). One-shot per throw, not a session window.
        private static readonly Dictionary<InteractablePackagingBox, int> ThrowPending
            = new Dictionary<InteractablePackagingBox, int>();
        private const int ThrowWaitMaxFrames = 10;

        public static void Reset()
        {
            RemoteMotions.Clear();
            PlacementIntents.Clear();
            PlacementPhysicsDisabled.Clear();
            PlacementInvariantLogged.Clear();
            PlacementRayLogged.Clear();
            ThrowPending.Clear();
        }

        public static void MarkThrow(InteractablePackagingBox box)
        {
            if (box != null)
                ThrowPending[box] = Time.frameCount;
        }

        public static bool IsThrowPending(InteractablePackagingBox box)
        {
            return box != null && ThrowPending.ContainsKey(box);
        }

        public static bool HasPendingThrows => ThrowPending.Count > 0;

        /// <summary>True once the throw impulse is integrated (or we waited long enough) so
        /// the sampled velocity is real.</summary>
        public static bool ThrowReady(InteractablePackagingBox box, Vector3 velocity)
        {
            if (box == null || !ThrowPending.TryGetValue(box, out int frame))
                return true;
            return velocity.sqrMagnitude >= 0.5f || Time.frameCount - frame >= ThrowWaitMaxFrames;
        }

        public static void ClearThrow(InteractablePackagingBox box)
        {
            if (!(box is null))
                ThrowPending.Remove(box);
        }

        public static bool IsSanePose(Vector3 position, float yaw)
        {
            return IsFinite(position.x) && IsFinite(position.y) && IsFinite(position.z)
                && IsFinite(yaw)
                && Mathf.Abs(position.x) <= MaxWireHorizontal
                && position.y >= -2f && position.y <= MaxWireHeight
                && Mathf.Abs(position.z) <= MaxWireHorizontal;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool IsSaneVelocity(Vector3 velocity, Vector3 angularVelocity)
        {
            return IsFinite(velocity.x) && IsFinite(velocity.y) && IsFinite(velocity.z)
                && IsFinite(angularVelocity.x) && IsFinite(angularVelocity.y)
                && IsFinite(angularVelocity.z)
                && velocity.sqrMagnitude <= MaxWireVelocity * MaxWireVelocity
                && angularVelocity.sqrMagnitude <= MaxWireVelocity * MaxWireVelocity;
        }

        public static void SanitizeVelocity(ref Vector3 velocity, ref Vector3 angularVelocity)
        {
            if (!IsSaneVelocity(velocity, angularVelocity))
            {
                velocity = Vector3.zero;
                angularVelocity = Vector3.zero;
            }
        }

        public static int ResolvePlacementAvatar(byte ownerKind, int ownerId)
        {
            return PlayerRegistry.ToAvatarId(ownerKind, ownerId);
        }

        public static void SetPlacementIntent(InteractablePackagingBox box, bool moving, int avatarId)
        {
            if ((CoopCore.Role != CoopRole.Client && CoopCore.Role != CoopRole.Host) || box == null)
                return;
            PlacementIntents[box] = new PlacementIntent { Moving = moving, AvatarId = avatarId };
        }

        public static void ClearPlacementIntent(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            PlacementIntents.Remove(box);
            if (PlacementPhysicsDisabled.Remove(box) && !IsBaseBoxHeld(box)
                && !box.GetIsMovingObject())
                BoxLifecycle.ApplyEnabled(box, true);
        }

        /// <summary>Reconstruct every remotely-placing box from its owner's camera: keep
        /// the physics body off and glide the visible pose toward the solved placement.</summary>
        public static void TickVanillaPlacement(float dt)
        {
            if ((CoopCore.Role != CoopRole.Client && CoopCore.Role != CoopRole.Host)
                || PlacementIntents.Count == 0)
                return;
            var dead = new List<InteractablePackagingBox>();
            foreach (var pair in PlacementIntents)
            {
                var box = pair.Key;
                var intent = pair.Value;
                if (box == null)
                {
                    dead.Add(box);
                    continue;
                }
                if (!intent.Moving || intent.AvatarId < 0)
                {
                    if (PlacementPhysicsDisabled.Remove(box) && !IsBaseBoxHeld(box)
                        && !box.GetIsMovingObject())
                        BoxLifecycle.ApplyEnabled(box, true);
                    continue;
                }
                if (!box.gameObject.activeInHierarchy)
                {
                    if (PlacementPhysicsDisabled.Remove(box))
                        BoxLifecycle.ApplyEnabled(box, true);
                    dead.Add(box);
                    continue;
                }
                if (IsBaseBoxHeld(box) || box.GetIsMovingObject())
                {
                    intent.Moving = false;
                    intent.AvatarId = -1;
                    if (PlacementPhysicsDisabled.Remove(box))
                        BoxLifecycle.ApplyEnabled(box, true);
                    continue;
                }
                if (CoopCore.Instance == null
                    || !CoopCore.Instance.TryGetAvatarCamera(intent.AvatarId, out var cameraPosition, out var cameraRotation))
                {
                    // An avatar may be temporarily absent during join/relay setup or
                    // disconnect. Never leave a box permanently kinematic while waiting for
                    // that camera; it remains loose and can be picked up normally.
                    if (PlacementPhysicsDisabled.Remove(box) && !IsBaseBoxHeld(box)
                        && !box.GetIsMovingObject())
                        BoxLifecycle.ApplyEnabled(box, true);
                    continue;
                }
                if (!PlacementPhysicsDisabled.Contains(box))
                {
                    BoxLifecycle.ApplyEnabled(box, false);
                    PlacementPhysicsDisabled.Add(box);
                }
                if (box.m_Rigidbody != null && !box.m_Rigidbody.isKinematic
                    && PlacementInvariantLogged.Add(box))
                    CoopPlugin.Log.LogWarning("BoxPlacement: placement reconciler failed to make moving box kinematic");
                var solved = PlacementSolver.Solve(new PlacementCamera
                {
                    Position = cameraPosition,
                    Rotation = cameraRotation,
                }, box.m_BoxPhysicsDimension);
                if (PlacementRayLogged.Add(box))
                    BoxShared.DebugLog("q-ray", $"box={box.name} avatar={intent.AvatarId} cam={cameraPosition} camFwd={cameraRotation * Vector3.forward} solved={solved.Position} surface={solved.HasSurface} dist={Vector3.Distance(cameraPosition, solved.Position):F2} dim={box.m_BoxPhysicsDimension}");
                ApplyPhysicsPose(box,
                    Vector3.Lerp(PhysicsPosition(box), solved.Position, Mathf.Clamp01(dt * 7.5f)),
                    PhysicsRotation(box).eulerAngles.y);
            }
            for (int i = 0; i < dead.Count; i++)
            {
                PlacementIntents.Remove(dead[i]);
                PlacementPhysicsDisabled.Remove(dead[i]);
            }
        }

        private static readonly FieldInfo FiShelfWorldUI = BoxFields.ShelfWorldUi;

        /// <summary>Show/hide a packaging box's world-UI label. The label is NOT a child of
        /// the box - the game parents it to the PriceTagUISpawner singleton - so deactivating
        /// the box leaves the label floating at its last spot. Hide it explicitly while a box
        /// is held.</summary>
        public static void SetLabelVisible(InteractablePackagingBox box, bool visible)
        {
            if (box == null)
                return;
            try
            {
                if (FiShelfWorldUI?.GetValue(box) is Transform grp && grp != null)
                    grp.gameObject.SetActive(visible);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        public static bool IsRemoteMotion(InteractablePackagingBox box)
        {
            return box != null && RemoteMotions.ContainsKey(box);
        }

        public static void CancelRemoteMotion(InteractablePackagingBox box)
        {
            if (box != null)
                RemoteMotions.Remove(box);
        }

        /// <summary>Schedule a visual-only reconciliation to an authoritative host pose.
        /// Carrying clients remain predicted locally; callers must invoke this only after the
        /// host has said the box is visible/not carried.</summary>
        public static void ScheduleRemoteMotion(InteractablePackagingBox box, Vector3 position, float yaw, bool allowArc = true)
        {
            if ((CoopCore.Role != CoopRole.Client && CoopCore.Role != CoopRole.Host) || box == null)
                return;
            var from = PhysicsPosition(box);
            var fromYaw = PhysicsRotation(box).eulerAngles.y;
            float distance = Vector3.Distance(from, position);
            if (distance < 0.05f && Mathf.Abs(Mathf.DeltaAngle(fromYaw, yaw)) < 2f)
            {
                RemoteMotions.Remove(box);
                ApplyPhysicsPose(box, position, yaw);
                return;
            }
            RemoteMotions[box] = new RemoteMotion
            {
                From = from,
                To = position,
                FromYaw = fromYaw,
                ToYaw = yaw,
                Age = 0f,
                Duration = allowArc
                    ? Mathf.Clamp(0.18f + distance * 0.06f, 0.18f, 0.45f)
                    : 0.10f,
                Arc = allowArc,
            };
        }

        /// <summary>Schedule dead-reckon smoothing of a box a player is physically pushing.
        /// The target is the streamed pose; between frames it is extrapolated along the streamed
        /// velocity and the body glides toward it (see <see cref="TickRemoteMotions"/>). The body
        /// is kept kinematic for the duration - the stream is the single pose writer - and it
        /// resumes physics when the stream goes stale or a reliable Free snapshot cancels the
        /// entry. Repeated calls slide the target forward and never restart; a correction larger
        /// than <see cref="PushSnapDistance"/> is applied hard instead of dragging. Runs on both
        /// roles (peers smooth a host push; the host smooths a client push).</summary>
        public static void ScheduleRemoteMotion(InteractablePackagingBox box, Vector3 position, float yaw,
            Vector3 velocity, Vector3 angularVelocity)
        {
            if ((CoopCore.Role != CoopRole.Client && CoopCore.Role != CoopRole.Host) || box == null)
                return;
            if (!IsSanePose(position, yaw))
                return;
            SanitizeVelocity(ref velocity, ref angularVelocity);
            RemoteMotion existing;
            if (RemoteMotions.TryGetValue(box, out existing) && existing.DeadReckon)
            {
                // a push stream is already driving this box: slide the target forward, never
                // restart (restarting would snap it back to the new frame's pose)
                existing.Target = position;
                existing.TargetYaw = yaw;
                existing.Velocity = velocity;
                existing.AngularVelocity = angularVelocity;
                existing.LastUpdate = Time.time;
                return;
            }
            bool snap = Vector3.Distance(PhysicsPosition(box), position) > PushSnapDistance;
            RemoteMotions[box] = new RemoteMotion
            {
                DeadReckon = true,
                Target = position,
                TargetYaw = yaw,
                Velocity = velocity,
                AngularVelocity = angularVelocity,
                LastUpdate = Time.time,
            };
            BoxLifecycle.ApplyEnabled(box, false); // kinematic while the stream is fresh
            if (snap)
                ApplyPhysicsPose(box, position, yaw); // a large correction snaps instead of dragging
        }

        /// <summary>Advance cosmetic box motion (both roles). The arc path eases to the host's
        /// settled pose; the dead-reckon path extrapolates a pushed box's target along its
        /// streamed velocity and glides toward it. Prediction never changes authority: a
        /// reliable pose still wins, and physics resumes when a stream goes stale or is
        /// cancelled.</summary>
        public static void TickRemoteMotions(float dt)
        {
            if (RemoteMotions.Count == 0)
                return;
            float frame = Mathf.Max(0f, dt);
            var finished = new List<InteractablePackagingBox>();
            foreach (var pair in RemoteMotions)
            {
                var box = pair.Key;
                var motion = pair.Value;
                if (box == null || !box.gameObject.activeInHierarchy)
                {
                    finished.Add(box);
                    continue;
                }
                if (motion.DeadReckon)
                {
                    // the push stream went stale (the driver settled/left): let the finished
                    // path resume real physics from the streamed velocity
                    if (Time.time - motion.LastUpdate > BoxEngine.MotionLeaseTimeout)
                    {
                        finished.Add(box);
                        continue;
                    }
                    // extrapolate the pushed box's target along its streamed velocity, then
                    // glide toward it with a clamped correction (a generous max keeps lag to
                    // about a frame while smoothing the per-frame wire steps)
                    motion.Target += motion.Velocity * frame;
                    var current = PhysicsPosition(box);
                    var next = Vector3.MoveTowards(current, motion.Target, DeadReckonMaxSpeed * frame);
                    float curYaw = PhysicsRotation(box).eulerAngles.y;
                    ApplyPhysicsPose(box, next,
                        Mathf.LerpAngle(curYaw, motion.TargetYaw, Mathf.Clamp01(frame * 10f)));
                    continue;
                }
                motion.Age += frame;
                float t = Mathf.Clamp01(motion.Age / motion.Duration);
                float eased = t * t * (3f - 2f * t);
                Vector3 p = Vector3.Lerp(motion.From, motion.To, eased);
                // A small arc makes a remote throw/drop read as motion rather than a teleport,
                // while the final endpoint is still exactly the host's authoritative pose.
                if (motion.Arc)
                    p.y += Mathf.Sin(t * Mathf.PI) * Mathf.Min(0.35f, 0.1f + Vector3.Distance(motion.From, motion.To) * 0.08f);
                ApplyPhysicsPose(box, p, Mathf.LerpAngle(motion.FromYaw, motion.ToYaw, eased));
                if (t >= 1f)
                    finished.Add(box);
            }
            for (int i = 0; i < finished.Count; i++)
            {
                var box = finished[i];
                if (box != null && RemoteMotions.TryGetValue(box, out var motion))
                {
                    if (motion.DeadReckon)
                        ResumePushPhysics(box, motion.Velocity, motion.AngularVelocity);
                    else
                        ApplyPhysicsPose(box, motion.To, motion.ToYaw);
                }
                RemoteMotions.Remove(box);
            }
        }

        /// <summary>End a box's push smoothing: re-enable its physics and hand it the streamed
        /// velocity so it continues sliding from the smoothed pose instead of stopping dead.
        /// Callers that cancel a dead-reckon entry without letting the tick finish it (the
        /// lease-timeout and reliable-edge paths) MUST call this, or the box stays kinematic.</summary>
        public static void ResumePushPhysics(InteractablePackagingBox box, Vector3 velocity, Vector3 angularVelocity)
        {
            SanitizeVelocity(ref velocity, ref angularVelocity);
            BoxLifecycle.ApplyEnabled(box, true);
            var rb = box != null ? box.m_Rigidbody : null;
            if (rb != null)
            {
                rb.velocity = velocity;
                rb.angularVelocity = angularVelocity;
                rb.WakeUp();
            }
        }

        /// <summary>Returns the authoritative world pose of a packaging box. The
        /// Rigidbody is the object the game actually simulates; using the root
        /// Transform alone can preserve a stale pose while a box is parented to a
        /// warehouse slot or is being moved by physics.</summary>
        public static Vector3 PhysicsPosition(InteractablePackagingBox box)
        {
            try
            {
                if (box != null && box.m_Rigidbody != null)
                    return box.m_Rigidbody.position;
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return box != null ? box.transform.position : Vector3.zero;
        }

        public static Quaternion PhysicsRotation(InteractablePackagingBox box)
        {
            try
            {
                if (box != null && box.m_Rigidbody != null)
                    return box.m_Rigidbody.rotation;
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return box != null ? box.transform.rotation : Quaternion.identity;
        }

        /// <summary>
        /// Vanilla hold mode moves the visible root to the hand while the dynamic
        /// Rigidbody remains at its previous world pose. Commit the visible pose to
        /// the real body before vanilla applies force or enters placement mode.
        /// </summary>
        public static void AlignHeldBody(InteractablePackagingBox box)
        {
            if (box == null || box.m_Rigidbody == null)
                return;
            try
            {
                var p = box.transform.position;
                var r = box.transform.rotation;
                box.m_Rigidbody.position = p;
                box.m_Rigidbody.rotation = r;
                box.m_Rigidbody.velocity = Vector3.zero;
                box.m_Rigidbody.angularVelocity = Vector3.zero;
                box.transform.SetPositionAndRotation(p, r);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"BoxPlacement: failed to align held body before release: {e.Message}");
            }
        }

        public static bool PhysicsSettled(InteractablePackagingBox box)
        {
            try
            {
                var rb = box != null ? box.m_Rigidbody : null;
                return rb == null || rb.isKinematic || rb.IsSleeping()
                    || rb.velocity.sqrMagnitude < 0.04f;
            }
            catch (System.Exception e) { Swallow.Log(e); return true; }
        }

        /// <summary>Moves the real physics body, not merely the visual root. This
        /// keeps the next physics tick and the next snapshot on the same pose.</summary>
        public static void ApplyPhysicsPose(InteractablePackagingBox box, Vector3 position, float yaw)
        {
            if (box == null)
                return;
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            try
            {
                var rb = box.m_Rigidbody;
                if (rb != null)
                {
                    rb.position = position;
                    rb.rotation = rotation;
                    if (!rb.isKinematic)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.WakeUp();
                    }
                    // The vanilla game and its raycasts read Transform immediately, while
                    // Unity may defer copying a dynamic Rigidbody pose until FixedUpdate.
                    // Write both representations in this one commit so a box cannot exist at
                    // two clickable locations for a frame.
                    box.transform.SetPositionAndRotation(position, rotation);
                }
                else
                    box.transform.SetPositionAndRotation(position, rotation);
            }
            catch
            {
                try
                {
                    box.transform.SetPositionAndRotation(position, rotation);
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            try
            {
                ObjMoveSync.SyncTagGroup(box.transform);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        private static bool IsBaseBoxHeld(InteractablePackagingBox box)
        {
            return BoxShared.IsHeldByAnyone(box);
        }
    }
}
