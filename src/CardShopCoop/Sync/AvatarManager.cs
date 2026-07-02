using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Renders remote players by cloning the game's own customer prefab. The clone is
    /// dressed with the game's real wardrobe pipeline (Customer.RandomizeCharacterMesh ->
    /// CharacterCustomization.Initialize) and then stripped of all AI/physics so it's a
    /// pure network puppet. Gender and outfit follow from the player's name, the Animator
    /// keeps the game's own walk cycle ("MoveSpeed"), and a simple box prop + the
    /// "IsHoldingBox" pose show when the remote player is carrying something.
    /// </summary>
    public class AvatarManager
    {
        private class RemoteAvatar
        {
            public GameObject Go;
            public Animator Anim;
            public bool HasMoveSpeed;
            public bool HasHoldingBox;
            public TextMesh NameTag;
            public TextMesh EmoteTag;
            public GameObject HoldProp;
            public string Name = "Player";
            public Vector3 TargetPos;
            public float TargetYaw;
            public float NetSpeed;
            public byte HoldState;
            public List<int> HoldTypes;         // actual EItemTypes being carried
            public List<CardData> HoldCards;    // actual cards fanned in hand
            public string HeldSig = "";          // what the spawned item props currently show
            public string CardSig = "";          // what the spawned card visuals currently show
            public readonly List<Item> HeldItems = new List<Item>();
            public readonly List<InteractableCard3d> HeldCards3d = new List<InteractableCard3d>();
            public GameObject BinderProp;
            public Item PackProp;
            public float PackTimer;
            public float EmoteTimer;
            public bool EverPositioned;
            public bool HasState;
        }

        private readonly Dictionary<int, RemoteAvatar> _avatars = new Dictionary<int, RemoteAvatar>();
        private bool _loggedAnimParams;

        public void SetName(int connId, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (_avatars.TryGetValue(connId, out var av))
            {
                if (av.Name == name) return;
                av.Name = name;
                if (av.NameTag != null) av.NameTag.text = name;
                if (av.Go != null) av.Go.name = "CoopAvatar_" + name;
            }
            else
            {
                _avatars[connId] = new RemoteAvatar { Name = name };
            }
        }

        public void UpdateState(int connId, Vector3 pos, float yaw, float speed, byte holdState,
            List<int> holdTypes = null, List<CardData> holdCards = null)
        {
            if (!_avatars.TryGetValue(connId, out var av))
            {
                av = new RemoteAvatar();
                _avatars[connId] = av;
            }
            av.TargetPos = pos;
            av.TargetYaw = yaw;
            av.NetSpeed = speed;
            av.HoldState = holdState;
            av.HoldTypes = holdTypes;
            av.HoldCards = holdCards;
            av.HasState = true;
            if (!av.EverPositioned && av.Go != null)
            {
                av.Go.transform.position = pos;
                av.EverPositioned = true;
            }
        }

        public void ShowEmote(int connId)
        {
            ShowTag(connId, "\\o/  hi!", 2.5f);
        }

        public void ShowTag(int connId, string text, float seconds)
        {
            if (_avatars.TryGetValue(connId, out var av) && av.EmoteTag != null)
            {
                av.EmoteTag.text = text;
                av.EmoteTimer = seconds;
            }
        }

        public void Remove(int connId)
        {
            if (_avatars.TryGetValue(connId, out var av))
            {
                ReleaseHeld(av); // pooled items must go back before their parent dies
                if (av.Go != null) Object.Destroy(av.Go);
                _avatars.Remove(connId);
            }
        }

        public void Clear()
        {
            foreach (var av in _avatars.Values)
            {
                ReleaseHeld(av);
                if (av.Go != null) Object.Destroy(av.Go);
            }
            _avatars.Clear();
        }

        private static void ReleaseHeld(RemoteAvatar av)
        {
            foreach (var item in av.HeldItems)
                if (item != null)
                {
                    try { ItemSpawnManager.DisableItem(item); } catch { }
                }
            av.HeldItems.Clear();
            av.HeldSig = "";
            ReleaseCards(av);
            if (av.PackProp != null)
            {
                try { ItemSpawnManager.DisableItem(av.PackProp); } catch { }
                av.PackProp = null;
            }
            if (av.BinderProp != null) { Object.Destroy(av.BinderProp); av.BinderProp = null; }
        }

        private static void ReleaseCards(RemoteAvatar av)
        {
            foreach (var c in av.HeldCards3d)
                if (c != null)
                {
                    try { c.OnDestroyed(); } catch { } // game's own card despawn path
                }
            av.HeldCards3d.Clear();
            av.CardSig = "";
        }

        /// <summary>A pack-opening happened: put the actual pack in the avatar's hands
        /// briefly and play the grab motion.</summary>
        public void ShowPackOpen(int connId, int packIndex)
        {
            if (!_avatars.TryGetValue(connId, out var av) || av.Go == null) return;
            av.PackTimer = 4f;
            try { if (av.Anim != null) av.Anim.SetTrigger("GrabItem"); } catch { }
            if (av.PackProp == null && packIndex >= 0)
            {
                try
                {
                    var meshData = InventoryBase.GetItemMeshData((EItemType)packIndex);
                    if (meshData != null)
                    {
                        var item = ItemSpawnManager.GetItem(av.Go.transform);
                        item.SetMesh(meshData.mesh, meshData.material, (EItemType)packIndex,
                            meshData.meshSecondary, meshData.materialSecondary, meshData.materialList);
                        item.transform.localPosition = new Vector3(0f, 1.15f, 0.4f);
                        item.transform.localRotation = Quaternion.Euler(35f, 0f, 0f);
                        item.gameObject.SetActive(true);
                        if (item.m_Rigidbody != null) item.m_Rigidbody.isKinematic = true;
                        if (item.m_Collider != null) item.m_Collider.enabled = false;
                        av.PackProp = item;
                    }
                    else
                    {
                        CoopPlugin.Log.LogInfo($"pack-open visual: no mesh for pack index {packIndex}");
                    }
                }
                catch { }
            }
        }

        /// <summary>Called every frame from CoopCore while linked.</summary>
        public void Tick(float dt)
        {
            if (!CoopPlugin.AvatarsEnabled.Value) return;
            bool inGame = CSingleton<CGameManager>.Instance != null
                          && CSingleton<CGameManager>.Instance.m_IsGameLevel;
            if (!inGame) return;

            foreach (var av in _avatars.Values)
            {
                if (av.Go == null)
                {
                    if (av.HasState) TrySpawn(av);
                    continue;
                }

                var t = av.Go.transform;
                t.position = Vector3.Lerp(t.position, av.TargetPos, dt * 12f);
                var targetRot = Quaternion.Euler(0f, av.TargetYaw, 0f);
                t.rotation = Quaternion.Slerp(t.rotation, targetRot, dt * 12f);

                if (av.Anim != null)
                {
                    if (av.HasMoveSpeed)
                    {
                        float current = av.Anim.GetFloat("MoveSpeed");
                        av.Anim.SetFloat("MoveSpeed", Mathf.Lerp(current, av.NetSpeed, dt * 8f));
                    }
                    if (av.HasHoldingBox)
                        av.Anim.SetBool("IsHoldingBox", av.HoldState != 0);
                }
                // carried visuals: real item meshes for items, the cardboard prop for boxes
                bool showBox = av.HoldState == 1;
                if (av.HoldProp != null && av.HoldProp.activeSelf != showBox)
                {
                    av.HoldProp.SetActive(showBox);
                    av.HoldProp.transform.localScale = new Vector3(0.34f, 0.27f, 0.34f);
                }
                // loose cards fanned in hand (real card faces, modded expansions included)
                string wantCards = "";
                if (av.HoldState == 3 && av.HoldCards != null && av.HoldCards.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var c in av.HoldCards)
                        sb.Append((int)c.monsterType).Append('/').Append((int)c.expansionType)
                          .Append('/').Append(c.isFoil ? 1 : 0).Append(';');
                    wantCards = sb.ToString();
                }
                if (wantCards != av.CardSig)
                {
                    ReleaseCards(av);
                    av.CardSig = wantCards;
                    if (wantCards.Length > 0)
                    {
                        for (int i = 0; i < av.HoldCards.Count; i++)
                        {
                            try
                            {
                                // the game's own card-visual recipe (same as display shelves)
                                var cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
                                var card3d = ShelfManager.SpawnInteractableObject(EObjectType.Card3d)
                                    .GetComponent<InteractableCard3d>();
                                cardUI.m_CardUI.SetCardUI(av.HoldCards[i]);
                                card3d.transform.SetParent(av.Go.transform, worldPositionStays: false);
                                float spread = (i - (av.HoldCards.Count - 1) * 0.5f);
                                card3d.transform.localPosition = new Vector3(spread * 0.08f, 1.15f, 0.38f);
                                card3d.transform.localRotation = Quaternion.Euler(30f, spread * -9f, 0f);
                                cardUI.transform.position = card3d.transform.position;
                                cardUI.transform.rotation = card3d.transform.rotation;
                                card3d.SetCardUIFollow(cardUI);
                                card3d.SetEnableCollision(isEnable: false);
                                av.HeldCards3d.Add(card3d);
                            }
                            catch { }
                        }
                    }
                }

                // pack-opening prop times out on its own
                if (av.PackProp != null)
                {
                    av.PackTimer -= dt;
                    if (av.PackTimer <= 0f)
                    {
                        try { ItemSpawnManager.DisableItem(av.PackProp); } catch { }
                        av.PackProp = null;
                    }
                }

                // reading the collection binder
                bool wantBinder = av.HoldState == 4;
                if (wantBinder && av.BinderProp == null) TrySpawnBinder(av);
                if (av.BinderProp != null && av.BinderProp.activeSelf != wantBinder)
                    av.BinderProp.SetActive(wantBinder);

                string wantSig = av.HoldState == 2 && av.HoldTypes != null && av.HoldTypes.Count > 0
                    ? string.Join(",", av.HoldTypes) : "";
                if (wantSig != av.HeldSig)
                {
                    ReleaseHeld(av);
                    av.HeldSig = wantSig;
                    if (wantSig.Length > 0)
                    {
                        for (int i = 0; i < av.HoldTypes.Count; i++)
                        {
                            try
                            {
                                var meshData = InventoryBase.GetItemMeshData((EItemType)av.HoldTypes[i]);
                                if (meshData == null) continue;
                                var item = ItemSpawnManager.GetItem(av.Go.transform);
                                item.SetMesh(meshData.mesh, meshData.material, (EItemType)av.HoldTypes[i],
                                    meshData.meshSecondary, meshData.materialSecondary, meshData.materialList);
                                item.transform.localPosition = new Vector3(0f, 1.02f + 0.14f * i, 0.42f);
                                item.transform.localRotation = Quaternion.identity;
                                item.gameObject.SetActive(true);
                                if (item.m_Rigidbody != null) item.m_Rigidbody.isKinematic = true;
                                if (item.m_Collider != null) item.m_Collider.enabled = false;
                                av.HeldItems.Add(item);
                            }
                            catch { }
                        }
                    }
                }

                var cam = Camera.main;
                if (cam != null)
                {
                    if (av.NameTag != null)
                        av.NameTag.transform.rotation =
                            Quaternion.LookRotation(av.NameTag.transform.position - cam.transform.position);
                    if (av.EmoteTag != null)
                        av.EmoteTag.transform.rotation =
                            Quaternion.LookRotation(av.EmoteTag.transform.position - cam.transform.position);
                }

                if (av.EmoteTimer > 0f)
                {
                    av.EmoteTimer -= dt;
                    if (av.EmoteTimer <= 0f && av.EmoteTag != null) av.EmoteTag.text = "";
                }
            }
        }

        private void TrySpawn(RemoteAvatar av)
        {
            var cm = CSingleton<CustomerManager>.Instance;
            if (cm == null) return;

            // stable gender pick per player name, so dad & son each keep a consistent look
            int nameHash = 17;
            foreach (char c in av.Name) nameHash = nameHash * 31 + c;
            bool female = (nameHash & 1) == 1;
            var prefab = female ? cm.m_CustomerFemalePrefab : cm.m_CustomerPrefab;
            if (prefab == null) prefab = cm.m_CustomerPrefab != null ? cm.m_CustomerPrefab : cm.m_CustomerFemalePrefab;
            if (prefab == null) return;

            // Instantiate under an inactive holder (defers Awake), position it, then
            // activate and IMMEDIATELY dress + strip within this same call - no game
            // Update/Start can run in between, so the Customer AI never gets a frame.
            var holder = new GameObject("CoopAvatarHolder_tmp");
            holder.SetActive(false);
            var clone = Object.Instantiate(prefab.gameObject, holder.transform);
            clone.transform.SetParent(null, worldPositionStays: false);
            clone.transform.position = av.TargetPos;
            clone.SetActive(true);
            Object.Destroy(holder);

            var cust = clone.GetComponent<Customer>();
            try
            {
                if (cust != null) cust.RandomizeCharacterMesh(); // game's own wardrobe pipeline
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning("Avatar dressing failed (spawning undressed): " + e.Message);
            }

            // Hide the hand/FX props that ActivateCustomer normally hides - without this
            // the clone spawns clutching the prefab's shopping bag, cash and card fans.
            if (cust != null)
            {
                try
                {
                    if (cust.m_ShoppingBagTransform != null) cust.m_ShoppingBagTransform.gameObject.SetActive(false);
                    if (cust.m_CustomerCash != null) cust.m_CustomerCash.gameObject.SetActive(false);
                    if (cust.m_GameCardFanOut != null) cust.m_GameCardFanOut.SetActive(false);
                    if (cust.m_GameCardSingle != null) cust.m_GameCardSingle.SetActive(false);
                    if (cust.m_CleanFX != null) cust.m_CleanFX.SetActive(false);
                    if (cust.m_ExclaimationMesh != null) cust.m_ExclaimationMesh.SetActive(false);
                    if (cust.m_InteractCollider != null) cust.m_InteractCollider.SetActive(false);
                    if (cust.m_SmellyFX != null) cust.m_SmellyFX.SetActive(false);
                }
                catch (System.Exception e)
                {
                    CoopPlugin.Log.LogWarning("Avatar prop hiding partial: " + e.Message);
                }
            }

            // Strip game logic but KEEP the cosmetic rig helpers (CC namespace): CopyPose
            // drives hair/apparel bones every LateUpdate - destroying it is why hair froze.
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string tn = mb.GetType().Name;
                if (tn == "CopyPose" || tn == "BlendshapeManager" || tn == "ScaleCharacter"
                    || tn == "TransformBone" || tn == "MipBiasAdjust")
                    continue;
                Object.DestroyImmediate(mb);
            }
            foreach (var comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                string n = comp.GetType().Name;
                if (n == "NavMeshAgent" || n == "NavMeshObstacle" || n == "Seeker" || n == "FunnelModifier")
                    Object.DestroyImmediate(comp);
            }
            foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);
            foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);

            clone.name = "CoopAvatar_" + av.Name;

            av.Go = clone;
            av.Anim = clone.GetComponentInChildren<Animator>(true);
            av.EverPositioned = true;

            if (av.Anim != null)
            {
                foreach (var p in av.Anim.parameters)
                {
                    if (p.name == "MoveSpeed") av.HasMoveSpeed = true;
                    if (p.name == "IsHoldingBox") av.HasHoldingBox = true;
                }
                if (!_loggedAnimParams)
                {
                    _loggedAnimParams = true;
                    var sb = new StringBuilder("Avatar animator params: ");
                    foreach (var p in av.Anim.parameters) sb.Append(p.name).Append(' ');
                    CoopPlugin.Log.LogInfo(sb.ToString());
                }
            }

            // carry prop: simple tinted box shown while the remote player holds something
            var prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(prop.GetComponent<Collider>());
            prop.name = "CoopHoldProp";
            prop.transform.SetParent(clone.transform, worldPositionStays: false);
            prop.transform.localPosition = new Vector3(0f, 1.05f, 0.45f);
            prop.transform.localRotation = Quaternion.identity;
            var mr = prop.GetComponent<MeshRenderer>();
            if (mr != null) mr.material.color = new Color(0.72f, 0.55f, 0.35f); // cardboard
            prop.SetActive(false);
            av.HoldProp = prop;

            av.NameTag = MakeTag(clone.transform, av.Name, 2.25f, Color.white);
            av.EmoteTag = MakeTag(clone.transform, "", 2.55f, new Color(1f, 0.85f, 0.2f));
            CoopPlugin.Log.LogInfo($"Spawned co-op avatar for '{av.Name}' ({(female ? "female" : "male")} model)");
        }

        private static void TrySpawnBinder(RemoteAvatar av)
        {
            try
            {
                var src = Object.FindObjectOfType<CollectionBinderFlipAnimCtrl>();
                if (src == null) return;
                var holder = new GameObject("CoopBinderHolder_tmp");
                holder.SetActive(false);
                var clone = Object.Instantiate(src.gameObject, holder.transform);
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null) Object.DestroyImmediate(mb);
                foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(col);
                clone.transform.SetParent(av.Go.transform, worldPositionStays: false);
                clone.transform.localPosition = new Vector3(0f, 1.1f, 0.38f);
                clone.transform.localRotation = Quaternion.Euler(35f, 0f, 0f);
                clone.transform.localScale = Vector3.one * 0.8f;
                clone.name = "CoopBinder";
                clone.SetActive(true);
                Object.Destroy(holder);
                av.BinderProp = clone;
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogInfo("binder prop unavailable: " + e.Message);
            }
        }

        private static TextMesh MakeTag(Transform parent, string text, float height, Color color)
        {
            var go = new GameObject("CoopTag");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, height, 0f);
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 0.02f;
            tm.fontSize = 96;
            tm.color = color;
            Font font = null;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (font == null)
            {
                try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            if (font != null)
            {
                tm.font = font;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) mr.material = font.material;
            }
            else
            {
                CoopPlugin.Log.LogWarning("No builtin font found - name tags will be invisible");
            }
            return tm;
        }
    }
}
