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

        public void UpdateState(int connId, Vector3 pos, float yaw, float speed, byte holdState)
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
            av.HasState = true;
            if (!av.EverPositioned && av.Go != null)
            {
                av.Go.transform.position = pos;
                av.EverPositioned = true;
            }
        }

        public void ShowEmote(int connId)
        {
            if (_avatars.TryGetValue(connId, out var av) && av.EmoteTag != null)
            {
                av.EmoteTag.text = "\\o/  hi!";
                av.EmoteTimer = 2.5f;
            }
        }

        public void Remove(int connId)
        {
            if (_avatars.TryGetValue(connId, out var av))
            {
                if (av.Go != null) Object.Destroy(av.Go);
                _avatars.Remove(connId);
            }
        }

        public void Clear()
        {
            foreach (var av in _avatars.Values)
                if (av.Go != null) Object.Destroy(av.Go);
            _avatars.Clear();
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
                if (av.HoldProp != null && av.HoldProp.activeSelf != (av.HoldState != 0))
                {
                    av.HoldProp.SetActive(av.HoldState != 0);
                    float s = av.HoldState == 1 ? 0.34f : 0.16f; // box vs item-in-hand
                    av.HoldProp.transform.localScale = new Vector3(s, s * 0.8f, s);
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

            try
            {
                var cust = clone.GetComponent<Customer>();
                if (cust != null) cust.RandomizeCharacterMesh(); // game's own wardrobe pipeline
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning("Avatar dressing failed (spawning undressed): " + e.Message);
            }

            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                Object.DestroyImmediate(mb);
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
