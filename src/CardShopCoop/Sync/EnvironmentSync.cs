using System.Reflection;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>The displayed clock alone cannot advance the frozen guest sky and sun.</summary>
    internal static class EnvironmentSync
    {
        private static readonly FieldInfo TimeOfDay = AccessTools.Field(typeof(LightManager), "m_TImeOfDayIndex");
        private static readonly FieldInfo SunLerp = AccessTools.Field(typeof(LightManager), "m_SunlightLerpTimer");
        private static readonly FieldInfo Lerping = AccessTools.Field(typeof(LightManager), "m_IsLerpingSunIntensity");
        private static readonly FieldInfo StartBrightness = AccessTools.Field(typeof(LightManager), "m_LerpStartBrightness");
        private static readonly FieldInfo SunRotation = AccessTools.Field(typeof(LightManager), "m_SunlightRotationLerpTimer");
        private static readonly FieldInfo SkyMaterial = AccessTools.Field(typeof(SkyboxBlender), "skyboxBlenderMaterial");
        private static readonly MethodInfo Brightness = AccessTools.Method(typeof(LightManager), "EvaluateWorldUIBrightness");
        private static readonly MethodInfo SetSky = AccessTools.Method(typeof(SkyboxBlender), "SetSkyBoxes");

        private static int TextureIndex(SkyboxBlender sky, Texture texture)
        {
            if (sky?.skyboxMaterials == null || texture == null)
                return -1;
            for (int i = 0; i < sky.skyboxMaterials.Length; i++)
                if (sky.skyboxMaterials[i] != null && sky.skyboxMaterials[i].GetTexture("_Tex") == texture)
                    return i;
            return -1;
        }

        public static EnvironmentState Capture(LightManager manager)
        {
            if (manager == null)
                return null;
            var state = new EnvironmentState
            {
                TimeOfDay = (int)TimeOfDay.GetValue(manager),
                SunLerp = (float)SunLerp.GetValue(manager),
                Lerping = (bool)Lerping.GetValue(manager),
                StartBrightness = (float)StartBrightness.GetValue(manager),
                SunRotation = (float)SunRotation.GetValue(manager),
                SunOn = manager.m_SunlightGrp.activeSelf,
                NightOn = manager.m_NightlightGrp.activeSelf,
                ShopOn = manager.m_ShoplightGrp.activeSelf,
                SunIntensity = new float[manager.m_SunlightList.Count],
                SunShadows = new int[manager.m_SunlightList.Count],
                SunShadowStrength = new float[manager.m_SunlightList.Count],
            };
            for (int i = 0; i < state.SunIntensity.Length; i++)
            {
                var light = manager.m_SunlightList[i];
                if (light == null)
                    continue;
                state.SunIntensity[i] = light.intensity;
                state.SunShadows[i] = (int)light.shadows;
                state.SunShadowStrength[i] = light.shadowStrength;
            }
            var sky = manager.m_SkyboxBlender;
            var material = RenderSettings.skybox;
            if (sky != null && material != null && material.HasProperty("_Tex") && material.HasProperty("_Tex2"))
            {
                state.SkyFrom = TextureIndex(sky, material.GetTexture("_Tex"));
                state.SkyTo = TextureIndex(sky, material.GetTexture("_Tex2"));
                state.SkyBlend = material.GetFloat("_BlendCubemaps");
                state.SkyRotation = material.GetFloat("_Rotation");
            }
            return state;
        }

        public static void Apply(LightManager manager, EnvironmentState state)
        {
            if (manager == null || state == null)
                return;
            TimeOfDay.SetValue(manager, Mathf.Clamp(state.TimeOfDay, 0, 4));
            SunLerp.SetValue(manager, Mathf.Clamp01(state.SunLerp));
            Lerping.SetValue(manager, state.Lerping);
            StartBrightness.SetValue(manager, Mathf.Clamp01(state.StartBrightness));
            SunRotation.SetValue(manager, Mathf.Clamp01(state.SunRotation));
            manager.m_Sunlight.rotation = Quaternion.Lerp(manager.m_SunlightLerpStartPos.rotation,
                manager.m_SunlightLerpEndPos.rotation, state.SunRotation);
            manager.m_SunlightGrp.SetActive(state.SunOn);
            manager.m_NightlightGrp.SetActive(state.NightOn);
            manager.m_ShoplightGrp.SetActive(state.ShopOn);
            for (int i = 0; i < manager.m_SunlightList.Count && i < (state.SunIntensity?.Length ?? 0); i++)
            {
                var light = manager.m_SunlightList[i];
                if (light == null)
                    continue;
                light.intensity = Mathf.Clamp(state.SunIntensity[i], 0f, 100f);
                if (i < (state.SunShadows?.Length ?? 0))
                    light.shadows = (LightShadows)Mathf.Clamp(state.SunShadows[i], 0, 2);
                if (i < (state.SunShadowStrength?.Length ?? 0))
                    light.shadowStrength = Mathf.Clamp01(state.SunShadowStrength[i]);
            }
            Brightness.Invoke(manager, null);
            var sky = manager.m_SkyboxBlender;
            if (sky?.skyboxMaterials == null || state.SkyFrom < 0 || state.SkyTo < 0
                || state.SkyFrom >= sky.skyboxMaterials.Length || state.SkyTo >= sky.skyboxMaterials.Length)
                return;
            sky.Stop();
            SetSky.Invoke(sky, new object[] { true, state.SkyFrom, true, state.SkyTo, true });
            if (SkyMaterial.GetValue(sky) is Material material)
            {
                material.SetFloat("_BlendCubemaps", Mathf.Clamp01(state.SkyBlend));
                material.SetFloat("_Rotation", state.SkyRotation);
                sky.blendValue = Mathf.Clamp01(state.SkyBlend);
            }
            sky.UpdateLightingAndReflections(true);
        }
    }
}
