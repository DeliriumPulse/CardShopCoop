using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Rides on a synced box (the dynamic rigidbody). Unity raises collision callbacks on the
    /// dynamic side of a kinematic-vs-dynamic pair, which is why the player-side probe never
    /// sees the player pushing a box: the box does. This forwards real contacts with the local
    /// player body to <see cref="BoxPushProbe"/> so the push can be streamed to the peers.
    /// </summary>
    public sealed class BoxContactProbe : MonoBehaviour
    {
        private InteractablePackagingBox _box;

        public void Init(InteractablePackagingBox box)
        {
            _box = box;
        }

        private void OnCollisionEnter(Collision c)
        {
            Note(c);
        }

        private void OnCollisionStay(Collision c)
        {
            Note(c);
        }

        // No OnCollisionExit handling on purpose: contact only refreshes the pushed lease.
        // The probe's per-frame prune releases the box once contact goes stale AND it comes to
        // rest, so a box that keeps sliding after the push ends is still streamed to the peers.

        private void Note(Collision c)
        {
            if (_box == null || c == null || c.collider == null)
                return;
            if (!CoopCore.IsLocalPlayerCollider(c.collider))
                return;
            BoxPushProbe.NotifyContact(_box);
        }
    }
}
