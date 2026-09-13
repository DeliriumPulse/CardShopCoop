using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The set of boxes the LOCAL player is physically pushing.
    ///
    /// The player body is a CMF capsule with a kinematic Rigidbody, so Unity does NOT raise
    /// collision callbacks on the player side. The dynamic BOX is the side the physics engine
    /// reliably reports contacts on, so every synced box carries a <see cref="BoxContactProbe"/>
    /// that forwards real contacts with the local player body here via
    /// <see cref="NotifyContact"/>. A box stays pushed while contacts keep arriving;
    /// <see cref="Tick"/> releases it once contact goes stale and it comes to rest. Remote
    /// avatars run with physics/colliders disabled, so each machine only ever sees its own
    /// player's pushes.
    ///
    /// The engine turns <see cref="Pushed"/> into a transient motion stream
    /// (<see cref="BoxEngine.PushTick"/>), which is what actually moves the box on the peers.
    /// </summary>
    public sealed class BoxPushProbe : MonoBehaviour
    {
        // A body can only meaningfully drive a handful of boxes at once; past the cap a new
        // box is ignored until an existing one drops out.
        private const int MaxPushed = 8;

        /// <summary>The active probe on the local player body; box contact receivers call in.</summary>
        internal static BoxPushProbe Active;

        private Rigidbody _body;
        private readonly Dictionary<InteractablePackagingBox, float> _lastContact
            = new Dictionary<InteractablePackagingBox, float>();
        private readonly HashSet<InteractablePackagingBox> _pushed
            = new HashSet<InteractablePackagingBox>();
        // Reused per-frame prune list so Tick allocates nothing.
        private readonly List<InteractablePackagingBox> _scratch
            = new List<InteractablePackagingBox>();

        /// <summary>The boxes the local player is currently pushing.</summary>
        public IReadOnlyCollection<InteractablePackagingBox> Pushed
        {
            get
            {
                return _pushed;
            }
        }

        /// <summary>Bind the probe to the player's walking body and make it the active target
        /// for box-side contact reports.</summary>
        public void Init(Rigidbody body)
        {
            if (body == null)
                throw new ArgumentNullException("body");
            _body = body;
            Active = this;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(Active, this))
                Active = null;
        }

        /// <summary>A box's collision receiver reports a real contact with the local player.</summary>
        internal static void NotifyContact(InteractablePackagingBox box)
        {
            Active?.NoteBox(box);
        }

        private void NoteBox(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            var engine = CoopCore.Instance?.Boxes;
            if (engine == null || !engine.CanLocallyPush(box))
                return;
            _lastContact[box] = Time.time;
            if (!_pushed.Contains(box) && _pushed.Count >= MaxPushed)
                return;
            if (_pushed.Add(box))
                BoxShared.DebugLog("push-add", $"name={box.name} role={CoopCore.Role} count={_pushed.Count}", box.GetInstanceID(), 0.5f);
        }

        /// <summary>Per-frame maintenance: drop boxes that are destroyed, or that lost contact
        /// and are now at rest. A box that lost contact but is still sliding stays in the set
        /// (its slide-to-rest is carried by the motion stream), so only a genuinely resting box
        /// is released. Runs over a reused scratch list - no per-frame allocation.</summary>
        public void Tick(float dt)
        {
            // The player's body went away (scene transition): release everything we hold.
            if (_body == null)
            {
                _pushed.Clear();
                _lastContact.Clear();
                return;
            }
            if (_pushed.Count == 0)
                return;
            float settleSq = BoxEngine.MotionSettleSpeed * BoxEngine.MotionSettleSpeed;
            _scratch.Clear();
            foreach (var box in _pushed)
            {
                if (box == null) // Unity fake-null: the box was destroyed
                {
                    _scratch.Add(box);
                    continue;
                }
                // Still in contact (fresh) - keep it regardless of speed.
                if (_lastContact.TryGetValue(box, out var last)
                    && Time.time - last <= BoxEngine.PushHysteresis)
                    continue;
                var rb = box.m_Rigidbody;
                bool resting = rb == null || rb.isKinematic || rb.IsSleeping()
                    || rb.velocity.sqrMagnitude < settleSq;
                if (resting)
                    _scratch.Add(box);
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                var box = _scratch[i];
                _pushed.Remove(box);
                _lastContact.Remove(box);
            }
        }
    }
}
