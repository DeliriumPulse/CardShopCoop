using System;
using System.Globalization;
using System.Text;

namespace CardShopCoop.Util
{
    /// <summary>Host-side outcomes only: enough to diagnose guest item actions from the
    /// host's own connection report, without item identities or network payloads.</summary>
    internal static class ItemTransferDiagnostics
    {
        private static readonly object Gate = new object();
        private static readonly DiagnosticHistory Recent = new DiagnosticHistory();
        private static int _shelfFull;
        private static int _shelfPartial;
        private static int _shelfRejected;
        private static int _boxFull;
        private static int _boxPartial;
        private static int _boxRejected;
        private static int _boxActionRejected;

        internal static void HostShelf(int peer, long requested, long accepted, string reason)
            => Record(true, peer, requested, accepted, reason);

        internal static void HostBox(int peer, long requested, long accepted, string reason)
            => Record(false, peer, requested, accepted, reason);

        internal static void HostBoxActionRejected(int peer, string reason)
        {
            lock (Gate)
            {
                _boxActionRejected++;
                Recent.Add(Stamp() + " peer " + peer + " box possession rejected: " + reason);
            }
        }

        private static void Record(bool shelf, int peer, long requested, long accepted, string reason)
        {
            string outcome = requested != 0 && accepted == requested ? "accepted"
                : accepted != 0 ? "partial" : "rejected";
            lock (Gate)
            {
                if (shelf)
                {
                    if (outcome == "accepted")
                        _shelfFull++;
                    else if (outcome == "partial")
                        _shelfPartial++;
                    else
                        _shelfRejected++;
                }
                else
                {
                    if (outcome == "accepted")
                        _boxFull++;
                    else if (outcome == "partial")
                        _boxPartial++;
                    else
                        _boxRejected++;
                }
                Recent.Add(Stamp() + " peer " + peer + (shelf ? " shelf " : " box ")
                    + (requested < 0 ? "take" : requested > 0 ? "put" : "edit")
                    + " requested=" + Math.Abs(requested) + " accepted=" + Math.Abs(accepted)
                    + " " + outcome + " (" + reason + ")");
            }
        }

        private static string Stamp() => DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

        internal static void AppendTo(StringBuilder text)
        {
            string[] recent;
            int shelfFull, shelfPartial, shelfRejected, boxFull, boxPartial, boxRejected, boxActionRejected;
            lock (Gate)
            {
                shelfFull = _shelfFull;
                shelfPartial = _shelfPartial;
                shelfRejected = _shelfRejected;
                boxFull = _boxFull;
                boxPartial = _boxPartial;
                boxRejected = _boxRejected;
                boxActionRejected = _boxActionRejected;
                recent = Recent.Snapshot();
            }
            text.AppendLine("Guest item outcomes observed by this host process (since launch):");
            text.AppendLine("  Shelf transfers: accepted=" + shelfFull + " partial=" + shelfPartial + " rejected=" + shelfRejected);
            text.AppendLine("  Box transfers: accepted=" + boxFull + " partial=" + boxPartial + " rejected=" + boxRejected);
            text.AppendLine("  Box possession rejections: " + boxActionRejected);
            if (recent.Length == 0)
                text.AppendLine("  No guest item outcomes recorded.");
            else
                foreach (string line in recent)
                    text.AppendLine("  " + line);
        }
    }
}
