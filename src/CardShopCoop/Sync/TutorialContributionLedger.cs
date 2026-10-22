using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    // Only direct player actions missing a host-side tutorial event belong here.
    // Host-simulated checkout, NPC activity, levels, purchases and unlocks stay out.
    internal sealed class TutorialContributionLedger
    {
        private readonly HashSet<int> _conditions;
        private readonly int _reversibleCondition;
        private readonly Dictionary<int, long> _last = new Dictionary<int, long>();
        internal TutorialContributionLedger(int reversibleCondition, params int[] conditions)
        {
            _reversibleCondition = reversibleCondition;
            _conditions = new HashSet<int>(conditions);
        }
        internal bool Valid(int condition, float delta) => _conditions.Contains(condition)
            && (delta == 1f || (condition == _reversibleCondition && delta == -1f));
        internal bool Accept(int peer, long sequence, int condition, float delta)
        {
            if (peer <= 0 || sequence <= 0 || !Valid(condition, delta)
                || (_last.TryGetValue(peer, out long last) && sequence <= last))
                return false;
            _last[peer] = sequence;
            return true;
        }
        internal void Clear() => _last.Clear();
    }
}
