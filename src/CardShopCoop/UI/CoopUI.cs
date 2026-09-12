using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BepInEx.Configuration;
using CardShopCoop.Net;
// NO `using Steamworks;` HERE, AND NEVER AGAIN - see the same note in CoopCore.cs. OnGUI
// is the worst possible place for a type-load fault (DrawNone runs every frame on the main
// menu), so every lobby id in this file is a plain ulong and lobby rows are Net.LobbyRow.
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>Small IMGUI window: host / join / status. Toggled with the UI key.
    /// Presentation is driven by <see cref="CoopTheme"/> (cozy card-shop paper + teal).</summary>
    public class CoopUI
    {
        public bool Visible = true;
        public static bool TextFieldFocused;

        private Rect _win = new Rect(24f, 96f, 400f, 10f);
        private string _ipField;
        private string _nameField;
        private string _lanIps;
        private string _lanIpsOther;
        private readonly CopyButton _ipCopy = new CopyButton();
        private string _inviteField = "";
        private readonly CopyButton _inviteCopy = new CopyButton();
        private readonly CopyButton _passwordCopy = new CopyButton();
        private string _lanPwField = "";
        private bool _characterOpen = true;
        private enum CoopTab
        {
            Session,
            Character,
            Settings,
        }
        private CoopTab _tab = CoopTab.Session;
        private Vector2 _sessionScroll;
        private Vector2 _characterScroll;
        private Vector2 _settingsScroll;
        private bool _characterSnapshotReady;
        private bool _characterFemale;
        private readonly List<string> _presetNames = new List<string>();
        private int _presetSelection;
        private readonly List<int> _presetSelectionBuffer = new List<int>(1) { 0 };
        private int _characterSnapshotGeneration = -1;
        private CC.CharacterCustomization _characterCustomizer;
        private readonly List<List<string>> _hairOptions = new List<List<string>>();
        private readonly List<int> _hairSelections = new List<int>();
        private readonly List<Color> _hairColors = new List<Color>();
        private readonly List<List<string>> _apparelOptions = new List<List<string>>();
        private readonly List<List<string>> _apparelNames = new List<List<string>>();
        private readonly List<int> _apparelSelections = new List<int>();
        private readonly List<int> _apparelMaterials = new List<int>();
        private readonly List<Color> _apparelColors = new List<Color>();
        private int _colorEditorKind = -1;
        private int _colorEditorSlot = -1;
        private Texture2D _svPickerTexture;
        private Texture2D _huePickerTexture;
        private float _pickerTextureHue = -1f;
        private int _colorDragTarget;
        private readonly List<string> _floatNames = new List<string>();
        private readonly List<float> _floatValues = new List<float>();

        // LATCHED COPIES OF THE INVITE FIELDS, and the reason they exist is IMGUI's two-pass
        // model rather than anything about threads. GUILayout matches the Layout pass against
        // the Repaint pass BY CONTROL INDEX: if a field changes value between the two passes
        // and that value decides whether a row is drawn, the counts disagree and Unity throws
        // "GUILayout: Mismatched LayoutGroup" over the whole window. CoopCore now publishes
        // these on the main thread, which makes a mid-frame change unlikely rather than
        // impossible (a queued publish drains in Update, and OnGUI can run more than once per
        // Update). Latching in Layout only - the same trick _lanIps already uses - makes both
        // passes draw from one immutable snapshot, so the question cannot arise.
        private InviteState _invState;
        private string _invCode, _invReason, _invPassword;
        private int _invPortState;

        // lobby browser + host options
        private bool _browserOpen;
        private string _searchField = "";
        private int _page;
        private bool _publicLobby;
        private string _lobbyNameField = "";
        private string _hostPwField = "";
        private string _joinPwField = "";
        private ulong _pwPromptLobby; // 0 = no password prompt open
        private const int PageSize = 6;
        private const float MaxWindowHeight = 720f;
        // Keep the complete window clear of the screen edges so a resize cannot push the
        // footer off-screen. The window's own height is set from this, not from its children.
        private const float WindowScreenMargin = 16f;
        // The scroll viewport measured on the last Repaint. WindowFn seeds the shared min-height
        // wrapper from it so the session's last panel can fill the tab, without guessing the
        // header/tabs/footer size.
        private float _viewportHeight;

        // OnGUI runs 2+ times per frame; GUIStyle construction and string interpolation there
        // is steady per-frame garbage. Styles/textures live in CoopTheme (built once, cached);
        // the HUD display strings - and the GUIContent wrappers the pills measure - are rebuilt
        // only when their source line changes.
        private string _errorSeen, _errorText; private GUIContent _errorGc;
        private string _enumRestoreMsg; // outcome line under the enum-lend notice
        private string _hostTimeSeen, _hostTimeText; private GUIContent _hostTimeGc;
        private string _registerSeen, _registerText; private GUIContent _registerGc;
        private KeyCode _toggleKeySeen = KeyCode.None;
        private string _toggleHint;

        /// <summary>Lower = more likely the real home-LAN address.</summary>
        private static int IpRank(string ip)
        {
            if (ip.StartsWith("192.168."))
                return 0;             // classic home router
            if (ip.StartsWith("10."))
                return 1;                   // some routers/VPNs
            if (ip.StartsWith("172."))
                return 2;                  // usually WSL/Hyper-V/Docker
            return 3;
        }

        public void Draw(CoopCore core, ICoopTransport net)
        {
            CoopTheme.EnsureBuilt();
            if (_ipField == null)
                _ipField = CoopPlugin.LastJoinIP.Value;
            if (_nameField == null)
                _nameField = core.EffectivePlayerName;

            // ---- HUD overlays (outside the window) ----
            if (!Visible)
            {
                // a hidden window cannot have a focused coop_ field, but IMGUI keeps the
                // last focus NAME alive after the window stops drawing - without this
                // reset, closing the window right after typing left TextFieldFocused
                // stuck TRUE and silently ate the serve key for the rest of the session
                TextFieldFocused = false;
                if (core.ErrorLine.Length > 0)
                {
                    if (core.ErrorLine != _errorSeen)
                    {
                        _errorSeen = core.ErrorLine;
                        _errorText = $"<size=13><color=#ff5a4a>CO-OP: {core.ErrorLine}</color></size>";
                        _errorGc = new GUIContent(_errorText);
                    }
                    Vector2 errSize = CoopTheme.PillSize(CoopTheme.HudPill, _errorGc, 660f);
                    GUI.Label(new Rect(8f, Screen.height - errSize.y - 6f, errSize.x, errSize.y),
                        _errorGc, CoopTheme.HudPill);
                }
            }
            if (CoopCore.Role == CoopRole.Client && core.HostTimeLine.Length > 0)
            {
                if (core.HostTimeLine != _hostTimeSeen)
                {
                    _hostTimeSeen = core.HostTimeLine;
                    _hostTimeText = $"<color=#ffd54a>{core.HostTimeLine} - co-op</color>";
                    _hostTimeGc = new GUIContent(_hostTimeText);
                }
                Vector2 sz = CoopTheme.PillSize(CoopTheme.HudPill, _hostTimeGc, 440f);
                GUI.Label(new Rect((Screen.width - sz.x) / 2f, 4f, sz.x, sz.y), _hostTimeGc, CoopTheme.HudPill);
            }
            if (core.RegisterLine.Length > 0)
            {
                if (core.RegisterLine != _registerSeen)
                {
                    _registerSeen = core.RegisterLine;
                    _registerText = $"<size=18><color=#8ef58a>{core.RegisterLine}</color></size>";
                    _registerGc = new GUIContent(_registerText);
                }
                Vector2 sz = CoopTheme.PillSize(CoopTheme.HudPillBig, _registerGc, 740f);
                GUI.Label(new Rect((Screen.width - sz.x) / 2f, Screen.height * 0.63f, sz.x, sz.y),
                    _registerGc, CoopTheme.HudPillBig);
            }
            if (!Visible)
            {
                core.SetCharacterPreview(false);
                return;
            }

            // The window sets its own height here rather than deriving it from its children: cap
            // it at MaxWindowHeight and at the room the game window actually has, then hand that
            // to GUILayout.Window. The content panel below simply expands into what is left, so
            // the full window (header, tabs, content, footer) always fits after Alt+Enter.
            _win.width = Mathf.Min(400f, Mathf.Max(1f, Screen.width - 32f));
            _win.height = TargetWindowHeight();
            _win.x = Mathf.Clamp(_win.x, 0f, Mathf.Max(0f, Screen.width - _win.width));
            _win.y = Mathf.Clamp(_win.y, 0f, Mathf.Max(0f, Screen.height - _win.height));
            CoopTheme.DrawWindowShadow(_win); // soft drop shadow behind the window (screen space)
            _win = GUILayout.Window(867530, _win, id => WindowFn(core, net), "", CoopTheme.Window,
                GUILayout.Width(_win.width), GUILayout.Height(_win.height));
            // Clamp again after layout: the layout option should already hold the size, but the
            // pre-call clamp only had the previous frame's height.
            _win.x = Mathf.Clamp(_win.x, 0f, Mathf.Max(0f, Screen.width - _win.width));
            _win.y = Mathf.Clamp(_win.y, 0f, Mathf.Max(0f, Screen.height - _win.height));
        }

        private void WindowFn(CoopCore core, ICoopTransport net)
        {
            CoopTheme.EnsureBuilt();
            CoopTheme.DrawWindowChrome(new Rect(0f, 0f, _win.width, _win.height),
                CoopPlugin.Name.ToUpperInvariant(), "v" + CoopPlugin.Version);
            CoopTheme.DrawConnectionIndicator(new Rect(_win.width - 30f, 9f, 12f, 12f),
                GetConnectionState(core, net));

            // A role change can leave the CHARACTER tab selected while it is no longer
            // available (disconnect); normalize before drawing so the highlight, the selected
            // content, and the scroll offset all agree.
            if (_tab == CoopTab.Character && CoopCore.Role == CoopRole.None)
                _tab = CoopTab.Session;

            DrawTabs(core);
            core.SetCharacterPreview(_tab == CoopTab.Character && CoopCore.Role != CoopRole.None);

            // The window height is set on GUILayout.Window itself, so nothing here measures the
            // header, tabs or footer. The content panel expands into whatever the window has
            // left, and the single scroll view fills the panel. Every tab shares this one scroll
            // view (nested scroll views are a layout hazard) with a separate offset per tab;
            // zero-padding keeps the overflow calculation exact, so the vertical bar only
            // appears when the content actually overflows.
            GUILayout.BeginVertical(CoopTheme.ContentPanel, GUILayout.ExpandHeight(true));
            Vector2 scroll = _tab == CoopTab.Character ? _characterScroll
                : _tab == CoopTab.Settings ? _settingsScroll : _sessionScroll;
            scroll = GUILayout.BeginScrollView(scroll, CoopTheme.ScrollView, GUILayout.ExpandHeight(true));
            // Everything in the scroll view shares this minimum-height wrapper, seeded from the
            // viewport measured on the previous Repaint. A short tab therefore fills the space
            // (its last panel can expand) while a tall one grows past it and scrolls. The PROBLEM
            // banner is inside it too, so a wrapped error counts as content instead of forcing a
            // scrollbar on its own.
            GUILayout.BeginVertical(CoopTheme.ScrollContent, GUILayout.MinHeight(_viewportHeight));
            if (core.ErrorLine.Length > 0)
            {
                GUILayout.BeginHorizontal();
                CoopTheme.Chip("PROBLEM", CoopTheme.ChipDanger);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label(core.ErrorLine, CoopTheme.LabelDanger);
                GUILayout.Space(4f);
            }
            if (_tab == CoopTab.Character && CoopCore.Role != CoopRole.None)
                DrawCharacterSelector(core);
            else if (_tab == CoopTab.Settings)
                DrawSettings(core);
            else
                DrawSession(core, net);
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            // Remember the viewport the scroll view actually received so the next pass can size
            // the wrapper above. Measured on Repaint: during Layout the rect is not usable yet.
            // Guarded so a bogus value can never inflate the content into a permanent scrollbar.
            if (Event.current.type == EventType.Repaint)
            {
                float measured = GUILayoutUtility.GetLastRect().height;
                if (measured > 0f && measured <= _win.height)
                    _viewportHeight = measured;
            }
            if (_tab == CoopTab.Character)
                _characterScroll = scroll;
            else if (_tab == CoopTab.Settings)
                _settingsScroll = scroll;
            else
                _sessionScroll = scroll;
            GUILayout.EndVertical();

            FinishWindow();
        }

        /// <summary>The SESSION tab body: custom-card-database notices, the role-specific
        /// host/join/status panels, and the graded-adopt prompt. The shared min-height wrapper in
        /// WindowFn lets the last panel expand to fill the tab container.</summary>
        private void DrawSession(CoopCore core, ICoopTransport net)
        {
            // The restore OUTCOME lives out here, NOT inside the lend block below. A successful
            // restore (and the no-backup branch, which also clears the marker) makes
            // EnumLendState() return null, so a message drawn inside that block would be drawn
            // for zero frames - the button appeared to do nothing at all. Out here it stays put
            // until the player closes the window.
            if (_enumRestoreMsg != null)
            {
                GUILayout.BeginHorizontal();
                CoopTheme.Chip("CARD DATABASE", CoopTheme.ChipWarn);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label(_enumRestoreMsg, CoopTheme.LabelWarn);
                GUILayout.Space(4f);
            }

            // Custom-card database on loan: a mismatched-enum join replaced the machine-global
            // registry with the HOST's copy (with a backup). Until it is restored, the player's
            // own modded SOLO saves fail to load ("data lost") - the exact field report this
            // notice exists for. Show the warning + one-click restore whenever the marker says
            // the on-disk registry is the host's.
            string lend = CoopCore.EnumLendState();
            if (lend != null)
            {
                GUILayout.BeginHorizontal();
                CoopTheme.Chip("CARD DATABASE", CoopTheme.ChipWarn);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label(lend, CoopTheme.LabelWarn);
                // (the restore outcome is rendered above, outside this block, because a
                // successful restore removes the very banner it would otherwise sit under)
                // restoring mid-session would break the CURRENT co-op world's custom cards;
                // only offer it when not connected
                if (CoopCore.Role == CoopRole.None
                    && GUILayout.Button("Restore MY card database (for solo saves - restart after)", CoopTheme.ButtonDanger))
                {
                    Util.ModParity.RestoreEnumBackup(out _enumRestoreMsg);
                }
                GUILayout.Space(4f);
            }

            switch (CoopCore.Role)
            {
                case CoopRole.None:
                    DrawNone(core);
                    break;
                case CoopRole.Host:
                    DrawHost(core, net);
                    break;
                case CoopRole.Client:
                    DrawClient(core);
                    break;
            }

            // Out here rather than inside the three role branches so it reaches all of them with
            // one call: the Steam host path returns early from DrawHost, and Role None simply has
            // no offers to draw.
            DrawGradedAdopt(core);
        }

        /// <summary>Shared footer for every tab: latch text-field focus, show the
        /// toggle-key hint below the content panel, and keep the header draggable.</summary>
        private void FinishWindow()
        {
            string focused = GUI.GetNameOfFocusedControl();
            TextFieldFocused = focused != null && focused.StartsWith("coop_");
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(ToggleHintText(), CoopTheme.LabelDim);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        /// <summary>Cached "Press &lt;key&gt; to toggle this window" line; rebuilt only when
        /// the configured key changes because OnGUI runs multiple times per frame.</summary>
        private string ToggleHintText()
        {
            KeyCode key = CoopPlugin.UiToggleKey != null ? CoopPlugin.UiToggleKey.Value : KeyCode.None;
            if (_toggleHint == null || key != _toggleKeySeen)
            {
                _toggleKeySeen = key;
                _toggleHint = key == KeyCode.None
                    ? "Press the toggle key to toggle this window"
                    : $"Press {key} to toggle this window";
            }
            return _toggleHint;
        }

        /// <summary>The complete outer window height: capped at <see cref="MaxWindowHeight"/>
        /// and at the space the game window actually has, so the whole window (header, tabs,
        /// content and footer) always fits after the game switches display mode.</summary>
        private static float TargetWindowHeight()
        {
            // Never larger than the screen, and never a cap smaller than the floor we hand to
            // Clamp; on any real resolution the screen term comfortably dominates.
            float max = Mathf.Max(1f, Mathf.Min(MaxWindowHeight, Screen.height - WindowScreenMargin));
            float min = Mathf.Min(320f, max);
            return Mathf.Clamp(Screen.height * 0.72f, min, max);
        }

        private void DrawTabs(CoopCore core)
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = _tab != CoopTab.Session;
            if (GUILayout.Button("SESSION", _tab == CoopTab.Session ? CoopTheme.TabSelected : CoopTheme.Tab,
                GUILayout.Width(88f)))
                _tab = CoopTab.Session;
            GUI.enabled = CoopCore.Role != CoopRole.None && _tab != CoopTab.Character;
            if (GUILayout.Button("CHARACTER", _tab == CoopTab.Character ? CoopTheme.TabSelected : CoopTheme.Tab,
                GUILayout.Width(104f)))
                _tab = CoopTab.Character;
            GUI.enabled = _tab != CoopTab.Settings;
            if (GUILayout.Button("SETTINGS", _tab == CoopTab.Settings ? CoopTheme.TabSelected : CoopTheme.Tab,
                GUILayout.Width(96f)))
                _tab = CoopTab.Settings;
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>The SETTINGS tab: appearance, live logging switches, and the artificial
        /// latency test sliders. Every control writes straight to its ConfigEntry, which
        /// persists to disk, so the tab is a front-end for the config file rather than a
        /// second source of truth.</summary>
        private void DrawSettings(CoopCore core)
        {
            GUILayout.Label("APPEARANCE", CoopTheme.SectionHeader);
            bool allowNsfw = GUILayout.Toggle(CoopPlugin.AllowNsfw.Value,
                CoopPlugin.AllowNsfw.Value ? "NSFW allowed" : "NSFW hidden", CoopTheme.Toggle);
            if (allowNsfw != CoopPlugin.AllowNsfw.Value)
                core.SetNsfwAllowed(allowNsfw);
            GUILayout.Space(8f);

            GUILayout.Label("LOGGING", CoopTheme.SectionHeader);
            DrawConfigToggle(CoopPlugin.BoxSyncDebug, "Verbose box-sync logging (BoxSyncDebug)");
            DrawConfigToggle(CoopPlugin.PerfDebug, "Per-frame stage timing (PerfDebug)");
            GUILayout.Space(8f);

            GUILayout.Label("LATENCY TESTING", CoopTheme.SectionHeader);
            DrawConfigIntSlider(CoopPlugin.ArtificialLagMs, 0, 2000, "Artificial lag");
            DrawConfigIntSlider(CoopPlugin.ArtificialJitterMs, 0, 1000, "Jitter");
            GUILayout.Space(8f);

            GUILayout.Label("Settings are saved to BepInEx/config/com.zwhit.cardshopcoop.cfg.", CoopTheme.LabelDim);
        }

        private static void DrawConfigToggle(ConfigEntry<bool> entry, string label)
        {
            if (entry == null)
                return;
            bool value = GUILayout.Toggle(entry.Value, " " + label, CoopTheme.Toggle);
            if (value != entry.Value)
                entry.Value = value; // ConfigEntry writes the file itself
        }

        private static void DrawConfigIntSlider(ConfigEntry<int> entry, int min, int max, string label)
        {
            if (entry == null)
                return;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(130f));
            int value = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.Value, min, max));
            GUILayout.Label(value + " ms", CoopTheme.LabelDim, GUILayout.Width(56f));
            GUILayout.EndHorizontal();
            if (value != entry.Value)
                entry.Value = value; // ConfigEntry writes the file itself
        }

        private static int GetConnectionState(CoopCore core, ICoopTransport net)
        {
            if (CoopCore.Role == CoopRole.Host)
                return (net?.ConnectionCount ?? 0) > 0 ? 2 : 1;
            if (CoopCore.Role == CoopRole.Client)
            {
                string status = core.StatusLine ?? "";
                return status.IndexOf("Connecting", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("loading", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2;
            }
            return 0;
        }

        private void DrawCharacterSelector(CoopCore core)
        {
            if (Event.current.type == EventType.Layout
                && (!_characterSnapshotReady || _characterSnapshotGeneration != core.PlayerModelGeneration))
            {
                var model = core.GetLocalPlayerModel();
                _characterFemale = model.Female;
                _presetNames.Clear();
                _presetNames.AddRange(core.GetLocalPresetNames());
                _presetSelection = _presetNames.FindIndex(x => x == (_characterFemale ? "Female" : "Male") + model.ModelIndex);
                if (_presetSelection < 0)
                    _presetSelection = 0;
                _presetSelectionBuffer[0] = _presetSelection;
                _characterCustomizer = core.GetLocalCustomization();
                CacheWardrobeControls();
                _characterSnapshotReady = true;
                _characterSnapshotGeneration = core.PlayerModelGeneration;
            }
            if (!_characterSnapshotReady)
                return;

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.BeginHorizontal();
            CoopTheme.Chip("MY CHARACTER", CoopTheme.ChipInfo);
            GUILayout.FlexibleSpace();
            _characterOpen = GUILayout.Toggle(_characterOpen, _characterOpen ? "▲" : "▼", CoopTheme.Toggle, GUILayout.Width(28f));
            GUILayout.EndHorizontal();
            if (_characterOpen)
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = core.CanUndoPlayerModel;
                if (GUILayout.Button("Undo", CoopTheme.ButtonSecondary, GUILayout.Width(58f)))
                    core.UndoPlayerModel();
                GUI.enabled = core.CanRedoPlayerModel;
                if (GUILayout.Button("Redo", CoopTheme.ButtonSecondary, GUILayout.Width(58f)))
                    core.RedoPlayerModel();
                GUI.enabled = true;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Gender", CoopTheme.Label, GUILayout.Width(56f));
                if (GUILayout.Button(_characterFemale ? "Female" : "Male", CoopTheme.ButtonSecondary))
                {
                    _characterFemale = !_characterFemale;
                    core.SetLocalPlayerModel(_characterFemale, 0);
                }
                GUILayout.EndHorizontal();
                if (_presetNames.Count > 0)
                {
                    DrawCycleRow("Preset", _presetNames, _presetSelectionBuffer, 0,
                        () => { _presetSelection = _presetSelectionBuffer[0]; core.ApplyLocalPreset(_presetNames[_presetSelection]); });
                }
                else
                    GUILayout.Label("No presets found for this gender.", CoopTheme.LabelDimWrap);

                DrawWardrobeControls(core);
            }
            GUILayout.EndVertical();
            GUILayout.Space(4f);
        }

        private void CacheWardrobeControls()
        {
            _hairOptions.Clear();
            _hairSelections.Clear();
            _hairColors.Clear();
            _apparelOptions.Clear();
            _apparelNames.Clear();
            _apparelSelections.Clear();
            _apparelMaterials.Clear();
            _apparelColors.Clear();
            _floatNames.Clear();
            _floatValues.Clear();
            if (_characterCustomizer == null)
                return;
            var data = _characterCustomizer.StoredCharacterData;
            if (data == null)
                return;

            for (int slot = 0; slot < _characterCustomizer.HairTables.Count; slot++)
            {
                var options = new List<string> { "None" };
                var table = _characterCustomizer.HairTables[slot];
                if (table != null && table.Hairstyles != null)
                    foreach (var hair in table.Hairstyles)
                        options.Add(hair.Name ?? "(unnamed)");
                _hairOptions.Add(options);
                string current = slot < data.HairNames.Count ? data.HairNames[slot] : "";
                int selected = options.FindIndex(x => x == current);
                _hairSelections.Add(selected < 0 ? 0 : selected);
                _hairColors.Add(ReadHairColor(data, slot));
            }

            for (int slot = 0; slot < _characterCustomizer.ApparelTables.Count; slot++)
            {
                var options = new List<string>();
                var names = new List<string>();
                // The Nude slot is only offered while NSFW is allowed; otherwise the model
                // is guaranteed clothed, so the empty option must not be selectable.
                if (CoopPlugin.AllowNsfw.Value)
                {
                    options.Add("Nude");
                    names.Add("");
                }
                var table = _characterCustomizer.ApparelTables[slot];
                if (table != null && table.Items != null)
                    foreach (var item in table.Items)
                    {
                        options.Add(string.IsNullOrEmpty(item.DisplayName) ? (item.Name ?? "(unnamed)") : item.DisplayName);
                        names.Add(item.Name ?? "");
                    }
                _apparelOptions.Add(options);
                _apparelNames.Add(names);
                string current = slot < data.ApparelNames.Count ? data.ApparelNames[slot] : "";
                int selected = names.IndexOf(current);
                _apparelSelections.Add(selected < 0 ? 0 : selected);
                _apparelMaterials.Add(slot < data.ApparelMaterials.Count ? Mathf.Max(0, data.ApparelMaterials[slot]) : 0);
                _apparelColors.Add(ReadApparelTint(data, slot));
            }

            if (data.FloatProperties != null)
                foreach (var property in data.FloatProperties)
                {
                    if (property == null || string.IsNullOrEmpty(property.propertyName))
                        continue;
                    _floatNames.Add(property.propertyName);
                    _floatValues.Add(property.floatValue);
                }
        }

        private void DrawWardrobeControls(CoopCore core)
        {
            if (_characterCustomizer == null)
                return;
            if (_hairOptions.Count > 0 || _apparelOptions.Count > 0)
                GUILayout.Label("WARDROBE", CoopTheme.SectionHeader);
            for (int slot = 0; slot < _hairOptions.Count; slot++)
                DrawCycleColorRow("Hair " + (slot + 1), _hairOptions[slot], _hairSelections, slot,
                    _hairColors[slot], 0, slot, color => { _hairColors[slot] = color; core.SetLocalHairColor(slot, color); },
                    () => ApplyHair(core, slot));
            for (int slot = 0; slot < _apparelOptions.Count; slot++)
            {
                // With NSFW off a slot with no real items offers nothing selectable.
                if (_apparelOptions[slot].Count == 0)
                    continue;
                string label = _characterCustomizer.ApparelTables[slot] != null
                    && !string.IsNullOrEmpty(_characterCustomizer.ApparelTables[slot].Label)
                    ? _characterCustomizer.ApparelTables[slot].Label : "Apparel " + (slot + 1);
                DrawCycleColorRow(label, _apparelOptions[slot], _apparelSelections, slot,
                    _apparelColors[slot], 1, slot, color => { _apparelColors[slot] = color; core.SetLocalApparelTint(slot, color); },
                    () => ApplyApparel(core, slot));
                string selectedApparel = _apparelNames[slot][_apparelSelections[slot]];
                if (!string.IsNullOrEmpty(selectedApparel))
                {
                    var table = _characterCustomizer.ApparelTables[slot];
                    // When the Nude slot is offered it occupies index 0, so the item index
                    // is offset by one; with NSFW off the list starts at the first real item.
                    int itemIndex = _apparelSelections[slot] - (CoopPlugin.AllowNsfw.Value ? 1 : 0);
                    int materialCount = table != null && itemIndex >= 0 && itemIndex < table.Items.Count
                        && table.Items[itemIndex].Materials != null ? table.Items[itemIndex].Materials.Count : 0;
                    if (materialCount > 1)
                        DrawNumberRow("Material", materialCount, _apparelMaterials, slot,
                            () => ApplyApparel(core, slot));
                }
            }
            for (int i = 0; i < _floatNames.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(_floatNames[i], CoopTheme.LabelDim, GUILayout.Width(130f));
                float min = (_floatNames[i] == "Height" || _floatNames[i] == "Width") ? 0.5f : 0f;
                float max = (_floatNames[i] == "Height" || _floatNames[i] == "Width") ? 1.5f : 1f;
                float value = GUILayout.HorizontalSlider(_floatValues[i], min, max);
                GUILayout.Label(value.ToString("0.00"), CoopTheme.LabelDim, GUILayout.Width(36f));
                GUILayout.EndHorizontal();
                if (!Mathf.Approximately(value, _floatValues[i]))
                {
                    _floatValues[i] = value;
                    var prop = new CC.CC_Property { propertyName = _floatNames[i], floatValue = value };
                    _characterCustomizer.setFloatProperty(prop, true);
                    core.CommitLocalCustomization();
                }
            }
        }

        private void DrawCycleRow(string label, List<string> options, List<int> selections, int index,
            Action apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(86f));
            if (GUILayout.Button("<", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + options.Count - 1) % options.Count;
                apply();
            }
            GUILayout.Label(options[selections[index]], CoopTheme.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + 1) % options.Count;
                apply();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawCycleColorRow(string label, List<string> options, List<int> selections, int index,
            Color color, int kind, int slot, Action<Color> applyColor, Action applySelection)
        {
            bool expanded = _colorEditorKind == kind && _colorEditorSlot == slot;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(86f));
            if (GUILayout.Button("<", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + options.Count - 1) % options.Count;
                applySelection();
            }
            GUILayout.Label(options[selections[index]], CoopTheme.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + 1) % options.Count;
                applySelection();
            }
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = color;
            if (GUILayout.Button(" ", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                _colorEditorKind = expanded ? -1 : kind;
                _colorEditorSlot = expanded ? -1 : slot;
                if (expanded)
                    _colorDragTarget = 0;
            }
            GUI.backgroundColor = previous;
            GUILayout.EndHorizontal();
            if (!expanded)
                return;

            Color.RGBToHSV(color, out float hue, out float saturation, out float value);
            DrawColorPicker(ref hue, ref saturation, ref value, applyColor);
        }

        private void DrawColorPicker(ref float hue, ref float saturation, ref float value, Action<Color> applyColor)
        {
            EnsureColorPickerTextures();
            if (!Mathf.Approximately(_pickerTextureHue, hue))
                RebuildSaturationValueTexture(hue);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Color", CoopTheme.LabelDim, GUILayout.Width(86f));
            Rect svRect = GUILayoutUtility.GetRect(150f, 92f, GUILayout.Width(150f), GUILayout.Height(92f));
            Rect hueRect = GUILayoutUtility.GetRect(18f, 92f, GUILayout.Width(18f), GUILayout.Height(92f));
            GUI.DrawTexture(svRect, _svPickerTexture, ScaleMode.StretchToFill, false);
            GUI.DrawTexture(hueRect, _huePickerTexture, ScaleMode.StretchToFill, false);
            DrawPickerMarker(new Rect(svRect.x + saturation * svRect.width - 4f,
                svRect.y + (1f - value) * svRect.height - 4f, 8f, 8f), Color.white);
            DrawPickerMarker(new Rect(hueRect.x - 2f, hueRect.y + (1f - hue) * hueRect.height - 2f,
                hueRect.width + 4f, 4f), Color.black);
            GUILayout.EndHorizontal();

            Event e = Event.current;
            Vector2 mousePosition = GUIUtility.GUIToScreenPoint(e.mousePosition);
            Vector2 svScreenPosition = GUIUtility.GUIToScreenPoint(svRect.position);
            Vector2 hueScreenPosition = GUIUtility.GUIToScreenPoint(hueRect.position);
            Rect svScreenRect = new Rect(svScreenPosition.x, svScreenPosition.y, svRect.width, svRect.height);
            Rect hueScreenRect = new Rect(hueScreenPosition.x, hueScreenPosition.y, hueRect.width, hueRect.height);
            bool changed = false;
            if (e.type == EventType.MouseDown)
            {
                if (svScreenRect.Contains(mousePosition))
                {
                    _colorDragTarget = 1;
                    saturation = Mathf.Clamp01((mousePosition.x - svScreenRect.x) / svScreenRect.width);
                    value = Mathf.Clamp01(1f - (mousePosition.y - svScreenRect.y) / svScreenRect.height);
                    changed = true;
                    e.Use();
                }
                else if (hueScreenRect.Contains(mousePosition))
                {
                    _colorDragTarget = 2;
                    hue = Mathf.Clamp01(1f - (mousePosition.y - hueScreenRect.y) / hueScreenRect.height);
                    changed = true;
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && _colorDragTarget != 0)
            {
                if (_colorDragTarget == 1)
                {
                    saturation = Mathf.Clamp01((mousePosition.x - svScreenRect.x) / svScreenRect.width);
                    value = Mathf.Clamp01(1f - (mousePosition.y - svScreenRect.y) / svScreenRect.height);
                }
                else
                    hue = Mathf.Clamp01(1f - (mousePosition.y - hueScreenRect.y) / hueScreenRect.height);
                changed = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
                _colorDragTarget = 0;
            if (changed)
                applyColor(Color.HSVToRGB(hue, saturation, value));
        }

        private void EnsureColorPickerTextures()
        {
            if (_svPickerTexture == null)
            {
                _svPickerTexture = new Texture2D(96, 64, TextureFormat.RGBA32, false);
                _svPickerTexture.wrapMode = TextureWrapMode.Clamp;
            }
            if (_huePickerTexture == null)
            {
                _huePickerTexture = new Texture2D(1, 96, TextureFormat.RGBA32, false);
                _huePickerTexture.wrapMode = TextureWrapMode.Clamp;
                for (int y = 0; y < 96; y++)
                    _huePickerTexture.SetPixel(0, y, Color.HSVToRGB(1f - y / 95f, 1f, 1f));
                _huePickerTexture.Apply();
            }
        }

        private void RebuildSaturationValueTexture(float hue)
        {
            _pickerTextureHue = hue;
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 96; x++)
                    _svPickerTexture.SetPixel(x, y, Color.HSVToRGB(hue, x / 95f, y / 63f));
            _svPickerTexture.Apply();
        }

        private static void DrawPickerMarker(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static Color ReadHairColor(CC.CC_CharacterData data, int slot)
        {
            if (data != null && data.HairColor != null && slot >= 0 && slot < data.HairColor.Count)
            {
                Color color;
                if (ColorUtility.TryParseHtmlString("#" + data.HairColor[slot].stringValue, out color))
                    return color;
            }
            return Color.white;
        }

        private static Color ReadApparelTint(CC.CC_CharacterData data, int slot)
        {
            if (data != null && data.ColorProperties != null)
            {
                string key = "CardShopCoop.ApparelTint." + slot;
                foreach (var property in data.ColorProperties)
                {
                    if (property == null || property.propertyName != key)
                        continue;
                    Color color;
                    if (ColorUtility.TryParseHtmlString("#" + property.stringValue, out color))
                        return color;
                }
            }
            return Color.white;
        }

        private void DrawNumberRow(string label, int count, List<int> values, int index, Action apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(86f));
            if (GUILayout.Button("<", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                values[index] = (values[index] + count - 1) % count;
                apply();
            }
            GUILayout.Label((values[index] + 1).ToString(), CoopTheme.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                values[index] = (values[index] + 1) % count;
                apply();
            }
            GUILayout.EndHorizontal();
        }

        private void ApplyHair(CoopCore core, int slot)
        {
            if (_characterCustomizer == null)
                return;
            if (_hairSelections[slot] == 0)
                core.ClearLocalHair(slot);
            else
            {
                _characterCustomizer.setHairByName(_hairOptions[slot][_hairSelections[slot]], slot);
                core.CommitLocalCustomization();
            }
        }

        private void ApplyApparel(CoopCore core, int slot)
        {
            if (_characterCustomizer == null)
                return;
            string name = _apparelNames[slot][_apparelSelections[slot]];
            if (string.IsNullOrEmpty(name))
                core.ClearLocalApparel(slot);
            else
            {
                int material = Mathf.Max(0, _apparelMaterials[slot]);
                _characterCustomizer.setApparelByName(name, slot, material);
                core.CommitLocalCustomization();
            }
        }

        /// <summary>Status chip (colored by state) + the raw StatusLine kept verbatim beside it
        /// (people paste that line into bug reports).</summary>
        private void DrawStatusRow(CoopCore core, ICoopTransport net)
        {
            ClassifyStatus(core, net, out GUIStyle chip, out string chipText);
            GUILayout.BeginHorizontal();
            if (chip != null)
            {
                CoopTheme.Chip(chipText, chip);
                GUILayout.Space(6f);
                GUILayout.Label(core.StatusLine, CoopTheme.LabelDim);
            }
            else
            {
                GUILayout.Label(core.StatusLine, CoopTheme.Label);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>success when hosting-with-players or connected, info when waiting/connecting,
        /// plain (null chip) otherwise.</summary>
        private static void ClassifyStatus(CoopCore core, ICoopTransport net, out GUIStyle chip, out string text)
        {
            chip = null;
            text = null;
            string s = core.StatusLine ?? "";
            switch (CoopCore.Role)
            {
                case CoopRole.Host:
                    if ((net?.ConnectionCount ?? 0) > 0)
                    {
                        chip = CoopTheme.ChipSuccess;
                        text = "HOSTING";
                    }
                    else
                    {
                        chip = CoopTheme.ChipInfo;
                        text = "WAITING";
                    }
                    break;
                case CoopRole.Client:
                    if (Has(s, "download") || Has(s, "loading") || Has(s, "requesting")
                        || Has(s, "received") || Has(s, "Joining") || Has(s, "Connecting"))
                    {
                        chip = CoopTheme.ChipInfo;
                        text = "CONNECTING";
                    }
                    else
                    {
                        chip = CoopTheme.ChipSuccess;
                        text = "CONNECTED";
                    }
                    break;
                default: // None
                    if (Has(s, "Joining") || Has(s, "Creating") || Has(s, "Connecting"))
                    {
                        chip = CoopTheme.ChipInfo;
                        text = "CONNECTING";
                    }
                    break;
            }
        }

        private static bool Has(string hay, string needle)
            => hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void DrawNone(CoopCore core)
        {
            if (_browserOpen)
            {
                DrawBrowser(core);
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name:", CoopTheme.Label, GUILayout.Width(72f));
            if (core.UsingSteamPersona)
            {
                GUILayout.Label(core.EffectivePlayerName, CoopTheme.TextField, GUILayout.Width(160f));
                GUILayout.Label("(from Steam)", CoopTheme.LabelDim);
            }
            else
            {
                GUI.SetNextControlName("coop_name");
                string newName = GUILayout.TextField(_nameField, 16, CoopTheme.TextField);
                if (newName != _nameField)
                {
                    _nameField = newName;
                    if (newName.Trim().Length > 0)
                        CoopPlugin.PlayerName.Value = newName.Trim();
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            // HOST section
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("HOST YOUR SHOP", CoopTheme.SectionHeader);
            _publicLobby = GUILayout.Toggle(_publicLobby, " public lobby (shows in the browser)", CoopTheme.Toggle);
            if (_publicLobby)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Lobby name:", CoopTheme.Label, GUILayout.Width(80f));
                GUI.SetNextControlName("coop_lobbyname");
                _lobbyNameField = GUILayout.TextField(_lobbyNameField, 28, CoopTheme.TextField);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Password:", CoopTheme.Label, GUILayout.Width(80f));
                GUI.SetNextControlName("coop_hostpw");
                _hostPwField = GUILayout.TextField(_hostPwField, 20, CoopTheme.TextField);
                GUILayout.Label("<size=10>(blank = open)</size>", CoopTheme.LabelDim, GUILayout.Width(80f));
                GUILayout.EndHorizontal();
            }
            // core.Steam == null means this build has NO Steamworks assembly (Game Pass /
            // DRM-free), so Steam can never work and there is nothing the player could do
            // about it. Hide the control entirely rather than leaving a dead button that
            // answers with an error - and promote LAN to the full-width primary so the row
            // doesn't look like something failed to draw.
            GUILayout.BeginHorizontal();
            if (core.Steam != null)
            {
                if (GUILayout.Button("Host via Steam", CoopTheme.ButtonPrimary))
                    core.StartHostingSteam(_publicLobby, _lobbyNameField, _publicLobby ? _hostPwField : "");
                if (GUILayout.Button("Host via LAN", CoopTheme.ButtonSecondary, GUILayout.Width(110f)))
                    core.StartHosting();
            }
            else
            {
                if (GUILayout.Button("Host via LAN", CoopTheme.ButtonPrimary))
                    core.StartHosting();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            CoopTheme.Divider();

            // JOIN section - the last panel on the idle session view, so it expands to fill the
            // tab container (DrawSession's MinHeight wrapper supplies the leftover space).
            GUILayout.BeginVertical(CoopTheme.SectionBox, GUILayout.ExpandHeight(true));
            GUILayout.Label("JOIN A FRIEND", CoopTheme.SectionHeader);
            // Lobby browser + invite hint are Steam-only. On a Steamworks-less build point
            // the player at the thing that DOES work here (the IP field two rows down)
            // instead of showing an error about something they cannot install.
            if (core.Steam != null)
            {
                if (GUILayout.Button("Browse public lobbies", CoopTheme.ButtonPrimary))
                {
                    _browserOpen = true;
                    _page = 0;
                    _pwPromptLobby = 0;
                    core.Steam.RefreshList();
                }
                GUILayout.Label("<size=11>Accept the host's Steam invite.</size>", CoopTheme.LabelDim);
            }
            else
            {
                GUILayout.Label("<size=11>This build has no Steam support - join by IP below.</size>", CoopTheme.LabelDim);
            }

            // wrong-password retry for invites into protected lobbies
            if (core.ErrorLine == "wrong password" && core.LastFailedLobby != 0UL)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Password:", CoopTheme.Label, GUILayout.Width(80f));
                GUI.SetNextControlName("coop_joinpw");
                _joinPwField = GUILayout.TextField(_joinPwField, 20, CoopTheme.TextField);
                if (GUILayout.Button("Retry", CoopTheme.ButtonPrimary, GUILayout.Width(64f)))
                    core.JoinSteam(core.LastFailedLobby, _joinPwField);
                GUILayout.EndHorizontal();
            }

            // IP + optional session password. The password field is here because a LAN host
            // now generates one by default (CoopPlugin.AutoLanPassword): a friend who PASTES
            // the invite code never sees it, but a friend who TYPES the address has to have
            // somewhere to put the eight characters the host read out. Blank is still correct
            // for a host that turned the password off, so nothing about the old flow breaks.
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("coop_ip");
            _ipField = GUILayout.TextField(_ipField, 24, CoopTheme.TextField);
            GUILayout.Label("<size=11>pw</size>", CoopTheme.LabelDim, GUILayout.Width(20f));
            GUI.SetNextControlName("coop_lanpw");
            _lanPwField = GUILayout.TextField(_lanPwField, 20, CoopTheme.TextField, GUILayout.Width(84f));
            if (GUILayout.Button("Join LAN", CoopTheme.ButtonPrimary, GUILayout.Width(84f)))
                // Trimmed and folded to upper case HERE, not in Join and not in the host's
                // password gate. A LAN host's password can only ever be the generated one
                // (there is no field anywhere that sets a custom one), and that is eight
                // characters of an uppercase-only alphabet - so folding what was TYPED can
                // never turn a wrong password into a right one, while it does spare a player
                // who typed what they heard in lower case an inexplicable "wrong password".
                // The gate itself stays exactly comparing, because a STEAM lobby password is
                // player-chosen and its case is the player's business.
                core.Join(_ipField, CoopPlugin.Port.Value, _lanPwField.Trim().ToUpperInvariant());
            GUILayout.EndHorizontal();

            // The invite code is just the IP row with the address, the port and the password
            // folded into one pasteable string - so it sits directly under it rather than in a
            // section of its own.
            //
            // DELIBERATELY VISIBLE ON STEAM BUILDS TOO, unlike the lobby browser above. A code
            // is a plain address + port + password: nothing in it is Steam-shaped, and the host
            // who produced it may well be on Game Pass, where Steam invites do not exist. A
            // Steam player joining a Game Pass host over IP is exactly the case this row is
            // for, so it is not gated on core.Steam.
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("coop_invite");
            _inviteField = GUILayout.TextField(_inviteField, InviteCode.MaxCodeLength, CoopTheme.TextField);
            if (GUILayout.Button("Join by code", CoopTheme.ButtonPrimary, GUILayout.Width(100f)))
            {
                if (InviteCode.TryParse(_inviteField, out string codeIp, out int codePort, out string codePw))
                {
                    _ipField = codeIp; // keep the plain IP row in step with what we just used
                    core.Join(codeIp, codePort, codePw);
                }
                else
                {
                    core.ErrorLine = "that invite code doesn't look right - check for typos";
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=11>Paste an invite code, or enter an IP and password.</size>", CoopTheme.LabelDim);
            GUILayout.Label($"<size=11>LAN port {CoopPlugin.Port.Value} - all players need this mod + the same mods.</size>", CoopTheme.LabelDim);
            GUILayout.EndVertical();
        }

        private void DrawHost(CoopCore core, ICoopTransport net)
        {
            // The host status is the last (and only) panel on this session view, so it expands
            // to fill the tab container via DrawSession's MinHeight wrapper.
            GUILayout.BeginVertical(CoopTheme.SectionBox, GUILayout.ExpandHeight(true));
            GUILayout.Label("HOSTING", CoopTheme.SectionHeader);
            if (core.IsSteamSession)
            {
                GUILayout.Label("Hosting through Steam - no IPs needed.", CoopTheme.Label);
                if (GUILayout.Button("Invite friend  (Steam overlay)", CoopTheme.ButtonPrimary))
                    core.OpenSteamInvite();
                int scount = net?.ConnectionCount ?? 0;
                GUILayout.Label(scount == 0 ? "Waiting for your invite to be accepted..." : PlayersLine(core), CoopTheme.Label);
            }
            else
            {
                if (_lanIps == null)
                {
                    var ips = LocalIPv4s();
                    // home-router addresses first; virtual/VPN adapters are unreachable
                    ips.Sort((a, b) => IpRank(a).CompareTo(IpRank(b)));
                    _lanIps = ips.Count > 0 ? ips[0] : "(no LAN address found)";
                    _lanIpsOther = ips.Count > 1 ? string.Join("  ", ips.GetRange(1, ips.Count - 1)) : "";
                }
                // The address is the one thing that can leak a home connection on stream, so it
                // is never printed: the copy button puts the whole connection block on the
                // clipboard instead (the clipboard is not on camera). The session password below
                // is still shown because it alone opens nothing and a friend typing the address
                // by hand needs to read it.
                _ipCopy.Draw("Copy IP address", IpClipboardText(), CoopTheme.ButtonSecondary);
                DrawInvite(core);
                int count = net?.ConnectionCount ?? 0;
                GUILayout.Label(count == 0 ? "Waiting for a player..." : PlayersLine(core), CoopTheme.Label);
            }
            if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")", CoopTheme.ButtonSecondary))
                core.SendEmote();
            if (GUILayout.Button("Stop hosting", CoopTheme.ButtonDanger))
                core.Disconnect();
            GUILayout.EndVertical();
        }

        /// <summary>The invite-code block in the LAN host panel: the status + copy row, the
        /// session-password row, and the router notes. Only ever drawn for a LAN session -
        /// DrawHost returns before this for Steam, where the overlay invite already does the
        /// job and an address-bearing code would just be a second way to do the same thing.
        ///
        /// The code EMBEDS the address, so the raw text is never printed: copying is always
        /// allowed (the clipboard isn't on camera) and the address itself is copy-only in
        /// DrawHost rather than shown on screen.</summary>
        private void DrawInvite(CoopCore core)
        {
            // LATCH FIRST, BEFORE THE EARLY RETURN. Everything below - including whether this
            // block draws at all - reads the latched copies, so the snapshot has to be taken
            // ahead of the first decision that depends on it. Latch after the return and a
            // panel that starts in Off never updates its own copy and stays blank forever.
            if (Event.current.type == EventType.Layout)
            {
                _invState = core.InviteStatus;
                _invCode = core.InviteCodeText;
                _invReason = core.InviteReason;
                _invPortState = core.PortForwardState;
                _invPassword = core.HostPassword;
            }

            if (_invState == InviteState.Off)
                return;

            string code = _invCode;

            float copyButtonWidth = CopyButtonWidth();
            // Both copy rows share this height (the button's box plus its margins) so the text
            // can be centred against the button instead of sitting at the top of the row.
            float copyRowHeight = CoopTheme.ButtonSecondary.fixedHeight + CoopTheme.ButtonSecondary.margin.vertical;
            GUILayout.BeginHorizontal();
            GUILayout.Label(InviteStatusText(_invState), CoopTheme.LabelDimMiddle, GUILayout.Height(copyRowHeight));
            GUILayout.FlexibleSpace();
            GUI.enabled = code != null;
            _inviteCopy.Draw("Copy invite code", code, CoopTheme.ButtonSecondary,
                GUILayout.Width(copyButtonWidth));
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // THE SESSION PASSWORD is still printed while the connection details are hidden,
            // and that is the shape of the stream-safety rule rather than an oversight: it
            // protects the ADDRESS, because an address is what lets a stranger reach this PC at
            // all, while the password on its own opens nothing and a friend typing the IP by
            // hand needs to read it. Anyone who used the invite code already supplied it.
            if (!string.IsNullOrEmpty(_invPassword))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<size=11>session password: <b>{_invPassword}</b></size>", CoopTheme.LabelDimMiddle,
                    GUILayout.ExpandWidth(true), GUILayout.Height(copyRowHeight));
                _passwordCopy.Draw("Copy password", _invPassword, CoopTheme.ButtonSecondary,
                    GUILayout.Width(copyButtonWidth));
                GUILayout.EndHorizontal();
            }

            // Say plainly what a LAN-only code is and isn't. It is NOT a failure - it is the
            // code that has always worked for the PC in the next room - so it is worded as a
            // limit rather than an error, and it names the reason so the player can fix it.
            if (_invState == InviteState.LanOnly)
            {
                string why = _invReason;
                GUILayout.Label("<size=11>LAN-only code - " + (why != null ? why : "couldn't reach the internet resolver")
                    + ". It carries your LAN address, so it still works for someone in this house.</size>",
                    CoopTheme.LabelWrap);
            }

            // UPnP success means the router ACCEPTED the request, which is not the same as the
            // port being reachable (a second router, or the ISP's own NAT, still gets a vote).
            // Never promise more than that.
            if (_invPortState == 1)
                GUILayout.Label("<size=10>port forward requested on your router automatically</size>", CoopTheme.LabelDim);
            else if (_invPortState == 2)
                // Say what to DO about it, not just that it happened: forwarding the port is a
                // router-admin job this mod cannot do, and the other player hosting instead is
                // the answer for anyone who isn't going to go find their router password.
                // Kept short AND wrapped: the long form measured wider than this 400px panel,
                // and LabelDim doesn't wrap, so the tail was clipped off-screen - the half that
                // carried the advice. LabelDimWrap is LabelDim + wordWrap.
                GUILayout.Label($"<size=10>router declined - forward port {CoopPlugin.Port.Value} manually, or let the other player host</size>", CoopTheme.LabelDimWrap);
        }

        /// <summary>The plain-text connection block the Copy IP button puts on the clipboard:
        /// the primary address + port, then any other local addresses. This is the text the
        /// panel used to render, kept copyable so the address is never shown on screen.</summary>
        private string IpClipboardText()
        {
            string text = $"{_lanIps}  (port {CoopPlugin.Port.Value})";
            if (!string.IsNullOrEmpty(_lanIpsOther))
                text += $"\nOther addresses: {_lanIpsOther}";
            return text;
        }

        private static float CopyButtonWidth()
        {
            return Mathf.Max(
                CoopTheme.ButtonSecondary.CalcSize(new GUIContent("Copy invite code")).x,
                CoopTheme.ButtonSecondary.CalcSize(new GUIContent("Copy password")).x,
                CoopTheme.ButtonSecondary.CalcSize(new GUIContent("Copied!")).x);
        }

        private static string InviteStatusText(InviteState state)
        {
            switch (state)
            {
                case InviteState.Resolving:
                    return "Preparing code...";
                case InviteState.Ready:
                    return "Code ready";
                case InviteState.LanOnly:
                    return "Code ready";
                default:
                    return "";
            }
        }

        private void DrawClient(CoopCore core)
        {
            // The client status is the last (and only) panel on this session view, so it expands
            // to fill the tab container via DrawSession's MinHeight wrapper.
            GUILayout.BeginVertical(CoopTheme.SectionBox, GUILayout.ExpandHeight(true));
            GUILayout.Label("IN SESSION", CoopTheme.SectionHeader);
            GUILayout.Label(PlayersLine(core), CoopTheme.Label);
            if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")", CoopTheme.ButtonSecondary))
                core.SendEmote();
            if (GUILayout.Button("Leave session", CoopTheme.ButtonDanger))
                core.Disconnect();
            GUILayout.EndVertical();
        }

        /// <summary>The graded-album repair offer, drawn only while a one-sided difference is
        /// actually known - i.e. after a digest exchange found graded cards the peer has and this
        /// PC does not. One button per peer, and pressing it is the ONLY thing in the whole
        /// graded-divergence feature that changes a card: it adds, one way, and never removes.
        ///
        /// LATCHED IN THE LAYOUT PASS for the reason spelled out at the top of this file - the
        /// offer list is rebuilt from the network pump, and a row count that differs between
        /// Layout and Repaint takes the entire window down with "Mismatched LayoutGroup".</summary>
        private readonly List<CoopCore.GradedAdoptOffer> _adoptOffers = new List<CoopCore.GradedAdoptOffer>();

        private void DrawGradedAdopt(CoopCore core)
        {
            if (Event.current.type == EventType.Layout)
            {
                _adoptOffers.Clear();
                _adoptOffers.AddRange(core.GradedAdoptOffers);
            }
            if (_adoptOffers.Count == 0)
                return;

            CoopTheme.Divider();
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.BeginHorizontal();
            CoopTheme.Chip("GRADED ALBUM", CoopTheme.ChipWarn);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // Must state the guard EXACTLY as GradedAdopt enforces it: refusal is on certificate
            // PRESENCE, not on "a different card". Promising the narrower rule invited the press
            // that duplicated a card this PC already held under Grading Overhaul's FAKE flag.
            GUILayout.Label("<size=11>Only missing graded cards will be added; existing certificates are skipped.</size>",
                CoopTheme.LabelWrap);
            for (int i = 0; i < _adoptOffers.Count; i++)
            {
                var o = _adoptOffers[i];
                if (GUILayout.Button($"Adopt {o.Count} graded card(s) {o.Who} has that you don't", CoopTheme.ButtonSecondary))
                    core.GradedAdopt(o.ConnId);
            }
            GUILayout.EndVertical();
        }

        private void DrawBrowser(CoopCore core)
        {
            // Belt and braces: the only door into the browser is already hidden when Steam
            // is absent, but every line below dereferences core.Steam, so refuse to draw at
            // all rather than trust that. Closes the panel silently - there is no error to
            // report, this build simply has no lobbies.
            if (core.Steam == null)
            {
                _browserOpen = false;
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("PUBLIC LOBBIES", CoopTheme.SectionHeader);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(core.Steam.ListRefreshing ? "..." : "Refresh", CoopTheme.ButtonPrimary, GUILayout.Width(72f)))
                core.Steam.RefreshList();
            if (GUILayout.Button("Back", CoopTheme.ButtonSecondary, GUILayout.Width(54f)))
            {
                _browserOpen = false;
                _pwPromptLobby = 0;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", CoopTheme.Label, GUILayout.Width(52f));
            GUI.SetNextControlName("coop_search");
            string s = GUILayout.TextField(_searchField, 24, CoopTheme.TextField);
            if (s != _searchField)
            {
                _searchField = s;
                _page = 0;
            }
            GUILayout.EndHorizontal();

            // Net.LobbyRow, NOT SteamLobby.LobbyRow: the latter carries a CSteamID field, so
            // even as a local generic argument here it would take this whole method down on
            // a build with no Steamworks assembly.
            var filtered = new List<LobbyRow>();
            foreach (var row in core.Steam.Lobbies)
                if (_searchField.Length == 0
                    || (row.Name ?? "").IndexOf(_searchField, StringComparison.OrdinalIgnoreCase) >= 0)
                    filtered.Add(row);

            int pages = Mathf.Max(1, (filtered.Count + PageSize - 1) / PageSize);
            _page = Mathf.Clamp(_page, 0, pages - 1);

            if (filtered.Count == 0)
            {
                GUILayout.Label(core.Steam.ListRefreshing ? "Searching..." : "No lobbies found - hit Refresh, or host one!", CoopTheme.LabelDim);
            }
            for (int i = _page * PageSize; i < filtered.Count && i < (_page + 1) * PageSize; i++)
            {
                var row = filtered[i];
                bool verOk = row.Ver == CoopPlugin.Version;
                GUILayout.BeginHorizontal(((i & 1) == 0) ? CoopTheme.RowEven : CoopTheme.RowOdd);
                GUILayout.Label((row.HasPw ? "[pw] " : "") + row.Name, CoopTheme.LabelBold, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                string right = $"{row.Players}/{row.Max}" + (verOk ? "" : $"  <size=10>v{row.Ver}</size>");
                GUILayout.Label(right, CoopTheme.LabelDim, GUILayout.ExpandWidth(false));
                GUILayout.Space(6f);
                GUI.enabled = verOk;
                if (GUILayout.Button("Join", CoopTheme.ButtonPrimary, GUILayout.Width(56f)))
                {
                    if (row.HasPw)
                    {
                        _pwPromptLobby = row.Id;
                        _joinPwField = "";
                    }
                    else
                    {
                        core.JoinSteam(row.Id, "");
                        _browserOpen = false;
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                if (_pwPromptLobby == row.Id)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Password:", CoopTheme.Label, GUILayout.Width(70f));
                    GUI.SetNextControlName("coop_joinpw");
                    _joinPwField = GUILayout.TextField(_joinPwField, 20, CoopTheme.TextField);
                    if (GUILayout.Button("Go", CoopTheme.ButtonPrimary, GUILayout.Width(44f)))
                    {
                        core.JoinSteam(row.Id, _joinPwField);
                        _browserOpen = false;
                        _pwPromptLobby = 0;
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = _page > 0;
            if (GUILayout.Button("< Prev", CoopTheme.ButtonSecondary, GUILayout.Width(64f)))
                _page--;
            GUI.enabled = _page < pages - 1;
            if (GUILayout.Button("Next >", CoopTheme.ButtonSecondary, GUILayout.Width(64f)))
                _page++;
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<size=11>page {_page + 1}/{pages} - {filtered.Count} lobbies</size>", CoopTheme.LabelDim);
            GUILayout.EndHorizontal();
        }

        private static string PlayersLine(CoopCore core)
        {
            if (core.PeerNames.Count == 0)
                return "Linked.";
            var names = new List<string>(core.PeerNames.Values);
            return "Playing with: " + string.Join(", ", names);
        }

        private static List<string> LocalIPv4s()
        {
            var result = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        string s = addr.Address.ToString();
                        if (s.StartsWith("169.254"))
                            continue; // link-local noise
                        result.Add(s);
                    }
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
            if (result.Count == 0)
                result.Add("(no LAN address found)");
            return result;
        }
    }
}
