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
            }
            else if (_clientClockFrozen)
            {
                FiTimerLerpSpeed.SetValue(manager, _clientClockOriginalSpeed);
                manager.m_SkyboxBlender?.Resume();
                _clientClockFrozen = false;
            }
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
            if (!_observedLightState || shop != _observedShopLight || night != _observedNightLight || sunlight != _observedSunlight)
            {
                _observedShopLight = shop;
                _observedNightLight = night;
                _observedSunlight = sunlight;
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
