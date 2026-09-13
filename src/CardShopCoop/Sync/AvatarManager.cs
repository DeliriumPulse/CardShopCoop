using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Renders remote players by cloning the game's own customer prefab. The clone is
    /// dressed with the game's real wardrobe pipeline (Customer.RandomizeCharacterMesh ->
    /// CharacterCustomization.Initialize) and then stripped of all AI/physics so it's a
    /// pure network puppet. A host-authoritative appearance choice supplies gender, model,
    /// and optional CC customization data; unconfigured players retain the name-based fallback.
    /// The Animator
    /// keeps the game's own walk cycle ("MoveSpeed"), and a simple box prop + the
    /// "IsHoldingBox" pose show when the remote player is carrying something.
    /// </summary>
    public class AvatarManager
    {
        /// <summary>One received transform state, timestamped with local arrival time so
        /// Tick can replay motion slightly in the past instead of predicting ahead.</summary>
        private struct Snapshot
        {
            public Vector3 Pos;
            public float Yaw;
            public float RecvTime;
        }

        private class RemoteAvatar
        {
            public GameObject Go;
            public Animator Anim;
            public bool HasMoveSpeed;
            public bool HasHoldingBox;
            public TMPro.TMP_Text NameTag;
            public TMPro.TMP_Text EmoteTag;
            public GameObject HoldProp;
            public Material HoldPropMat;  // instanced by the tint at spawn; Destroy(Go) alone leaks it
            public string Name = "Player";
            public Vector3 TargetPos;
            public Vector3 Velocity;      // measured between packets, only used when the buffer runs dry
            public float LastStateTime;   // Time.time when TargetPos arrived
            public float TargetYaw;
            public Vector3 CameraPosition;
            public Quaternion CameraRotation = Quaternion.identity;
            public Vector3 PreviousCameraPosition;
            public Quaternion PreviousCameraRotation = Quaternion.identity;
            public bool HasCamera;
            // Drive the walk cycle from the motion rendered on this machine. The wire speed
            // is an arrival-time estimate and can remain stale while a sender is idle.
            public float AnimSpeed;
            public float AppliedAnimSpeed = float.NaN;
            public Vector3 PreviousRenderedPosition;
            public bool HasRenderedPosition;
            public byte HoldState;
            // actual EItemTypes being carried, already LOCAL ids: the translation happened
            // at the wire boundary (Msg.ReadItemType inside CoopCore.ReadHoldPayload), not
            // here. Owned, not aliased: the host relays the sender's list on to the other
            // clients verbatim and must not see anything we do to our copy.
            public readonly List<int> HoldTypes = new List<int>(6);
            public List<CardData> HoldCards;    // actual cards fanned in hand
            public readonly Snapshot[] Snaps = new Snapshot[SnapBufferSize];
            public int SnapHead = -1;           // index of newest snapshot
            public int SnapCount;
            public string HeldSig = "";          // what the spawned item props currently show
            public string CardSig = "";          // what the spawned card visuals currently show
            public string PendingBoxSig = "";    // built at packet rate in UpdateState; Tick only compares
            public string PendingCardSig = "";
            public string PendingItemSig = "";
            public readonly List<Item> HeldItems = new List<Item>();
            public readonly List<InteractableCard3d> HeldCards3d = new List<InteractableCard3d>();
            public GameObject BinderProp;
            public Item PackProp;
            public float PackTimer;
            public GameObject BoxProp;     // real cardboard box clone
            public Item BoxProdItem;       // the product shown on top of it
            public string BoxSig = "";
            public float EmoteTimer;
            public bool EverPositioned;
            public bool HasState;
            public bool HoldingBoxPose;    // last value pushed to the animator, to skip redundant SetBool
            public bool HoldingBoxPoseSet;
            public bool HasModel;
            public bool Female;
            public int ModelIndex;
            public string CustomizationJson;
        }

        private const int SnapBufferSize = 4;
        private const float InterpDelay = 2f / 15f;    // two send intervals at the 15 Hz default
        private const float MaxExtrapolation = 0.25f;  // never dead-reckon further than this past the newest snapshot
        private static readonly int MoveSpeedHash = Animator.StringToHash("MoveSpeed");
        private static readonly int IsHoldingBoxHash = Animator.StringToHash("IsHoldingBox");
        /// <summary>Scene-wide lookup is milliseconds in a full shop; cache it and let the
        /// Unity fake-null re-resolve after scene loads.</summary>
        private static RestockManager _restock;
        // NEVER CSingleton<CustomerManager>.Instance: avatars tick every frame, including
        // the client's world-reload loading screen, where the getter would fabricate a
        // fake empty DontDestroyOnLoad CustomerManager that shadows the real one for the
        // rest of the run - killing avatar respawn, TradeServe and TournamentSync after
        // a rejoin (see WorldSync.ResolveShelfManager). Cached like _restock above.
        private static CustomerManager _customers;

        private readonly Dictionary<int, RemoteAvatar> _avatars = new Dictionary<int, RemoteAvatar>();
        private bool _loggedAnimParams;
        private GameObject _editorHolder;
        private CC.CharacterCustomization _editorCustomization;
        private bool _editorFemale;
        private GameObject _previewBody;
        private CC.CharacterCustomization _previewCustomization;
        private string _previewSignature;
        private const string ApparelTintPrefix = "CardShopCoop.ApparelTint.";

        public void SetName(int connId, string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            if (_avatars.TryGetValue(connId, out var av))
            {
                if (av.Name == name)
                    return;
                av.Name = name;
                if (av.NameTag != null)
                    av.NameTag.text = name;
                if (av.Go != null)
                    av.Go.name = "CoopAvatar_" + name;
            }
            else
            {
                _avatars[connId] = new RemoteAvatar { Name = name };
            }
        }

        public void SetModel(int connId, bool female, int modelIndex, string customizationJson)
        {
            if (!_avatars.TryGetValue(connId, out var av))
            {
                av = new RemoteAvatar();
                _avatars[connId] = av;
            }
            modelIndex = Mathf.Max(0, modelIndex);
            bool changed = !av.HasModel || av.Female != female || av.ModelIndex != modelIndex
                || av.CustomizationJson != customizationJson;
            av.HasModel = true;
            av.Female = female;
            av.ModelIndex = modelIndex;
            av.CustomizationJson = customizationJson;
            if (changed && av.Go != null)
            {
                // Rebuild only on an explicit appearance change. All movement/hold state is
                // retained in RemoteAvatar and the next Tick reconstructs its visuals.
                DespawnBody(av);
            }
        }

        public PlayerModelEntry CaptureLocalModel(Transform root)
        {
            var result = new PlayerModelEntry { Female = false, ModelIndex = 0, CustomizationJson = null };
            var custom = FindCustomization(root);
            if (custom == null)
                return result;
            string name = custom.CharacterName ?? "";
            result.Female = name.StartsWith("Female", System.StringComparison.OrdinalIgnoreCase);
            int parsed;
            int prefixLength = result.Female ? 6 : 4;
            if (name.Length > prefixLength && int.TryParse(name.Substring(prefixLength), out parsed))
                result.ModelIndex = Mathf.Max(0, parsed);
            if (custom.StoredCharacterData != null)
                result.CustomizationJson = JsonConvert.SerializeObject(custom.StoredCharacterData);
            return result;
        }

        public PlayerModelEntry CaptureLocalModel(CC.CharacterCustomization custom, bool female, int modelIndex)
        {
            var result = new PlayerModelEntry
            {
                Female = female,
                ModelIndex = Mathf.Max(0, modelIndex),
                CustomizationJson = null
            };
            if (custom != null && custom.StoredCharacterData != null)
                result.CustomizationJson = JsonConvert.SerializeObject(custom.StoredCharacterData);
            return result;
        }

        public void ApplyLocalModel(Transform root, PlayerModelEntry model)
        {
            var custom = FindCustomization(root);
            ApplyLocalModel(custom, model);
        }

        public void ApplyLocalModel(CC.CharacterCustomization custom, PlayerModelEntry model)
        {
            if (custom == null || model == null)
                return;
            try
            {
                custom.CharacterName = (model.Female ? "Female" : "Male") + Mathf.Max(0, model.ModelIndex);
                custom.Initialize();
                if (!string.IsNullOrEmpty(model.CustomizationJson))
                {
                    var data = JsonConvert.DeserializeObject<CC.CC_CharacterData>(model.CustomizationJson);
                    if (data != null)
                    {
                        if (!CoopPlugin.AllowNsfw.Value && IsNude(data))
                        {
                            // Deliberately nude appearance with NSFW off: forget the payload
                            // and keep the game's initialized (clothed) preset.
                            model.CustomizationJson = null;
                            CoopPlugin.Log.LogInfo("blocked a nude local appearance because NSFW is disabled");
                        }
                        else
                        {
                            NormalizeCharacterData(custom, data);
                            custom.StoredCharacterData = data;
                            if (!TryApplyCharacterData(custom, data, "local model"))
                                model.CustomizationJson = null;
                            else
                                ClearEmptyWardrobeSlots(custom, data);
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                // Appearance data is user/session state, never a reason to fail a join or
                // abort the world snapshot. Initialize has already selected the safe default.
                model.CustomizationJson = null;
                CoopPlugin.Log.LogWarning("Local character model was reset after invalid appearance data: " + e.Message);
                try
                {
                    custom.Initialize();
                }
                catch (System.Exception resetError)
                {
                    CoopPlugin.Log.LogWarning("Default character model could not be initialized: " + resetError.Message);
                }
            }
        }

        private static void NormalizeCharacterData(CC.CharacterCustomization custom, CC.CC_CharacterData data)
        {
            if (data.Blendshapes == null)
                data.Blendshapes = new List<CC.CC_Property>();
            if (data.TextureProperties == null)
                data.TextureProperties = new List<CC.CC_Property>();
            if (data.FloatProperties == null)
                data.FloatProperties = new List<CC.CC_Property>();
            if (data.ColorProperties == null)
                data.ColorProperties = new List<CC.CC_Property>();

            int hairSlots = custom.HairTables != null ? custom.HairTables.Count : 0;
            int apparelSlots = custom.ApparelTables != null ? custom.ApparelTables.Count : 0;
            NormalizeList(data.HairNames, hairSlots, "", value => data.HairNames = value);
            NormalizeList(data.HairColor, hairSlots, () => new CC.CC_Property(), value => data.HairColor = value);
            NormalizeList(data.ApparelNames, apparelSlots, "", value => data.ApparelNames = value);
            NormalizeList(data.ApparelMaterials, apparelSlots, 0, value => data.ApparelMaterials = value);
        }

        private static void NormalizeList<T>(List<T> source, int count, T fill, System.Action<List<T>> assign)
        {
            var list = source ?? new List<T>();
            if (list.Count > count)
                list.RemoveRange(count, list.Count - count);
            while (list.Count < count)
                list.Add(fill);
            assign(list);
        }

        private static void NormalizeList<T>(List<T> source, int count, System.Func<T> fill, System.Action<List<T>> assign)
        {
            var list = source ?? new List<T>();
            if (list.Count > count)
                list.RemoveRange(count, list.Count - count);
            while (list.Count < count)
                list.Add(fill());
            assign(list);
        }

        private static bool TryApplyCharacterData(CC.CharacterCustomization custom, CC.CC_CharacterData data, string context)
        {
            var savedApparelTints = data.ColorProperties != null
                ? data.ColorProperties.FindAll(p => p != null && p.propertyName != null && p.propertyName.StartsWith(ApparelTintPrefix, System.StringComparison.Ordinal))
                : new List<CC.CC_Property>();
            try
            {
                if (data.ColorProperties != null)
                    data.ColorProperties.RemoveAll(p => p != null && p.propertyName != null && p.propertyName.StartsWith(ApparelTintPrefix, System.StringComparison.Ordinal));
                custom.ApplyCharacterVars(data);
                if (data.ColorProperties != null)
                    data.ColorProperties.AddRange(savedApparelTints);
                ApplyStoredApparelTints(custom, savedApparelTints);
                return true;
            }
            catch (System.Exception e)
            {
                if (data.ColorProperties != null)
                {
                    data.ColorProperties.RemoveAll(p => p != null && p.propertyName != null && p.propertyName.StartsWith(ApparelTintPrefix, System.StringComparison.Ordinal));
                    data.ColorProperties.AddRange(savedApparelTints);
                }
                CoopPlugin.Log.LogWarning("Character appearance reset during " + context + ": " + e.Message);
                try
                {
                    custom.Initialize();
                }
                catch (System.Exception resetError)
                {
                    CoopPlugin.Log.LogWarning("Character appearance default reset failed: " + resetError.Message);
                }
                return false;
            }
        }

        private static void ApplyStoredApparelTints(CC.CharacterCustomization custom, List<CC.CC_Property> tints)
        {
            if (custom == null || tints == null)
                return;
            foreach (var tint in tints)
            {
                if (tint == null || !int.TryParse(tint.propertyName.Substring(ApparelTintPrefix.Length), out int slot))
                    continue;
                Color color;
                if (!ColorUtility.TryParseHtmlString("#" + tint.stringValue, out color))
                    continue;
                var field = typeof(CC.CharacterCustomization).GetField("ApparelObjects",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
                if (objects == null || slot < 0 || slot >= objects.Count || objects[slot] == null)
                    continue;
                foreach (var renderer in objects[slot].GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.materials)
                    {
                        material.SetColor("_Tint", color);
                        material.SetColor("_Tint_R", color);
                        material.SetColor("_Tint_G", color);
                        material.SetColor("_Tint_B", color);
                    }
            }
        }

        private static void ClearEmptyWardrobeSlots(CC.CharacterCustomization custom, CC.CC_CharacterData data)
        {
            if (custom == null || data == null)
                return;
            for (int slot = 0; slot < custom.HairTables.Count; slot++)
                if (slot >= data.HairNames.Count || string.IsNullOrEmpty(data.HairNames[slot]))
                    ClearHairOverlay(custom, slot);
            for (int slot = 0; slot < custom.ApparelTables.Count; slot++)
                if (slot >= data.ApparelNames.Count || string.IsNullOrEmpty(data.ApparelNames[slot]))
                    ClearApparelOverlay(custom, slot);
        }

        private static void ClearAllApparel(CC.CharacterCustomization custom)
        {
            if (custom == null || custom.ApparelTables == null)
                return;
            for (int slot = 0; slot < custom.ApparelTables.Count; slot++)
                ClearApparelOverlay(custom, slot);
        }

        private static void ClearHairOverlay(CC.CharacterCustomization custom, int slot)
        {
            if (custom == null || slot < 0 || slot >= custom.HairTables.Count)
                return;
            var field = typeof(CC.CharacterCustomization).GetField("HairObjects",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
            if (objects != null && slot < objects.Count && objects[slot] != null)
            {
                Object.Destroy(objects[slot]);
                objects[slot] = null;
            }
            if (custom.StoredCharacterData != null && slot < custom.StoredCharacterData.HairNames.Count)
                custom.StoredCharacterData.HairNames[slot] = "";
        }

        private static void ClearApparelOverlay(CC.CharacterCustomization custom, int slot)
        {
            if (custom == null || slot < 0 || slot >= custom.ApparelTables.Count)
                return;
            var field = typeof(CC.CharacterCustomization).GetField("ApparelObjects",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
            if (objects != null && slot < objects.Count && objects[slot] != null)
            {
                Object.Destroy(objects[slot]);
                objects[slot] = null;
            }
            ResetApparelMask(custom, slot);
            if (custom.StoredCharacterData != null && slot < custom.StoredCharacterData.ApparelNames.Count)
            {
                custom.StoredCharacterData.ApparelNames[slot] = "";
                if (slot < custom.StoredCharacterData.ApparelMaterials.Count)
                    custom.StoredCharacterData.ApparelMaterials[slot] = 0;
            }
        }

        private static void ResetApparelMask(CC.CharacterCustomization custom, int slot)
        {
            if (custom == null || custom.ApparelTables == null || slot < 0 || slot >= custom.ApparelTables.Count)
                return;
            var table = custom.ApparelTables[slot];
            if (table == null || string.IsNullOrEmpty(table.MaskProperty))
                return;

            // Apparel applies its mask to the base body material. Removing only the
            // apparel mesh leaves the old mask behind, which makes the nude body render
            // as holes. A solid white texture restores the body's fully visible state.
            custom.setTextureProperty(new CC.CC_Property { propertyName = table.MaskProperty },
                save: false, t: Texture2D.whiteTexture);
        }

        /// <summary>True when the appearance would leave the character fully nude: no
        /// customization payload at all, or a payload whose every apparel slot is empty.
        /// A deliberately nude character commits a payload with empty apparel names, so
        /// null JSON must be treated as "unset" (the initialized preset still counts as nude),
        /// which is exactly what the NSFW guard acts on.</summary>
        public static bool IsNude(PlayerModelEntry model)
        {
            if (model == null)
                return true;
            return IsNude(model.CustomizationJson);
        }

        public static bool IsNude(string customizationJson)
        {
            if (string.IsNullOrEmpty(customizationJson))
                return true;
            CC.CC_CharacterData data;
            try
            {
                data = JsonConvert.DeserializeObject<CC.CC_CharacterData>(customizationJson);
            }
            catch (System.Exception e) { Swallow.Log(e); return true; }
            return IsNude(data);
        }

        public static bool IsNude(CC.CC_CharacterData data)
        {
            if (data == null || data.ApparelNames == null || data.ApparelNames.Count == 0)
                return true;
            for (int i = 0; i < data.ApparelNames.Count; i++)
                if (!IsNudeApparelName(data.ApparelNames[i]))
                    return false;
            return true;
        }

        /// <summary>The game stores the "nothing worn in this slot" item as the literal names
        /// "None" (and the wardrobe's Nude preset can leave "Nude"); both, and an empty name,
        /// count as nude.</summary>
        private static bool IsNudeApparelName(string name)
        {
            return string.IsNullOrEmpty(name)
                || name.Equals("None", System.StringComparison.OrdinalIgnoreCase)
                || name.Equals("Nude", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>NSFW is off: replace each nude apparel slot ("None"/empty/"Nude") with the
        /// first real item for that slot, leaving hair, body and any worn items untouched. This
        /// is deliberately slot-level so a remote nude player still looks like themselves.</summary>
        private static void ClotheNudeSlots(CC.CharacterCustomization custom, CC.CC_CharacterData data)
        {
            if (custom == null || data == null || custom.ApparelTables == null)
                return;
            if (data.ApparelNames == null)
                data.ApparelNames = new System.Collections.Generic.List<string>();
            while (data.ApparelNames.Count < custom.ApparelTables.Count)
                data.ApparelNames.Add("");
            for (int slot = 0; slot < custom.ApparelTables.Count && slot < data.ApparelNames.Count; slot++)
            {
                if (!IsNudeApparelName(data.ApparelNames[slot]))
                    continue;
                var table = custom.ApparelTables[slot];
                if (table == null || table.Items == null || table.Items.Count == 0)
                    continue;
                data.ApparelNames[slot] = table.Items[0].Name ?? "";
            }
        }

        /// <summary>Builds a model entry from a wardrobe preset (a complete CC_CharacterData).
        /// The preset's CharacterName decides gender and model index.</summary>
        public static PlayerModelEntry ModelFromPreset(CC.CC_CharacterData preset)
        {
            if (preset == null)
                return null;
            bool female = (preset.CharacterName ?? "").StartsWith("Female", System.StringComparison.OrdinalIgnoreCase);
            return new PlayerModelEntry
            {
                Female = female,
                ModelIndex = ParsePresetIndex(preset.CharacterName, female),
                CustomizationJson = JsonConvert.SerializeObject(preset)
            };
        }

        private static int ParsePresetIndex(string characterName, bool female)
        {
            int prefixLength = female ? 6 : 4;
            int parsed;
            if (!string.IsNullOrEmpty(characterName) && characterName.Length > prefixLength
                && int.TryParse(characterName.Substring(prefixLength), out parsed))
                return Mathf.Max(0, parsed);
            return 0;
        }

        /// <summary>Random clothed preset for the requested gender, or null while the game's
        /// preset data is not available yet (e.g. before a world with a CustomerManager exists).
        /// Never returns a nude entry: if the preset list is unusable it falls back to the
        /// editor template's own initialized preset.</summary>
        public PlayerModelEntry TryCreateRandomPresetModel(bool female)
        {
            var custom = GetEditorCustomization(female);
            if (custom == null)
                return null;
            string prefix = female ? "Female" : "Male";
            var presets = custom.Presets != null ? custom.Presets.Presets : null;
            if (presets != null && presets.Count > 0)
            {
                var matches = new List<CC.CC_CharacterData>();
                foreach (var preset in presets)
                    if (preset != null && (preset.CharacterName ?? "").StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                        matches.Add(preset);
                if (matches.Count > 0)
                    return ModelFromPreset(matches[UnityEngine.Random.Range(0, matches.Count)]);
            }
            return ModelFromPreset(custom.StoredCharacterData);
        }

        /// <summary>Random gender + random preset of that gender, or null while preset data
        /// is unavailable.</summary>
        public PlayerModelEntry TryCreateRandomModel()
        {
            return TryCreateRandomPresetModel(UnityEngine.Random.value < 0.5f);
        }

        /// <summary>Placeholder used before preset data exists: random gender, model 0, no
        /// payload. Applying it leaves the game's initialized preset on the body.</summary>
        public PlayerModelEntry CreateDefaultModel()
        {
            return new PlayerModelEntry
            {
                Female = UnityEngine.Random.value < 0.5f,
                ModelIndex = 0,
                CustomizationJson = null
            };
        }

        public List<CC.CC_Property> GetLocalBlendshapes(Transform root)
        {
            var custom = FindCustomization(root);
            if (custom == null || custom.StoredCharacterData == null)
                return new List<CC.CC_Property>();
            return custom.StoredCharacterData.Blendshapes ?? new List<CC.CC_Property>();
        }

        public List<CC.CC_Property> GetBlendshapes(CC.CharacterCustomization custom)
        {
            if (custom == null || custom.StoredCharacterData == null)
                return new List<CC.CC_Property>();
            return custom.StoredCharacterData.Blendshapes ?? new List<CC.CC_Property>();
        }

        public CC.CharacterCustomization GetLocalCustomization(Transform root)
        {
            return FindCustomization(root);
        }

        public CC.CharacterCustomization GetEditorCustomization(bool female)
        {
            if (_editorCustomization != null && _editorFemale == female)
                return _editorCustomization;
            if (_editorHolder != null)
                Object.DestroyImmediate(_editorHolder);
            _editorCustomization = null;
            if (_customers == null)
                _customers = Object.FindObjectOfType<CustomerManager>();
            if (_customers == null)
                return null;
            var prefab = female ? _customers.m_CustomerFemalePrefab : _customers.m_CustomerPrefab;
            if (prefab == null)
                return null;
            _editorHolder = new GameObject("CoopCharacterEditorTemplate");
            _editorHolder.SetActive(false);
            var clone = Object.Instantiate(prefab.gameObject, _editorHolder.transform);
            var customer = clone.GetComponent<Customer>();
            _editorCustomization = customer != null
                ? customer.m_CharacterCustom
                : clone.GetComponentInChildren<CC.CharacterCustomization>(true);
            _editorFemale = female;
            if (_editorCustomization != null)
            {
                _editorCustomization.CharacterName = (female ? "Female" : "Male") + "0";
                _editorCustomization.Initialize();
                CoopPlugin.Log.LogInfo($"character editor template: {(female ? "female" : "male")}, hair slots={_editorCustomization.HairTables.Count}, apparel slots={_editorCustomization.ApparelTables.Count}, presets={(_editorCustomization.Presets != null && _editorCustomization.Presets.Presets != null ? _editorCustomization.Presets.Presets.Count : 0)}");
            }
            return _editorCustomization;
        }

        public List<CC.CC_Property> GetBlendshapes(Transform root, bool female)
        {
            return GetBlendshapes(GetEditorCustomization(female));
        }

        public bool SetLocalBlendshape(Transform root, string propertyName, float value)
        {
            var custom = FindCustomization(root);
            if (custom == null || string.IsNullOrEmpty(propertyName))
                return false;
            custom.setBlendshapeByName(propertyName, Mathf.Clamp01(value));
            return true;
        }

        public bool SetBlendshape(CC.CharacterCustomization custom, string propertyName, float value)
        {
            if (custom == null || string.IsNullOrEmpty(propertyName))
                return false;
            custom.setBlendshapeByName(propertyName, Mathf.Clamp01(value));
            return true;
        }

        public bool SetHairColor(CC.CharacterCustomization custom, int slot, Color color)
        {
            if (custom == null || slot < 0 || custom.HairTables == null || slot >= custom.HairTables.Count
                || custom.StoredCharacterData == null)
                return false;
            custom.setHairColor(new CC.CC_Property { propertyName = "_Hair_Color" }, color, slot, save: true);
            return true;
        }

        public Color GetHairColor(CC.CharacterCustomization custom, int slot)
        {
            if (custom == null || custom.StoredCharacterData == null || slot < 0
                || custom.StoredCharacterData.HairColor == null || slot >= custom.StoredCharacterData.HairColor.Count)
                return Color.white;
            Color color;
            return ColorUtility.TryParseHtmlString("#" + custom.StoredCharacterData.HairColor[slot].stringValue, out color)
                ? color : Color.white;
        }

        public bool SetApparelTint(CC.CharacterCustomization custom, int slot, Color color)
        {
            if (custom == null || custom.StoredCharacterData == null || slot < 0
                || custom.ApparelTables == null || slot >= custom.ApparelTables.Count)
                return false;
            var field = typeof(CC.CharacterCustomization).GetField("ApparelObjects",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
            if (objects == null || slot >= objects.Count || objects[slot] == null)
                return false;
            foreach (var renderer in objects[slot].GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.materials)
                {
                    material.SetColor("_Tint", color);
                    material.SetColor("_Tint_R", color);
                    material.SetColor("_Tint_G", color);
                    material.SetColor("_Tint_B", color);
                }
            string propertyName = ApparelTintPrefix + slot;
            string value = ColorUtility.ToHtmlStringRGBA(color);
            var properties = custom.StoredCharacterData.ColorProperties;
            if (properties == null)
                custom.StoredCharacterData.ColorProperties = properties = new List<CC.CC_Property>();
            var saved = properties.Find(p => p != null && p.propertyName == propertyName);
            if (saved == null)
                properties.Add(new CC.CC_Property { propertyName = propertyName, stringValue = value });
            else
                saved.stringValue = value;
            return true;
        }

        public bool ClearLocalHair(Transform root, int slot)
        {
            var custom = FindCustomization(root);
            if (custom == null || slot < 0 || slot >= custom.HairTables.Count)
                return false;
            var field = typeof(CC.CharacterCustomization).GetField("HairObjects",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
            if (objects != null && slot < objects.Count && objects[slot] != null)
            {
                Object.Destroy(objects[slot]);
                objects[slot] = null;
            }
            if (custom.StoredCharacterData != null && slot < custom.StoredCharacterData.HairNames.Count)
                custom.StoredCharacterData.HairNames[slot] = "";
            return true;
        }

        public bool ClearHair(CC.CharacterCustomization custom, int slot)
        {
            if (custom == null || slot < 0 || slot >= custom.HairTables.Count)
                return false;
            var field = typeof(CC.CharacterCustomization).GetField("HairObjects",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
            if (objects != null && slot < objects.Count && objects[slot] != null)
            {
                Object.Destroy(objects[slot]);
                objects[slot] = null;
            }
            if (custom.StoredCharacterData != null && slot < custom.StoredCharacterData.HairNames.Count)
                custom.StoredCharacterData.HairNames[slot] = "";
            return true;
        }

        public bool ClearLocalApparel(Transform root, int slot)
        {
            var custom = FindCustomization(root);
            if (custom == null || slot < 0 || slot >= custom.ApparelTables.Count)
                return false;
            var field = typeof(CC.CharacterCustomization).GetField("ApparelObjects",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
            if (objects != null && slot < objects.Count && objects[slot] != null)
            {
                Object.Destroy(objects[slot]);
                objects[slot] = null;
            }
            ResetApparelMask(custom, slot);
            if (custom.StoredCharacterData != null && slot < custom.StoredCharacterData.ApparelNames.Count)
                custom.StoredCharacterData.ApparelNames[slot] = "";
            return true;
        }

        public bool ClearApparel(CC.CharacterCustomization custom, int slot)
        {
            if (custom == null || slot < 0 || slot >= custom.ApparelTables.Count)
                return false;
            var field = typeof(CC.CharacterCustomization).GetField("ApparelObjects",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var objects = field != null ? field.GetValue(custom) as List<GameObject> : null;
            if (objects != null && slot < objects.Count && objects[slot] != null)
            {
                Object.Destroy(objects[slot]);
                objects[slot] = null;
            }
            ResetApparelMask(custom, slot);
            if (custom.StoredCharacterData != null && slot < custom.StoredCharacterData.ApparelNames.Count)
                custom.StoredCharacterData.ApparelNames[slot] = "";
            return true;
        }

        private static CC.CharacterCustomization FindCustomization(Transform root)
        {
            if (root == null)
                return null;
            var found = root.GetComponentInChildren<CC.CharacterCustomization>(true);
            if (found != null)
                return found;
            found = root.GetComponentInParent<CC.CharacterCustomization>();
            if (found != null)
                return found;
            // Some game builds keep the visual body beside, rather than below, the CMF
            // walker. Resolve the nearest customization once the selector is opened.
            var all = Object.FindObjectsOfType<CC.CharacterCustomization>(true);
            float best = 9f;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null)
                    continue;
                float distance = (all[i].transform.position - root.position).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    found = all[i];
                }
            }
            return found;
        }

        public void UpdateState(int connId, Vector3 pos, float yaw, byte holdState,
            Vector3 cameraPosition = default(Vector3), Quaternion cameraRotation = default(Quaternion),
            List<int> holdTypes = null, List<CardData> holdCards = null)
        {
            if (!_avatars.TryGetValue(connId, out var av))
            {
                av = new RemoteAvatar();
                _avatars[connId] = av;
            }
            float now = Time.time;
            float span = now - av.LastStateTime;
            if (av.HasState && span > 0.01f && span < 1f)
            {
                var v = (pos - av.TargetPos) / span;
                v.y = 0f;
                av.Velocity = Vector3.ClampMagnitude(v, 6f);
            }
            else
                av.Velocity = Vector3.zero;
            if (av.HasCamera)
            {
                av.PreviousCameraPosition = av.CameraPosition;
                av.PreviousCameraRotation = av.CameraRotation;
            }
            else
            {
                av.PreviousCameraPosition = cameraPosition;
                av.PreviousCameraRotation = cameraRotation;
            }
            av.LastStateTime = now;
            av.TargetPos = pos;
            av.TargetYaw = yaw;
            av.CameraPosition = cameraPosition;
            av.CameraRotation = cameraRotation == default(Quaternion) ? Quaternion.identity : cameraRotation;
            av.HasCamera = cameraRotation != default(Quaternion);
            av.HoldState = holdState;
            // THE HOLD PAYLOAD IS ALREADY IN LOCAL IDS: CoopCore.ReadHoldPayload built it
            // with Msg.ReadItemType, which is the one and only translation boundary for
            // these values. Do NOT translate again here - a second FromWire on an
            // already-local id is how a modded product turns into the wrong prop (or into
            // None). This loop is a plain copy into the avatar's OWN list (not aliased: the
            // host relays the sender's list on to the other clients verbatim).
            // An item from a content pack this PC does not have already arrived as
            // EItemType.None, and the hold-prop loop tests for that sentinel BY VALUE: note
            // that GetItemMeshData(None) hands back a BLANK but non-null ItemMeshData, so a
            // "meshData == null" check alone would NOT skip it. Skipped explicitly, the avatar
            // carries one item fewer and nothing is destroyed.
            av.HoldTypes.Clear();
            if (holdTypes != null)
            {
                // hold state 1 packs [isBigBox flag, product EItemType]: slot 0 is a BOOL,
                // not an id, and must never be translated. ReadHoldPayload already honours
                // that (the flag is 0/1, far below EnumMap's modded floor, so its
                // Msg.ReadItemType pass is the identity function) - and nothing here
                // translates at all, so the copy is uniform.
                for (int i = 0; i < holdTypes.Count; i++)
                    av.HoldTypes.Add(holdTypes[i]);
            }
            av.HoldCards = holdCards;
            av.HasState = true;

            av.SnapHead = (av.SnapHead + 1) % SnapBufferSize;
            av.Snaps[av.SnapHead] = new Snapshot { Pos = pos, Yaw = yaw, RecvTime = now };
            if (av.SnapCount < SnapBufferSize)
                av.SnapCount++;

            // Hold signatures are built here, at packet rate (<=15 Hz): strings are fine at
            // this cadence, and Tick then only compares cached strings so rendering never
            // allocates while something is carried (steady per-frame garbage was a GC-stutter
            // source on the joiner).
            // the sigs are built from the LOCAL-id list, so they describe what will
            // actually be rendered here rather than what the sender saw
            av.PendingBoxSig = holdState == 1
                ? (av.HoldTypes.Count >= 2
                    ? av.HoldTypes[0] + ":" + av.HoldTypes[1] : "0:0")
                : "";
            if (holdState == 3 && holdCards != null && holdCards.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var c in holdCards)
                    sb.Append((int)c.monsterType).Append('/').Append((int)c.expansionType)
                      .Append('/').Append(c.isFoil ? 1 : 0).Append(';');
                av.PendingCardSig = sb.ToString();
            }
            else
                av.PendingCardSig = "";
            av.PendingItemSig = holdState == 2 && av.HoldTypes.Count > 0
                ? string.Join(",", av.HoldTypes) : "";

            if (!av.EverPositioned && av.Go != null)
            {
                av.Go.transform.position = pos;
                av.EverPositioned = true;
            }
        }

        public bool TryGetPlacementCamera(int connId, out Vector3 position, out Quaternion rotation)
        {
            if (_avatars.TryGetValue(connId, out var avatar) && avatar.HasState && avatar.HasCamera)
            {
                float t = Mathf.Clamp01((Time.time - avatar.LastStateTime) / (1f / 15f));
                position = Vector3.Lerp(avatar.PreviousCameraPosition, avatar.CameraPosition, t);
                rotation = Quaternion.Slerp(avatar.PreviousCameraRotation, avatar.CameraRotation, t);
                return true;
            }
            position = default(Vector3);
            rotation = Quaternion.identity;
            return false;
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
                DestroyBody(av);
                _avatars.Remove(connId);
            }
        }

        public void Clear()
        {
            foreach (var av in _avatars.Values)
                DespawnBody(av);
            _avatars.Clear();
            if (_editorHolder != null)
                Object.Destroy(_editorHolder);
            _editorHolder = null;
            _editorCustomization = null;
            DestroyPreview();
        }

        public void UpdatePreview(PlayerModelEntry model, Transform player, Transform camera, bool visible)
        {
            if (!visible || model == null || player == null || camera == null)
            {
                DestroyPreview();
                return;
            }

            string signature = (model.Female ? "F" : "M") + model.ModelIndex + ":" + (model.CustomizationJson ?? "");
            if (_previewBody == null || _previewSignature != signature)
            {
                DestroyPreview();
                SpawnPreview(model);
                _previewSignature = signature;
            }
            if (_previewBody == null)
                return;
            Vector3 forward = camera.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                forward = player.forward;
            forward.Normalize();
            Vector3 position = player.position + forward * 1.6f;
            position.y = player.position.y;
            _previewBody.transform.position = position;
            Vector3 towardPlayer = player.position - position;
            towardPlayer.y = 0f;
            if (towardPlayer.sqrMagnitude > 0.001f)
                _previewBody.transform.rotation = Quaternion.LookRotation(towardPlayer.normalized, Vector3.up);
        }

        public void DestroyPreview()
        {
            if (_previewBody != null)
                Object.Destroy(_previewBody);
            _previewBody = null;
            _previewCustomization = null;
            _previewSignature = null;
        }

        private void SpawnPreview(PlayerModelEntry model)
        {
            if (_customers == null)
                _customers = Object.FindObjectOfType<CustomerManager>();
            if (_customers == null)
                return;
            var prefab = model.Female ? _customers.m_CustomerFemalePrefab : _customers.m_CustomerPrefab;
            if (prefab == null)
                return;
            var holder = new GameObject("CoopCharacterPreviewHolder_tmp");
            holder.SetActive(false);
            var clone = Object.Instantiate(prefab.gameObject, holder.transform);
            clone.transform.SetParent(null, false);
            clone.SetActive(true);
            Object.Destroy(holder);

            var customer = clone.GetComponent<Customer>();
            HideCustomerProps(customer, "character preview");
            _previewCustomization = customer != null ? customer.m_CharacterCustom
                : clone.GetComponentInChildren<CC.CharacterCustomization>(true);
            if (_previewCustomization != null)
            {
                _previewCustomization.CharacterName = (model.Female ? "Female" : "Male") + Mathf.Max(0, model.ModelIndex);
                _previewCustomization.Initialize();
                // A null payload, a nude payload with NSFW off, or an undecodable payload
                // all keep the game's initialized (clothed) preset instead of stripping it.
                if (!string.IsNullOrEmpty(model.CustomizationJson))
                {
                    var data = JsonConvert.DeserializeObject<CC.CC_CharacterData>(model.CustomizationJson);
                    if (data != null && (CoopPlugin.AllowNsfw.Value || !IsNude(data)))
                    {
                        NormalizeCharacterData(_previewCustomization, data);
                        _previewCustomization.StoredCharacterData = data;
                        if (TryApplyCharacterData(_previewCustomization, data, "character preview"))
                            ClearEmptyWardrobeSlots(_previewCustomization, data);
                    }
                }
            }

            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                    continue;
                string name = mb.GetType().Name;
                if (name == "CopyPose" || name == "BlendshapeManager" || name == "ScaleCharacter"
                    || name == "CharacterCustomization" || name == "TransformBone" || name == "MipBiasAdjust")
                    continue;
                Object.DestroyImmediate(mb);
            }
            foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);
            foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);
            clone.name = "CoopCharacterPreview";
            _previewBody = clone;
        }

        /// <summary>Tears down the spawned body AND the animator/pose flags Tick tracks for
        /// it, so the next Tick rebuilds the body from the retained RemoteAvatar state.</summary>
        private void DespawnBody(RemoteAvatar av)
        {
            if (av == null || av.Go == null)
                return;
            ReleaseHeld(av);
            DestroyBody(av);
            av.Go = null;
            av.Anim = null;
            av.HasMoveSpeed = false;
            av.HasHoldingBox = false;
            av.HoldingBoxPoseSet = false;
            av.EverPositioned = false;
        }

        /// <summary>Drops every remote body so the next Tick respawns it. Used when the
        /// NSFW filter changes, so an avatar that should now be re-dressed is rebuilt.</summary>
        public void RefreshRemoteAvatars()
        {
            foreach (var av in _avatars.Values)
                DespawnBody(av);
        }

        /// <summary>Materials are assets, not scene objects: the instanced cube tint from
        /// TrySpawn must be destroyed explicitly or it leaks past Destroy(Go).</summary>
        private static void DestroyBody(RemoteAvatar av)
        {
            if (av.HoldPropMat != null)
            {
                Object.Destroy(av.HoldPropMat);
                av.HoldPropMat = null;
            }
            if (av.Go != null)
                Object.Destroy(av.Go);
        }

        /// <summary>Releases ONLY the loose-item pile. The items signature changes every time
        /// the remote player adds one to the stack - tearing down pack/binder/box visuals on
        /// that path forced needless re-instantiation mid-carry.</summary>
        private static void ReleaseItems(RemoteAvatar av)
        {
            foreach (var item in av.HeldItems)
                if (item != null)
                {
                    try
                    {
                        ItemSpawnManager.DisableItem(item);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
            av.HeldItems.Clear();
            av.HeldSig = "";
        }

        private static void ReleaseHeld(RemoteAvatar av)
        {
            ReleaseItems(av);
            ReleaseCards(av);
            if (av.PackProp != null)
            {
                try
                {
                    ItemSpawnManager.DisableItem(av.PackProp);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                av.PackProp = null;
            }
            if (av.BinderProp != null)
            {
                Object.Destroy(av.BinderProp);
                av.BinderProp = null;
            }
            ReleaseBoxProp(av);
        }

        private static void HideCustomerProps(Customer customer, string context)
        {
            if (customer == null)
                return;
            try
            {
                if (customer.m_ShoppingBagTransform != null)
                    customer.m_ShoppingBagTransform.gameObject.SetActive(false);
                if (customer.m_CustomerCash != null)
                    customer.m_CustomerCash.gameObject.SetActive(false);
                if (customer.m_GameCardFanOut != null)
                    customer.m_GameCardFanOut.SetActive(false);
                if (customer.m_GameCardSingle != null)
                    customer.m_GameCardSingle.SetActive(false);
                if (customer.m_CleanFX != null)
                    customer.m_CleanFX.SetActive(false);
                if (customer.m_ExclaimationMesh != null)
                    customer.m_ExclaimationMesh.SetActive(false);
                if (customer.m_InteractCollider != null)
                    customer.m_InteractCollider.SetActive(false);
                if (customer.m_SmellyFX != null)
                    customer.m_SmellyFX.SetActive(false);
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning("Customer prop hiding partial (" + context + "): " + e.Message);
            }
        }

        private static void ReleaseBoxProp(RemoteAvatar av)
        {
            if (av.BoxProdItem != null)
            {
                try
                {
                    ItemSpawnManager.DisableItem(av.BoxProdItem);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                av.BoxProdItem = null;
            }
            if (av.BoxProp != null)
            {
                Object.Destroy(av.BoxProp);
                av.BoxProp = null;
            }
            av.BoxSig = "";
        }

        /// <summary>Clone the game's real packaging-box prefab as a pure visual: stripped
        /// BEFORE activation so its scripts never wake (no manager registration, no physics).</summary>
        private static void TrySpawnBoxProp(RemoteAvatar av, bool isBig, int itemType)
        {
            try
            {
                if (_restock == null)
                    _restock = Object.FindObjectOfType<RestockManager>();
                var rm = _restock;
                var prefab = isBig ? rm?.m_PackageBoxPrefab : rm?.m_PackageBoxSmallPrefab;
                if (prefab == null)
                    return;
                var holder = new GameObject("CoopBoxHolder_tmp");
                holder.SetActive(false);
                var clone = Object.Instantiate(prefab.gameObject, holder.transform);
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null)
                        Object.DestroyImmediate(mb);
                foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                    Object.DestroyImmediate(rb);
                foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(col);
                // anchor to the CHEST BONE so the box rides the carry pose; then correct
                // against the model's rendered bounds - the prefab's pivot is offset from
                // its visible box, which is how a "waist-height" offset drew at the knees
                Transform anchor = null;
                try
                {
                    anchor = av.Anim != null ? av.Anim.GetBoneTransform(HumanBodyBones.Chest) : null;
                }
                catch (System.Exception e) { Swallow.Log(e); }
                var body = av.Go.transform;
                clone.transform.SetParent(anchor != null ? anchor : body, worldPositionStays: false);
                clone.transform.rotation = body.rotation;
                clone.name = "CoopBoxProp";
                clone.SetActive(true);
                Object.Destroy(holder);
                Vector3 armsCenter = (anchor != null
                        ? anchor.position - body.up * 0.05f
                        : body.position + body.up * 1.16f)
                    + body.forward * 0.42f;
                var rends = clone.GetComponentsInChildren<Renderer>();
                if (rends.Length > 0)
                {
                    var b = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++)
                        b.Encapsulate(rends[i].bounds);
                    clone.transform.position += armsCenter - b.center;
                }
                else
                {
                    clone.transform.position = armsCenter;
                }
                av.BoxProp = clone;

                if (itemType > 0)
                {
                    var meshData = InventoryBase.GetItemMeshData((EItemType)itemType);
                    if (meshData != null)
                    {
                        var item = ItemSpawnManager.GetItem(clone.transform); // rides the box
                        item.SetMesh(meshData.mesh, meshData.material, (EItemType)itemType,
                            meshData.meshSecondary, meshData.materialSecondary, meshData.materialList);
                        item.transform.position = armsCenter + body.up * (isBig ? 0.30f : 0.22f);
                        item.transform.rotation = body.rotation;
                        item.gameObject.SetActive(true);
                        if (item.m_Rigidbody != null)
                            item.m_Rigidbody.isKinematic = true;
                        if (item.m_Collider != null)
                            item.m_Collider.enabled = false;
                        av.BoxProdItem = item;
                    }
                }
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogInfo("box prop unavailable (using cube): " + e.Message);
            }
        }

        private static void ReleaseCards(RemoteAvatar av)
        {
            foreach (var c in av.HeldCards3d)
                if (c != null)
                {
                    try
                    {
                        c.OnDestroyed();
                    }
                    catch (System.Exception e) { Swallow.Log(e); } // game's own card despawn path
                }
            av.HeldCards3d.Clear();
            av.CardSig = "";
        }

        /// <summary>A pack-opening happened: put the actual pack in the avatar's hands
        /// briefly and play the grab motion.</summary>
        public void ShowPackOpen(int connId, int packIndex)
        {
            if (!_avatars.TryGetValue(connId, out var av) || av.Go == null)
                return;
            av.PackTimer = 4f;
            try
            {
                if (av.Anim != null)
                    av.Anim.SetTrigger("GrabItem");
            }
            catch (System.Exception e) { Swallow.Log(e); }
            // packIndex is ALREADY A LOCAL EItemType: both callers (CoopCore's Activity and
            // RelayTag handlers) read it with Msg.ReadItemType, which is the one translation
            // boundary for it. Do NOT translate again here. A pack from a set only the
            // sender has already arrived as EItemType.None (-1), which the >= 0 guard below
            // refuses: the avatar plays the grab motion with no pack in hand.
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
                        if (item.m_Rigidbody != null)
                            item.m_Rigidbody.isKinematic = true;
                        if (item.m_Collider != null)
                            item.m_Collider.enabled = false;
                        av.PackProp = item;
                    }
                    else
                    {
                        CoopPlugin.Log.LogInfo($"pack-open visual: no mesh for pack index {packIndex}");
                    }
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
        }

        /// <summary>Called every frame from CoopCore while linked.</summary>
        public void Tick(float dt)
        {
            if (!CoopPlugin.AvatarsEnabled.Value)
                return;
            bool inGame = CSingleton<CGameManager>.Instance != null
                          && CSingleton<CGameManager>.Instance.m_IsGameLevel;
            if (!inGame)
                return;

            // billboard against the camera the player actually SEES THROUGH - the game
            // runs several cameras, and Camera.main can be one of the others, which
            // left name tags facing a phantom viewpoint (mirrored from one side)
            var cam = ViewCamera != null ? ViewCamera : Camera.main?.transform;

            foreach (var av in _avatars.Values)
            {
                if (av.Go == null)
                {
                    av.HasRenderedPosition = false;
                    av.AnimSpeed = 0f;
                    av.AppliedAnimSpeed = float.NaN;
                    av.HasMoveSpeed = false;
                    av.HasHoldingBox = false;
                    av.HoldingBoxPoseSet = false;
                    if (av.HoldPropMat != null)
                    {
                        // the body died with a scene load: pooled children went down with it,
                        // so free the instanced tint material before rebuilding the visuals.
                        Object.Destroy(av.HoldPropMat);
                        av.HoldPropMat = null;
                    }
                    // Clear these even when the material was already gone. Unity's fake-null
                    // references can otherwise leave stale props/signatures after a reload.
                    av.HoldProp = null;
                    av.PackProp = null;
                    av.BinderProp = null;
                    av.BoxProp = null;
                    av.BoxProdItem = null;
                    av.HeldItems.Clear();
                    av.HeldCards3d.Clear();
                    av.HeldSig = "";
                    av.CardSig = "";
                    av.BoxSig = "";
                    if (av.HasState)
                    {
                        long ts = Util.PerfProbe.Start();
                        TrySpawn(av);
                        Util.PerfProbe.End("avatar.spawn", ts);
                    }
                    continue;
                }

                var t = av.Go.transform;
                // snapshot interpolation: render two send intervals in the past so there is
                // almost always a newer snapshot to blend toward - motion glides at any frame
                // rate instead of stepping at packet cadence or overshooting on stops/turns
                Vector3 renderPos;
                float renderYaw;
                if (av.SnapCount > 0)
                    SampleSnapshots(av, Time.time - InterpDelay, dt, out renderPos, out renderYaw);
                else
                {
                    renderPos = av.TargetPos;
                    renderYaw = av.TargetYaw;
                }
                bool snap = (t.position - renderPos).sqrMagnitude > 25f; // 5m = teleport, don't glide
                float blend = 1f - Mathf.Exp(-14f * dt); // frame-rate independent residual smoothing
                t.position = snap ? renderPos : Vector3.Lerp(t.position, renderPos, blend);
                var targetRot = Quaternion.Euler(0f, renderYaw, 0f);
                t.rotation = snap ? targetRot : Quaternion.Slerp(t.rotation, targetRot, blend);

                if (av.Anim != null)
                {
                    if (av.HasMoveSpeed)
                    {
                        if (!av.HasRenderedPosition)
                        {
                            av.PreviousRenderedPosition = t.position;
                            av.AnimSpeed = 0f;
                            av.AppliedAnimSpeed = float.NaN;
                            av.HasRenderedPosition = true;
                        }
                        else
                        {
                            Vector3 renderedDelta = t.position - av.PreviousRenderedPosition;
                            renderedDelta.y = 0f;
                            float renderedSpeed = snap
                                ? 0f
                                : Mathf.Min(renderedDelta.magnitude / Mathf.Max(dt, 0.0001f), 6f);
                            av.PreviousRenderedPosition = t.position;

                            // Decelerate faster than we accelerate: a stale walking pose is
                            // much more noticeable than a slightly soft start.
                            float rate = renderedSpeed < av.AnimSpeed ? 14f : 8f;
                            av.AnimSpeed = Mathf.Lerp(av.AnimSpeed, renderedSpeed,
                                1f - Mathf.Exp(-rate * dt));
                            if (av.AnimSpeed < 0.05f)
                                av.AnimSpeed = 0f;
                        }

                        if (float.IsNaN(av.AppliedAnimSpeed)
                            || Mathf.Abs(av.AppliedAnimSpeed - av.AnimSpeed) > 0.01f)
                        {
                            av.Anim.SetFloat(MoveSpeedHash, av.AnimSpeed);
                            av.AppliedAnimSpeed = av.AnimSpeed;
                        }
                    }
                    if (av.HasHoldingBox)
                    {
                        bool holding = av.HoldState == 1;
                        if (!av.HoldingBoxPoseSet || av.HoldingBoxPose != holding)
                        {
                            av.Anim.SetBool(IsHoldingBoxHash, holding);
                            av.HoldingBoxPose = holding;
                            av.HoldingBoxPoseSet = true;
                        }
                    }
                }
                // carried visuals: the REAL box (with its product on top) when carrying one
                bool showBox = av.HoldState == 1;
                if (av.PendingBoxSig != av.BoxSig)
                {
                    ReleaseBoxProp(av);
                    av.BoxSig = av.PendingBoxSig;
                    if (showBox)
                    {
                        bool isBig = av.HoldTypes.Count >= 1 && av.HoldTypes[0] == 1;
                        int prodType = av.HoldTypes.Count >= 2 ? av.HoldTypes[1] : 0;
                        TrySpawnBoxProp(av, isBig, prodType);
                    }
                }
                // generic cube only as fallback when the real prefab wasn't available
                bool showCube = showBox && av.BoxProp == null;
                if (av.HoldProp != null && av.HoldProp.activeSelf != showCube)
                {
                    av.HoldProp.SetActive(showCube);
                    av.HoldProp.transform.localScale = new Vector3(0.34f, 0.27f, 0.34f);
                }
                // loose cards fanned in hand (real card faces, modded expansions included)
                if (av.PendingCardSig != av.CardSig)
                {
                    ReleaseCards(av);
                    av.CardSig = av.PendingCardSig;
                    if (av.CardSig.Length > 0 && av.HoldCards != null)
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
                            catch (System.Exception e) { Swallow.Log(e); }
                        }
                    }
                }

                // pack-opening prop times out on its own
                if (av.PackProp != null)
                {
                    av.PackTimer -= dt;
                    if (av.PackTimer <= 0f)
                    {
                        try
                        {
                            ItemSpawnManager.DisableItem(av.PackProp);
                        }
                        catch (System.Exception e) { Swallow.Log(e); }
                        av.PackProp = null;
                    }
                }

                // reading the collection binder
                bool wantBinder = av.HoldState == 4;
                if (wantBinder && av.BinderProp == null)
                    TrySpawnBinder(av);
                if (av.BinderProp != null && av.BinderProp.activeSelf != wantBinder)
                    av.BinderProp.SetActive(wantBinder);

                if (av.PendingItemSig != av.HeldSig)
                {
                    ReleaseItems(av); // items only: growing the pile must not kill pack/binder/box visuals
                    av.HeldSig = av.PendingItemSig;
                    if (av.HeldSig.Length > 0)
                    {
                        for (int i = 0; i < av.HoldTypes.Count; i++)
                        {
                            try
                            {
                                // TEST THE SENTINEL EXPLICITLY - the null guard below does NOT
                                // catch it. InventoryBase.GetItemMeshData(EItemType.None) returns
                                // a BLANK `new ItemMeshData()`, which is non-null, so an item from
                                // a content pack this PC does not have would build a real pooled
                                // prop with no mesh instead of being skipped.
                                if (av.HoldTypes[i] == (int)EItemType.None)
                                    continue;
                                var meshData = InventoryBase.GetItemMeshData((EItemType)av.HoldTypes[i]);
                                if (meshData == null)
                                    continue;
                                var item = ItemSpawnManager.GetItem(av.Go.transform);
                                item.SetMesh(meshData.mesh, meshData.material, (EItemType)av.HoldTypes[i],
                                    meshData.meshSecondary, meshData.materialSecondary, meshData.materialList);
                                // a carried pile: outward from the chest, each tucked behind
                                // the previous with a slight rise and lean - not a totem pole
                                item.transform.localPosition = new Vector3(0f, 1.04f + 0.018f * i, 0.36f + 0.055f * i);
                                item.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
                                item.gameObject.SetActive(true);
                                if (item.m_Rigidbody != null)
                                    item.m_Rigidbody.isKinematic = true;
                                if (item.m_Collider != null)
                                    item.m_Collider.enabled = false;
                                av.HeldItems.Add(item);
                            }
                            catch (System.Exception e) { Swallow.Log(e); }
                        }
                    }
                }

                if (cam != null)
                {
                    if (av.NameTag != null)
                        av.NameTag.transform.rotation =
                            Quaternion.LookRotation(av.NameTag.transform.position - cam.position);
                    if (av.EmoteTag != null)
                        av.EmoteTag.transform.rotation =
                            Quaternion.LookRotation(av.EmoteTag.transform.position - cam.position);
                }

                if (av.EmoteTimer > 0f)
                {
                    av.EmoteTimer -= dt;
                    if (av.EmoteTimer <= 0f && av.EmoteTag != null)
                        av.EmoteTag.text = "";
                }
            }
        }

        /// <summary>Sample the snapshot buffer at renderTime (playback delayed by InterpDelay).
        /// Extrapolates only when the buffer runs dry, capped at MaxExtrapolation with the
        /// velocity easing to zero (exp(-3*dt)) so a stopped or turning player never overshoots
        /// and rubber-bands back when the next packet lands.</summary>
        private static void SampleSnapshots(RemoteAvatar av, float renderTime, float dt,
            out Vector3 pos, out float yaw)
        {
            var newest = av.Snaps[av.SnapHead];
            if (renderTime >= newest.RecvTime)
            {
                av.Velocity *= Mathf.Exp(-3f * dt);
                float age = Mathf.Min(renderTime - newest.RecvTime, MaxExtrapolation);
                pos = newest.Pos + av.Velocity * age;
                yaw = newest.Yaw;
                return;
            }
            int oldest = (av.SnapHead - av.SnapCount + 1 + SnapBufferSize) % SnapBufferSize;
            var prev = av.Snaps[oldest];
            for (int i = 1; i < av.SnapCount; i++)
            {
                var next = av.Snaps[(oldest + i) % SnapBufferSize];
                if (renderTime <= next.RecvTime)
                {
                    float span = next.RecvTime - prev.RecvTime;
                    float u = span > 0.0001f ? (renderTime - prev.RecvTime) / span : 1f;
                    pos = Vector3.Lerp(prev.Pos, next.Pos, u);         // clamps u below 0 (renderTime older than buffer)
                    yaw = Mathf.LerpAngle(prev.Yaw, next.Yaw, u);      // wraps correctly through 360
                    return;
                }
                prev = next;
            }
            pos = prev.Pos; // single-snapshot buffer, not yet time to show it: hold the pose
            yaw = prev.Yaw;
        }

        private void TrySpawn(RemoteAvatar av)
        {
            if (_customers == null)
                _customers = Object.FindObjectOfType<CustomerManager>();
            var cm = _customers;
            if (cm == null)
                return;

            // Stable gender pick per player name, so each remote player keeps a consistent look.
            int nameHash = 17;
            foreach (char c in av.Name)
                nameHash = nameHash * 31 + c;
            bool female = (nameHash & 1) == 1;
            var prefab = female ? cm.m_CustomerFemalePrefab : cm.m_CustomerPrefab;
            if (av.HasModel)
            {
                female = av.Female;
                prefab = female ? cm.m_CustomerFemalePrefab : cm.m_CustomerPrefab;
            }
            if (prefab == null)
                prefab = cm.m_CustomerPrefab != null ? cm.m_CustomerPrefab : cm.m_CustomerFemalePrefab;
            if (prefab == null)
                return;

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
                if (cust != null)
                {
                    bool hasJson = !string.IsNullOrEmpty(av.CustomizationJson);
                    if (BoxShared.Debug)
                    {
                        string apparelDbg = "n/a";
                        if (hasJson)
                        {
                            try
                            {
                                var dbg = JsonConvert.DeserializeObject<CC.CC_CharacterData>(av.CustomizationJson);
                                apparelDbg = dbg == null || dbg.ApparelNames == null
                                    ? "null"
                                    : "[" + string.Join("|", dbg.ApparelNames) + "]";
                            }
                            catch (System.Exception e) { apparelDbg = "err:" + e.GetType().Name; }
                        }
                        BoxShared.DebugLog("avatar-censor",
                            $"name={av.Name} hasModel={av.HasModel} hasJson={hasJson} allow={CoopPlugin.AllowNsfw.Value} "
                            + $"nude={(hasJson ? IsNude(av.CustomizationJson).ToString() : "n/a")} apparel={apparelDbg}",
                            av.GetHashCode(), 0.5f);
                    }
                    if (!av.HasModel)
                    {
                        // No model was sent: fall back to the game's own random clothed wardrobe.
                        cust.RandomizeCharacterMesh();
                    }
                    else
                    {
                        cust.m_CharacterCustom.CharacterName = (female ? "Female" : "Male") + av.ModelIndex;
                        cust.m_CharacterCustom.Initialize();
                        CC.CC_CharacterData data = null;
                        if (hasJson)
                        {
                            try
                            {
                                data = JsonConvert.DeserializeObject<CC.CC_CharacterData>(av.CustomizationJson);
                            }
                            catch (System.Exception e) { Swallow.Log(e); }
                        }
                        if (data == null)
                        {
                            // Unreadable payload: the safe fallback is a random clothed customer.
                            cust.RandomizeCharacterMesh();
                        }
                        else
                        {
                            // NSFW off: dress only the nude slots instead of randomizing the
                            // whole character, so the player's hair/body/other clothes survive.
                            // Runs whenever NSFW is off, not only for a fully nude model, so a
                            // mixed outfit (top on, bottom nude) is censored too.
                            if (!CoopPlugin.AllowNsfw.Value)
                                ClotheNudeSlots(cust.m_CharacterCustom, data);
                            NormalizeCharacterData(cust.m_CharacterCustom, data);
                            cust.m_CharacterCustom.StoredCharacterData = data;
                            if (TryApplyCharacterData(cust.m_CharacterCustom, data, "remote avatar"))
                                ClearEmptyWardrobeSlots(cust.m_CharacterCustom, data);
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                CoopPlugin.Log.LogWarning("Avatar dressing failed (spawning undressed): " + e.Message);
            }

            // Customer prefabs include held-item and FX props; avatars are visual-only.
            HideCustomerProps(cust, "remote avatar");

            // Strip game logic but KEEP the cosmetic rig helpers (CC namespace): CopyPose
            // drives hair/apparel bones every LateUpdate - destroying it is why hair froze.
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                    continue;
                string tn = mb.GetType().Name;
                if (tn == "CopyPose" || tn == "BlendshapeManager" || tn == "ScaleCharacter"
                    || tn == "TransformBone" || tn == "MipBiasAdjust")
                    continue;
                Object.DestroyImmediate(mb);
            }
            foreach (var comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null)
                    continue;
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
            av.HasMoveSpeed = false;
            av.HasHoldingBox = false;
            av.HasRenderedPosition = false;
            av.AnimSpeed = 0f;
            av.AppliedAnimSpeed = float.NaN;
            av.HoldingBoxPoseSet = false; // fresh Animator: the pose must be pushed once

            if (av.Anim != null)
            {
                foreach (var p in av.Anim.parameters)
                {
                    if (p.name == "MoveSpeed" && p.type == AnimatorControllerParameterType.Float)
                        av.HasMoveSpeed = true;
                    if (p.name == "IsHoldingBox" && p.type == AnimatorControllerParameterType.Bool)
                        av.HasHoldingBox = true;
                }
                if (!_loggedAnimParams)
                {
                    _loggedAnimParams = true;
                    var sb = new StringBuilder("Avatar animator params: ");
                    foreach (var p in av.Anim.parameters)
                        sb.Append(p.name).Append(' ');
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
            if (mr != null)
            {
                // .material instances a clone; keep the handle so DestroyBody can free it
                av.HoldPropMat = mr.material;
                av.HoldPropMat.color = new Color(0.72f, 0.55f, 0.35f); // cardboard
            }
            prop.SetActive(false);
            av.HoldProp = prop;

            float heightFactor = GetCharacterHeightFactor(cust != null ? cust.m_CharacterCustom : null);
            av.NameTag = MakeTag(clone.transform, av.Name, 2.25f * heightFactor, Color.white);
            av.EmoteTag = MakeTag(clone.transform, "", 2.55f * heightFactor, new Color(1f, 0.85f, 0.2f));
            CoopPlugin.Log.LogInfo($"Spawned co-op avatar for '{av.Name}' ({(female ? "female" : "male")} model)");
        }

        private static float GetCharacterHeightFactor(CC.CharacterCustomization custom)
        {
            if (custom == null || custom.StoredCharacterData == null
                || custom.StoredCharacterData.FloatProperties == null)
                return 1f;
            foreach (var property in custom.StoredCharacterData.FloatProperties)
            {
                if (property == null || property.propertyName != "Height")
                    continue;
                return Mathf.Clamp(property.floatValue, 0.5f, 1.5f);
            }
            return 1f;
        }

        private static void TrySpawnBinder(RemoteAvatar av)
        {
            try
            {
                var src = Object.FindObjectOfType<CollectionBinderFlipAnimCtrl>();
                if (src == null)
                    return;
                var holder = new GameObject("CoopBinderHolder_tmp");
                holder.SetActive(false);
                var clone = Object.Instantiate(src.gameObject, holder.transform);
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null)
                        Object.DestroyImmediate(mb);
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

        /// <summary>Set by CoopCore each frame: the transform of the camera the player
        /// actually renders through (Camera.main can be a different, stationary one).</summary>
        public static Transform ViewCamera;

        private static TMPro.TMP_FontAsset _tagFont;

        /// <summary>World-space TextMeshPro label. TMP's distance-field material is
        /// depth-tested, so walls occlude the tag (the legacy TextMesh font shader drew
        /// on top of everything), and the SDF glyphs stay crisp at any distance.</summary>
        private static TMPro.TMP_Text MakeTag(Transform parent, string text, float height, Color color)
        {
            var go = new GameObject("CoopTag");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, height, 0f);
            var tmp = go.AddComponent<TMPro.TextMeshPro>();
            tmp.text = text;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.fontSize = 1.8f;
            tmp.color = color;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TMPro.TextOverflowModes.Overflow;
            tmp.rectTransform.sizeDelta = new Vector2(4f, 1f);
            if (_tagFont == null)
            {
                _tagFont = TMPro.TMP_Settings.defaultFontAsset;
                if (_tagFont == null)
                {
                    // borrow the font any of the game's own TMP labels use
                    var any = UnityEngine.Object.FindObjectOfType<TMPro.TMP_Text>(true);
                    if (any != null)
                        _tagFont = any.font;
                }
            }
            if (_tagFont != null)
                tmp.font = _tagFont;
            return tmp;
        }
    }

}
