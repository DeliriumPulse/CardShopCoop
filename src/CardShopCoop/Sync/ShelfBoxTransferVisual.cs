using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>A cosmetic flight after a successful transfer. Inventory belongs to the
    /// snapshots; this object has no Item, collider, or inventory registration.</summary>
    public sealed class ShelfBoxTransferVisual : MonoBehaviour
    {
        private Transform _box;
        private Vector3 _from, _localTarget;
        private Quaternion _fromRotation;
        private float _elapsed;

        public static void Play(InteractablePackagingBox box, ShelfBoxCueMessage message)
        {
            if (box == null || !box.gameObject.activeInHierarchy || message.ItemType == EItemType.None)
                return;
            SoundManager.GenericPop(1f, 0.9f);
            var data = InventoryBase.GetItemMeshData(message.ItemType);
            if (data?.mesh == null)
                return;
            var visual = new GameObject("Coop shelf to box item");
            visual.transform.position = message.From;
            visual.transform.rotation = message.Rotation;
            visual.transform.localScale = message.Scale;
            visual.AddComponent<MeshFilter>().sharedMesh = data.mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = data.materialList != null && data.materialList.Count > 0
                ? data.materialList.ToArray()
                : data.meshSecondary == null && data.materialSecondary != null
                    ? new[] { data.material, data.materialSecondary } : new[] { data.material };
            if (data.meshSecondary != null)
            {
                var secondary = new GameObject("Secondary mesh");
                secondary.transform.SetParent(visual.transform, false);
                secondary.AddComponent<MeshFilter>().sharedMesh = data.meshSecondary;
                secondary.AddComponent<MeshRenderer>().sharedMaterial = data.materialSecondary;
            }
            var flight = visual.AddComponent<ShelfBoxTransferVisual>();
            flight._box = box.transform;
            flight._from = message.From;
            flight._fromRotation = message.Rotation;
            flight._localTarget = message.DestinationLocal;
            Destroy(visual, 1f); // Also cleans up if a scene or UI disables this component.
        }

        private void Update()
        {
            if (_box == null || !_box.gameObject.activeInHierarchy)
            {
                Destroy(gameObject);
                return;
            }
            _elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsed / 0.33f);
            transform.position = Vector3.Lerp(_from, _box.TransformPoint(_localTarget), t)
                + Vector3.up * (Mathf.Sin(t * Mathf.PI) * 0.1f);
            transform.rotation = Quaternion.Slerp(_fromRotation, _box.rotation, t);
            if (t >= 1f)
                Destroy(gameObject);
        }
    }
}
