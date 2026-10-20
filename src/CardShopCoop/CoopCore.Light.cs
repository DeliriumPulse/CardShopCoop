using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        /// <summary>Clients must not run LightManager's local clock. Its Update method
        /// advances time every frame and can independently reach night between host
        /// correction packets.</summary>
        public void EnforceClientClock(LightManager manager)
        {
            if (manager == null || FiTimerLerpSpeed == null)
                return;

            if (Role == CoopRole.Client)
            {
                if (!_clientClockFrozen)
                {
                    object current = FiTimerLerpSpeed.GetValue(manager);
                    if (current is float speed)
                        _clientClockOriginalSpeed = speed;
                    CoopPlugin.Log.LogInfo("client lighting clock frozen; time is host-authoritative");
                    _clientClockFrozen = true;
                }
                FiTimerLerpSpeed.SetValue(manager, 0f);
                AdvanceClientClock(manager);
            }
            else
            {
                _hostClockMinutes = -1f;
                _clientLightPhase = -1;
                if (_clientClockFrozen)
                {
                    FiTimerLerpSpeed.SetValue(manager, _clientClockOriginalSpeed);
                    _clientClockFrozen = false;
                }
            }
        }

        // How far (game minutes) the client may run past the host's last DayTime update. The
        // host sends one every 2 s, at 1 game minute per second.
        private const float MaxClientClockLead = 2.5f;
        // The client never reaches 21:00 on its own; ending the day is the host's call.
        private const float LastClientClockMinute = 21f * 60f - 0.001f;

        /// <summary>Client: run the frozen clock forward between host updates so the HUD
        /// counts minute by minute instead of jumping 2 minutes every 2 seconds. Uses the same
        /// gates as vanilla LightManager.Update, never goes backward, and sets the hour itself so
        /// EvaluateTimeClock's rollover branch (which also ages grading jobs) never runs here.</summary>
        private void AdvanceClientClock(LightManager manager)
        {
            if (_hostClockMinutes < 0f || !CPlayerData.m_IsShopOnceOpen
                || _clientDayResetPending || _clientDayResetInFlight)
                return;
            try
            {
                if (FiHasDayEnded?.GetValue(manager) is bool ended && ended)
                    return;
                float target = _hostClockMinutes
                    + (Time.unscaledTime - _hostClockReceivedAt) * _clientClockOriginalSpeed;
                target = Mathf.Min(target, _hostClockMinutes + MaxClientClockLead, LastClientClockMinute);
                if (target > ClientClockMinutes(manager))
                    SetClientClock(manager, target, forceEvaluate: false);
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        /// <summary>Client: apply the host's clock from DayTime or LightState. Snaps to the host
        /// unless the client is only slightly ahead from running its own clock, so the HUD never
        /// ticks backward; it then waits for the host within MaxClientClockLead.</summary>
        private void ApplyHostClock(LightManager manager, int hour, float minFloat, bool dayChanged)
        {
            float host = hour * 60f + minFloat;
            _hostClockMinutes = host;
            _hostClockReceivedAt = Time.unscaledTime;
            float ahead = ClientClockMinutes(manager) - host;
            if (!dayChanged && hour < 21 && ahead > 0f && ahead <= MaxClientClockLead + 0.5f)
                return;
            SetClientClock(manager, host, forceEvaluate: dayChanged);
        }

        private static float ClientClockMinutes(LightManager manager)
        {
            int hour = FiTimeHour?.GetValue(manager) is int h ? h : 0;
            float minFloat = FiTimeMinFloat?.GetValue(manager) is float m ? m : 0f;
            return hour * 60f + minFloat;
        }

        private static void SetClientClock(LightManager manager, float minutes, bool forceEvaluate)
        {
            int hour = Mathf.FloorToInt(minutes / 60f);
            float minFloat = minutes - hour * 60f;
            int min = Mathf.Min(Mathf.FloorToInt(minFloat), 59);
            bool changed = !(FiTimeHour?.GetValue(manager) is int oldHour && oldHour == hour)
                || !(FiTimeMin?.GetValue(manager) is int oldMin && oldMin == min);
            FiTimeHour?.SetValue(manager, hour);
            FiTimeMin?.SetValue(manager, min);
            FiTimeMinFloat?.SetValue(manager, minFloat);
            if (changed || forceEvaluate)
                MiEvaluateTimeClock?.Invoke(manager, null);
        }

        private void MarkClientDayResetPending()
        {
            _clientDayResetPending = true;
            _clientDayResetDeadline = Time.time + 10f;
        }

        private void TryStartClientDayReset()
        {
            if (Role != CoopRole.Client)
                return;

            if (_clientDayResetDeadline > 0f && Time.time >= _clientDayResetDeadline
                && (_clientDayResetPending || _clientDayResetInFlight))
            {
                _clientDayResetPending = false;
                _clientDayResetDeadline = 0f;
                _clientDayResetInFlight = false;
                Patches.GamePatches.AllowNextDayStarted = false;
                CoopPlugin.Log.LogWarning("client day reset timed out; continuing with host clock/light correction");
                return;
            }

            if (!_clientDayResetPending)
                return;
            if (_clientDayResetInFlight)
                return;

            if (!InGameLevel())
                return;
            if (MiDayReset == null)
                return;
            if (_lightManager == null)
                _lightManager = FindObjectOfType<LightManager>();
            if (_lightManager == null)
                return;

            EnforceClientClock(_lightManager);

            // Close client-only UI/state before the environment coroutine starts. These
            // are intentionally best-effort; the reset itself must still be attempted.
            try
            {
                Sync.ReportSync.CloseClientReport();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("day change: closing stale report: " + e.Message); }
            try
            {
                Sync.RegisterSync.ForceExitManned();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("day change: force-exit register: " + e.Message); }

            _clientDayResetPending = false;
            _clientDayResetInFlight = true;
            _clientDayResetDeadline = Time.time + 10f;
            Patches.GamePatches.AllowNextDayStarted = true;
            _lightManager.StartCoroutine(ClientDayResetRoutine());
        }

        private IEnumerator ClientDayResetRoutine()
        {
            try
            {
                yield return (IEnumerator)MiDayReset.Invoke(_lightManager, null);
            }
            finally
            {
                _clientLightPhase = -1; // the reset restored the morning lighting itself
                _clientDayResetInFlight = false;
                _clientDayResetDeadline = 0f;
                Patches.GamePatches.AllowNextDayStarted = false;
            }
        }

        /// <summary>Causes the next host tick to immediately echo the authoritative lighting
        /// state. Used after a forwarded switch request so clients do not wait for the normal
        /// lighting heartbeat.</summary>
        public void ForceLightResend()
        {
            if (Role == CoopRole.Host)
            {
                _lightForceResend = true;
                _lightSyncTimer = 0f;
            }
        }

        /// <summary>Called by LightManager hooks after vanilla or a mod has refreshed its
        /// lighting data. Direct SetActive changes have no setter to hook, so compare the
        /// actual groups and request an immediate authoritative echo only when they change.</summary>
        public void ObserveHostLightState(LightManager manager)
        {
            if (Role != CoopRole.Host || manager == null)
                return;
            bool shop = manager.m_ShoplightGrp != null && manager.m_ShoplightGrp.activeSelf;
            bool night = manager.m_NightlightGrp != null && manager.m_NightlightGrp.activeSelf;
            bool sunlight = manager.m_SunlightGrp != null && manager.m_SunlightGrp.activeSelf;
            // a new time-of-day phase starts the evening/night lighting, which clients only
            // pick up from a LightState, so send one now instead of on the 30 s heartbeat
            int phase = FiTimeOfDayIdx?.GetValue(manager) is int idx ? idx : -1;
            if (!_observedLightState || shop != _observedShopLight || night != _observedNightLight
                || sunlight != _observedSunlight || phase != _observedPhase)
            {
                _observedShopLight = shop;
                _observedNightLight = night;
                _observedSunlight = sunlight;
                _observedPhase = phase;
                _observedLightState = true;
                ForceLightResend();
            }
        }

        private static void ApplyClientLightSwitchModels(bool isOn)
        {
            try
            {
                var switches = FindObjectsOfType<InteractableLightSwitch>(true);
                for (int i = 0; i < switches.Length; i++)
                {
                    var sw = switches[i];
                    if (sw == null)
                        continue;
                    if (sw.m_SwitchOnModel != null)
                        sw.m_SwitchOnModel.SetActive(isOn);
                    if (sw.m_SwitchOffModel != null)
                        sw.m_SwitchOffModel.SetActive(!isOn);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("light-switch visual apply: " + e.Message); }
        }

        // ------------------------------------------------ message handling

    }
}
