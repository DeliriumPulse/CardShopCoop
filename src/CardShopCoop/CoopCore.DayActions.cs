using CardShopCoop.Net.Messages;
using CardShopCoop.Sync;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private float _lastDayActionAt = -999f;
        private static readonly System.Reflection.FieldInfo DayLoading =
            AccessTools.Field(typeof(EndOfDayReportScreen), "m_IsLoadingNextDay");

        internal void RequestDayAction(bool advance, bool confirmedQueue = false)
        {
            if (Role != CoopRole.Client || !InGameLevel()
                || (!confirmedQueue && Time.unscaledTime - _lastDayActionAt < 0.75f))
                return;
            _lastDayActionAt = Time.unscaledTime;
            Send(1, new DayActionMessage { Day = CPlayerData.m_CurrentDay, Advance = advance, ConfirmedQueue = confirmedQueue });
        }

        private void ApplyDayAction(int connection, DayActionMessage message)
        {
            var screen = FindObjectOfType<EndOfDayReportScreen>();
            var player = FindObjectOfType<InteractionPlayerController>();
            if (screen == null || player == null)
                return;
            var action = SharedDayRules.Decide(message.Day, CPlayerData.m_CurrentDay,
                LightManager.GetHasDayEnded(), DayLoading.GetValue(screen) is true,
                EndOfDayReportScreen.IsActive(), message.Advance,
                ShelfManager.HasCustomerInCashCounterQueue(), message.ConfirmedQueue);
            if (action == SharedDayAction.Ignore)
                return;
            if (action == SharedDayAction.Advance)
            {
                screen.OnPressGoNextDay(); // Vanilla loading flag is set synchronously; duplicate presses no-op.
                return;
            }
            if (action == SharedDayAction.ResendReport)
            {
                _report.ResendOpenReport();
                return;
            }
            if (action == SharedDayAction.ConfirmQueue)
            {
                Send(connection, new DayActionReplyMessage { Day = message.Day, ConfirmQueue = true });
                return;
            }
            ReportSync.DismissDayWarning(player);
            if (!ReportSync.CanOpenSharedReport(player))
            {
                _report.DeferHostOpen(message.Day);
                Send(connection, new DayActionReplyMessage
                {
                    Day = message.Day,
                    Notice = "End of day is queued. The shared recap will open when the host closes their current menu."
                });
                return;
            }
            player.ShowGoNextDayScreen();
        }

        private void ApplyDayActionReply(DayActionReplyMessage message)
        {
            if (message.Day != CPlayerData.m_CurrentDay)
                return;
            if (!string.IsNullOrEmpty(message.Notice))
                ReportInteraction(message.Notice);
            if (!message.ConfirmQueue || ReportSync.HasClientReport)
                return;
            var player = FindObjectOfType<InteractionPlayerController>();
            if (player?.m_ConfirmGoNextDayScreen != null && player.m_ConfirmGoNextDayScreen.CanOpenScreen()
                && !player.m_ConfirmGoNextDayScreen.IsScreenOpened())
            {
                if (!ReportSync.CanOpenSharedReport(player))
                {
                    ReportInteraction("Customers are still waiting to pay. Close the current menu and press Enter again to finish the day.");
                    return;
                }
                player.m_ConfirmGoNextDayScreen.OpenScreen();
            }
        }

        private static void UpdateGuestDayPrompt(bool ended)
        {
            // Day-ended simulation events stay suppressed on guests, but their HUD must show the action.
            var ui = FindObjectOfType<GameUIScreen>();
            if (ui == null || ui.m_PressEnterGoNextDayIndicator == null)
                return;
            if (ui.m_PressEnterGoNextDayIndicator.activeSelf != ended)
            {
                ui.m_PressEnterGoNextDayIndicator.SetActive(ended);
                if (ended)
                {
                    ui.m_PressEnterGoNextDayIndicatorText.SetActive(true);
                    ui.UpdateGoNextDayText();
                }
            }
        }
    }
}
