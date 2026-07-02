using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>Small IMGUI window: host / join / status. Toggled with F11.</summary>
    public class CoopUI
    {
        public bool Visible = true;
        public static bool TextFieldFocused;

        private Rect _win = new Rect(24f, 96f, 340f, 10f);
        private string _ipField;
        private string _nameField;
        private string _lanIps;

        public void Draw(CoopCore core, Transport net)
        {
            if (_ipField == null) _ipField = CoopPlugin.LastJoinIP.Value;
            if (_nameField == null) _nameField = CoopPlugin.PlayerName.Value;

            // little always-on hint + client link status
            if (!Visible)
            {
                GUI.Label(new Rect(8f, Screen.height - 22f, 400f, 20f),
                    $"<size=11><color=#9fd3ff>CardShopCoop: {CoopPlugin.UiToggleKey.Value} for co-op</color></size>");
                if (core.ErrorLine.Length > 0)
                {
                    var warn = new GUIStyle(GUI.skin.label) { richText = true, fontStyle = FontStyle.Bold };
                    GUI.Label(new Rect(8f, Screen.height - 46f, 900f, 22f),
                        $"<size=13><color=#ff5a4a>CO-OP: {core.ErrorLine}</color></size>", warn);
                }
            }
            if (CoopCore.Role == CoopRole.Client && core.HostTimeLine.Length > 0)
            {
                var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true };
                GUI.Label(new Rect(Screen.width / 2f - 150f, 4f, 300f, 20f),
                    $"<color=#ffd54a>{core.HostTimeLine} - co-op</color>", style);
            }
            if (core.RegisterLine.Length > 0 || core.PromptLine.Length > 0)
            {
                var big = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter, richText = true, fontStyle = FontStyle.Bold
                };
                if (core.PromptLine.Length > 0)
                    GUI.Label(new Rect(Screen.width / 2f - 300f, Screen.height * 0.58f, 600f, 30f),
                        $"<size=17><color=#7ecbff>{core.PromptLine}</color></size>", big);
                if (core.RegisterLine.Length > 0)
                    GUI.Label(new Rect(Screen.width / 2f - 300f, Screen.height * 0.63f, 600f, 30f),
                        $"<size=18><color=#8ef58a>{core.RegisterLine}</color></size>", big);
            }
            if (!Visible) return;

            _win = GUILayout.Window(867530, _win, id => WindowFn(core, net), "CardShopCoop " + CoopPlugin.Version);
        }

        private void WindowFn(CoopCore core, Transport net)
        {
            GUILayout.Label(core.StatusLine);
            if (core.ErrorLine.Length > 0)
            {
                var red = new GUIStyle(GUI.skin.label) { wordWrap = true };
                red.normal.textColor = new Color(1f, 0.45f, 0.4f);
                GUILayout.Label(core.ErrorLine, red);
            }

            switch (CoopCore.Role)
            {
                case CoopRole.None:
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Your name:", GUILayout.Width(72f));
                    GUI.SetNextControlName("coop_name");
                    string newName = GUILayout.TextField(_nameField, 16);
                    if (newName != _nameField)
                    {
                        _nameField = newName;
                        if (newName.Trim().Length > 0) CoopPlugin.PlayerName.Value = newName.Trim();
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Space(6f);
                    GUILayout.Label("<b>Host</b> (load your shop first):");
                    if (GUILayout.Button("Host my shop"))
                        core.StartHosting();

                    GUILayout.Space(6f);
                    GUILayout.Label("<b>Join</b> (stay on the main menu):");
                    GUILayout.BeginHorizontal();
                    GUI.SetNextControlName("coop_ip");
                    _ipField = GUILayout.TextField(_ipField, 24);
                    if (GUILayout.Button("Join", GUILayout.Width(64f)))
                        core.Join(_ipField);
                    GUILayout.EndHorizontal();
                    GUILayout.Label($"<size=11>Port {CoopPlugin.Port.Value} - both PCs need this mod + the same mods.</size>");
                    break;
                }
                case CoopRole.Host:
                {
                    if (_lanIps == null) _lanIps = string.Join("  ", LocalIPv4s());
                    GUILayout.Label("Give this to the other PC:");
                    GUILayout.Label($"<b><size=15>{_lanIps}</size></b>  (port {CoopPlugin.Port.Value})");
                    int count = net?.ConnectionCount ?? 0;
                    GUILayout.Label(count == 0 ? "Waiting for a player..." : PlayersLine(core));
                    if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")")) core.SendEmote();
                    if (GUILayout.Button("Stop hosting")) core.Disconnect();
                    break;
                }
                case CoopRole.Client:
                {
                    GUILayout.Label(PlayersLine(core));
                    GUILayout.Label($"<size=11>You're playing in the host's shop. At the register, click the customer's items to scan them, then click to take payment and give change ({CoopPlugin.ServeKey.Value} also works). Your own saves are protected.</size>",
                        new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true });
                    if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")")) core.SendEmote();
                    if (GUILayout.Button("Leave session")) core.Disconnect();
                    break;
                }
            }

            string focused = GUI.GetNameOfFocusedControl();
            TextFieldFocused = focused == "coop_ip" || focused == "coop_name";

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        private static string PlayersLine(CoopCore core)
        {
            if (core.PeerNames.Count == 0) return "Linked.";
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
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = addr.Address.ToString();
                        if (s.StartsWith("169.254")) continue; // link-local noise
                        result.Add(s);
                    }
                }
            }
            catch { }
            if (result.Count == 0) result.Add("(no LAN address found)");
            return result;
        }
    }
}
