using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    internal static class BoxPhysicsStability
    {
        private struct Original
        {
            public CollisionDetectionMode Detection;
            public int Iterations;
            public float Depenetration;
            public float AngularSpeed;
        }
        private static readonly Dictionary<Rigidbody, Original> Originals = new Dictionary<Rigidbody, Original>();

        public static void Configure(InteractablePackagingBox __instance)
        {
            if (CoopCore.Role == CoopRole.None || __instance == null || __instance.m_Rigidbody == null)
                return;
            var body = __instance.m_Rigidbody;
            if (Originals.ContainsKey(body))
                return;
            Originals[body] = new Original
            {
                Detection = body.collisionDetectionMode,
                Iterations = body.solverIterations,
                Depenetration = body.maxDepenetrationVelocity,
                AngularSpeed = body.maxAngularVelocity
            };
            // Supports the kinematic/dynamic changes used for remote smoothing.
            // Keep collision, gravity and throwing enabled while reducing explosive contacts.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.solverIterations = Mathf.Max(body.solverIterations, 8);
            body.maxDepenetrationVelocity = Mathf.Min(body.maxDepenetrationVelocity, 2f);
            body.maxAngularVelocity = Mathf.Min(body.maxAngularVelocity, 3f);
        }

        internal static void Reset()
        {
            foreach (var pair in Originals)
            {
                if (pair.Key == null)
                    continue;
                pair.Key.collisionDetectionMode = pair.Value.Detection;
                pair.Key.solverIterations = pair.Value.Iterations;
                pair.Key.maxDepenetrationVelocity = pair.Value.Depenetration;
                pair.Key.maxAngularVelocity = pair.Value.AngularSpeed;
            }
            Originals.Clear();
        }
    }
}
