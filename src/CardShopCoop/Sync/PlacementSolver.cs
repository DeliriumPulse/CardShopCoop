using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    public struct PlacementCamera
    {
        public Vector3 Position;
        public Quaternion Rotation;
    }

    public struct PlacementResult
    {
        public Vector3 Position;
        public bool HasSurface;
    }

    /// <summary>Single implementation of vanilla delivery-box Q-mode ray placement.
    /// It intentionally remains camera-based; no moving pose is sent over the network.</summary>
    public static class PlacementSolver
    {
        private static readonly FieldInfo FiRayDistance =
            AccessTools.Field(typeof(InteractionPlayerController), "m_RayDistance");
        private static bool _resolvedRayDistance;
        private static bool _fallbackLogged;
        private static float _rayDistance = 5f;

        public static float RayDistance
        {
            get
            {
                ResolveRayDistance();
                return _rayDistance;
            }
        }

        private static void ResolveRayDistance()
        {
            if (_resolvedRayDistance)
                return;
            // m_Instance is a vestigial field on InteractionPlayerController that is never
            // assigned (the class extends CSingleton<T>), so the old lookup always failed and
            // permanently latched the 5m fallback. Use the real singleton, and only latch on a
            // successful read so a not-yet-spawned controller retries instead of freezing.
            var controller = CSingleton<InteractionPlayerController>.Instance;
            if (controller != null && FiRayDistance != null)
            {
                _rayDistance = (float)FiRayDistance.GetValue(controller);
                _resolvedRayDistance = true;
                CoopPlugin.Log.LogInfo($"PlacementSolver: m_RayDistance resolved = {_rayDistance}");
                return;
            }
            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                CoopPlugin.Log.LogWarning("PlacementSolver: m_RayDistance unavailable; using 5m fallback (will retry)");
            }
        }

        public static PlacementResult Solve(PlacementCamera camera, Vector3 boxPhysicsDimension,
            bool verticalMode = false)
        {
            ResolveRayDistance();
            int mask = verticalMode
                ? LayerMask.GetMask("DecorationBlocker", "DecorationBlockerRaycast")
                : LayerMask.GetMask("ShopModel", "Ground", "Glass", "Obstacles", "Physics");
            var result = new PlacementResult();
            if (Physics.Raycast(new Ray(camera.Position, camera.Rotation * Vector3.forward),
                out var hit, _rayDistance, mask))
            {
                float offset = Mathf.Lerp(boxPhysicsDimension.x, boxPhysicsDimension.y, hit.normal.y);
                result.Position = hit.point + hit.normal * offset;
                result.HasSurface = true;
            }
            else
            {
                result.Position = camera.Position + camera.Rotation * Vector3.forward
                    - (camera.Rotation * Vector3.up) * 0.1f;
                result.HasSurface = false;
            }
            if (result.Position.y < 0f)
                result.Position.y = 0f;
            return result;
        }
    }
}
