using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Streams only the visual furniture move preview. The real object remains under
    /// ObjMoveSync's settled-pose authority; remote previews are detached meshes and therefore
    /// cannot participate in movement, collision, or index reconciliation.</summary>
    public sealed class MovePreviewSync : CoopModule
    {
        private const byte StartPhase = 0;
        private const byte Update = 1;
        private const byte Stop = 2;
        private const float SendInterval = 1f / 12f;
        private const float ExpireAfter = 1.5f;

        private sealed class RemotePreview
        {
            public int SourceId;
            public int ObjectKey;
            public bool IsBox;
            public Component SourceObject;
            public GameObject Ghost;
            public MeshRenderer Renderer;
            public Material Material;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 LocalPickupPosition;
            public Quaternion LocalPickupRotation;
            public Vector3 PickupScale;
            public bool Valid;
            public float LastReceived;
            public bool MaterialValiditySet;
            public bool LastRenderedValid;
        }

        private static readonly FieldInfo FiValid = AccessTools.Field(
            typeof(InteractableObject), "m_IsMovingObjectValidState");
        private readonly Dictionary<int, RemotePreview> _remote =
            new Dictionary<int, RemotePreview>();
        private readonly List<int> _expired = new List<int>();
        private readonly HashSet<int> _ghostUnavailable = new HashSet<int>();
        private InteractableObject _localObject;
        private int _localKey;
        private int _localSourceId;
        private float _sendTimer;
        private bool _localValid;
        private static readonly FieldInfo FiBoxedObject = AccessTools.Field(
            typeof(InteractablePackagingBox_Shelf), "m_BoxedObject");
        private ShelfManager _shelfManager;

        public override string Name => "move-preview";

        public override void ForceResend()
        {
        }

        public override void Reset()
        {
            _localObject = null;
            _localKey = 0;
            _sendTimer = 0f;
            _shelfManager = null;
            _expired.Clear();
            _ghostUnavailable.Clear();
            foreach (var preview in _remote.Values)
                DestroyPreview(preview);
            _remote.Clear();
        }

        public void RemoveSource(int sourceId)
        {
            var remove = new List<int>();
            foreach (var pair in _remote)
                if (pair.Value.SourceId == sourceId)
                    remove.Add(pair.Key);
            for (int i = 0; i < remove.Count; i++)
                RemoveRemote(remove[i]);
        }

        public void Tick(float dt)
        {
            if (CoopCore.Role == CoopRole.None)
            {
                if (_localObject != null)
                    Reset();
                return;
            }

            if (_localObject != null)
            {
                if (!_localObject || !_localObject.GetIsMovingObject())
                {
                    StopLocal();
                }
                else
                {
                    _sendTimer -= dt;
                    if (_sendTimer <= 0f)
                    {
                        _sendTimer = SendInterval;
                        SendLocal(Update, ReadValid());
                    }
                }
            }

            float now = Time.realtimeSinceStartup;
            _expired.Clear();
            foreach (var pair in _remote)
                if (now - pair.Value.LastReceived > ExpireAfter)
                    _expired.Add(pair.Key);
            for (int i = 0; i < _expired.Count; i++)
                RemoveRemote(_expired[i]);
        }

        public void BeginLocal(InteractableObject obj)
        {
            if (CoopCore.Role == CoopRole.None || obj == null || !obj.GetIsMovingObject())
                return;
            if (!TryFindKey(obj, out int key, out int kind))
                return;
            if (_localObject != null)
                StopLocal();
            _localObject = obj;
            _localKey = key;
            _localSourceId = CoopCore.Role == CoopRole.Host ? 0 : 1;
            _localValid = ReadValid();
            _sendTimer = SendInterval;
            SendLocal(StartPhase, _localValid);
        }

        public void UpdateLocalValidity(bool valid)
        {
            if (_localObject == null)
                return;
            _localValid = valid;
        }

        public void EndLocal()
        {
            if (_localObject != null)
                StopLocal();
        }

        public void ApplyRemote(MovePreviewMessage message, int sourceId)
        {
            if (message == null || CoopCore.Role == CoopRole.None)
                return;
            if (sourceId == (CoopCore.Role == CoopRole.Host ? 0 : 1))
                return;
            int key = message.ObjectKey;
            if (key == 0)
                return;

            if (message.Phase == Stop)
            {
                RemoveRemote(keyFor(sourceId, key));
                return;
            }

            int id = keyFor(sourceId, key);
            if (message.Phase == StartPhase)
                _ghostUnavailable.Remove(id);
            if (!_remote.TryGetValue(id, out var preview))
            {
                preview = new RemotePreview { SourceId = sourceId, ObjectKey = key };
                _remote[id] = preview;
            }
            preview.SourceId = sourceId;
            preview.ObjectKey = key;
            preview.IsBox = message.IsBox;
            preview.Position = message.Pos;
            preview.Rotation = message.Rot;
            preview.Valid = message.Valid;
            preview.LastReceived = Time.realtimeSinceStartup;
            EnsureGhost(preview);
            ApplyGhost(preview);
        }

        private static int keyFor(int sourceId, int objectKey)
        {
            unchecked
            {
                return (sourceId * 397) ^ objectKey;
            }
        }

        private void EnsureGhost(RemotePreview preview)
        {
            if (preview.Ghost != null || _ghostUnavailable.Contains(keyFor(preview.SourceId, preview.ObjectKey)))
                return;
            Component source = ResolvePreviewSource(preview.ObjectKey, preview.IsBox);
            var obj = source as InteractableObject;
            if (obj == null || obj.m_PickupObjectMesh == null || obj.m_PickupObjectMesh.sharedMesh == null)
            {
                _ghostUnavailable.Add(keyFor(preview.SourceId, preview.ObjectKey));
                return;
            }
            var sm = ShelfManagerInstance();
            if (sm == null || sm.m_MoveObjectPreviewRenderer == null)
            {
                _ghostUnavailable.Add(keyFor(preview.SourceId, preview.ObjectKey));
                return;
            }

            var ghost = new GameObject("CoopRemoteMovePreview");
            var filter = ghost.AddComponent<MeshFilter>();
            var renderer = ghost.AddComponent<MeshRenderer>();
            var pickupTransform = obj.m_PickupObjectMesh.transform;
            var previewModel = sm.m_MoveObjectPreviewModel;
            if (previewModel == null)
            {
                UnityEngine.Object.Destroy(ghost);
                _ghostUnavailable.Add(keyFor(preview.SourceId, preview.ObjectKey));
                return;
            }
            ghost.layer = previewModel.gameObject.layer;
            renderer.renderingLayerMask = sm.m_MoveObjectPreviewRenderer.renderingLayerMask;
            filter.sharedMesh = obj.m_PickupObjectMesh.sharedMesh;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var sourceMaterial = sm.m_MoveObjectPreviewRenderer.material;
            var material = new Material(sourceMaterial)
            {
                shaderKeywords = sourceMaterial.shaderKeywords,
                renderQueue = sourceMaterial.renderQueue,
            };
            var materials = new Material[Mathf.Max(1, filter.sharedMesh.subMeshCount)];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = material;
            renderer.sharedMaterials = materials;
            preview.SourceObject = obj;
            preview.Ghost = ghost;
            preview.Renderer = renderer;
            preview.Material = material;
            preview.LocalPickupPosition = obj.transform.InverseTransformPoint(pickupTransform.position);
            preview.LocalPickupRotation = Quaternion.Inverse(obj.transform.rotation) * pickupTransform.rotation;
            preview.PickupScale = pickupTransform.lossyScale + Vector3.one * 0.001f;
            preview.MaterialValiditySet = false;
        }

        private void ApplyGhost(RemotePreview preview)
        {
            if (preview.Ghost == null || preview.Renderer == null)
                return;
            preview.Ghost.transform.SetPositionAndRotation(
                preview.Position + preview.Rotation * preview.LocalPickupPosition,
                preview.Rotation * preview.LocalPickupRotation);
            preview.Ghost.transform.localScale = preview.PickupScale;
            var sm = ShelfManagerInstance();
            if (sm != null && preview.Material != null)
            {
                if (!preview.MaterialValiditySet || preview.LastRenderedValid != preview.Valid)
                {
                    preview.Material.SetColor("_Color",
                        preview.Valid ? sm.m_PreviewMeshValidColor : sm.m_PreviewMeshInvalidColor);
                    preview.MaterialValiditySet = true;
                    preview.LastRenderedValid = preview.Valid;
                }
            }
            preview.Ghost.SetActive(true);
        }

        private void RemoveRemote(int id)
        {
            if (_remote.TryGetValue(id, out var preview))
            {
                DestroyPreview(preview);
                _remote.Remove(id);
                _ghostUnavailable.Remove(id);
            }
        }

        private static void DestroyPreview(RemotePreview preview)
        {
            if (preview.Material != null)
                UnityEngine.Object.Destroy(preview.Material);
            if (preview.Ghost != null)
                UnityEngine.Object.Destroy(preview.Ghost);
            preview.Ghost = null;
            preview.Renderer = null;
            preview.Material = null;
        }

        private void StopLocal()
        {
            SendLocal(Stop, _localValid);
            _localObject = null;
            _localKey = 0;
        }

        private bool ReadValid()
        {
            return _localObject != null && FiValid != null &&
                (bool)FiValid.GetValue(_localObject);
        }

        private void SendLocal(byte phase, bool valid)
        {
            if (_localObject == null || _localKey == 0)
                return;
            var message = new MovePreviewMessage
            {
                Phase = phase,
                ObjectKey = _localKey,
                SourceId = _localSourceId,
                IsBox = _localObject is InteractablePackagingBox_Shelf,
                Pos = _localObject.transform.position,
                Rot = _localObject.transform.rotation,
                Valid = valid,
            };
            CoopCore.Instance?.SendMovePreview(message);
        }

        private ShelfManager ShelfManagerInstance()
        {
            if (_shelfManager == null)
                _shelfManager = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _shelfManager;
        }

        private static bool TryFindKey(InteractableObject obj, out int key, out int kind)
        {
            key = 0;
            kind = -1;
            InteractableObject identityObject = obj;
            if (obj is InteractablePackagingBox_Shelf box)
            {
                identityObject = FiBoxedObject?.GetValue(box) as InteractableObject;
                if (identityObject == null)
                    return false;
            }
            var sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            if (sm == null)
                return false;
            for (int i = 0; i < PopulationSync.KindCount; i++)
            {
                IList list = PopulationSync.GetList(sm, i);
                if (list == null)
                    continue;
                for (int j = 0; j < list.Count; j++)
                {
                    if (!ReferenceEquals(list[j], identityObject))
                        continue;
                    if (!PlacedObjectIdentity.TryMakeObjectKey(i, identityObject, out key))
                        return false;
                    kind = i;
                    return true;
                }
            }
            return false;
        }

        private static Component ResolvePreviewSource(int objectKey, bool isBox)
        {
            var identity = ObjMoveSync.ResolveObjectByKey(objectKey) as InteractableObject;
            if (identity == null)
                return null;
            if (!isBox)
                return identity;
            var boxes = UnityEngine.Object.FindObjectsOfType<InteractablePackagingBox_Shelf>();
            for (int i = 0; i < boxes.Length; i++)
            {
                var boxed = FiBoxedObject?.GetValue(boxes[i]) as InteractableObject;
                if (ReferenceEquals(boxed, identity))
                    return boxes[i];
            }
            return identity;
        }
    }
}
