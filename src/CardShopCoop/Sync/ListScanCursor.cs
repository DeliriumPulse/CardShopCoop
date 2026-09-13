using System;
using System.Collections;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Round-robin time-slicer over a fixed set of <see cref="IList"/> groups. A scan visits
    /// up to <see cref="Budget"/> items per call and remembers its position, so an
    /// O(all-objects) walk costs a bounded slice per frame instead of one hitch. Call
    /// <see cref="Reset"/> before each new pass and check <see cref="Done"/> to know when the
    /// pass finished.
    /// </summary>
    internal sealed class ListScanCursor
    {
        public int Budget = 24;

        public bool Done
        {
            get; private set;
        }

        private int _group;
        private int _index;

        public void Reset()
        {
            _group = 0;
            _index = 0;
            Done = false;
        }

        /// <summary>Advance the scan by up to <see cref="Budget"/> items across
        /// <paramref name="groups"/>, calling <paramref name="visit"/>(item, groupIndex,
        /// itemIndex). Returns true when the whole set has been visited.</summary>
        public bool Scan(IList[] groups, Action<object, int, int> visit)
        {
            Done = false;
            if (groups == null)
            {
                Done = true;
                return true;
            }
            int spent = 0;
            int guard = 0;
            while (_group < groups.Length && spent < Budget && guard++ < 512)
            {
                var list = groups[_group];
                if (list == null)
                {
                    _group++;
                    _index = 0;
                    continue;
                }
                while (_index < list.Count && spent < Budget)
                {
                    visit(list[_index], _group, _index);
                    _index++;
                    spent++;
                }
                if (_index >= list.Count)
                {
                    _group++;
                    _index = 0;
                }
            }
            Done = _group >= groups.Length;
            return Done;
        }
    }
}
