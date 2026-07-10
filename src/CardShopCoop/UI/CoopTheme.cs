using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Cozy "card-shop paper + teal" IMGUI theme for the co-op window and its HUD overlays.
    ///
    /// Everything here is built ONCE (lazily, inside OnGUI where GUI.skin and the graphics
    /// device are valid) and cached. Textures are generated at runtime with analytic
    /// anti-aliased rounded corners and 9-sliced via GUIStyle.border so they scale crisp at
    /// any size. They are marked HideFlags.HideAndDontSave so they survive scene loads and are
    /// never touched by Resources.UnloadUnusedAssets - we never rebuild them per frame.
    ///
    /// Public surface used by CoopUI:
    ///   CoopTheme.EnsureBuilt();                       // call early in OnGUI
    ///   CoopTheme.Window / Label / ButtonPrimary / ...  // GUIStyle fields
    ///   CoopTheme.DrawWindowShadow(Rect win);          // soft drop shadow (screen space)
    ///   CoopTheme.DrawWindowChrome(Rect r, title, ver);// header strip + title + version
    ///   CoopTheme.Divider();                           // 1px horizontal rule
    ///   CoopTheme.Chip(text, chipStyle);               // a pill
    ///   CoopTheme.PillSize(style, content, maxW);      // sized rect for a HUD pill
    /// </summary>
    public static class CoopTheme
    {
        // ---- palette (warm card-shop paper + teal) --------------------------------------
        public static readonly Color Panel        = Rgb(247, 241, 227, 0.97f); // #F7F1E3
        public static readonly Color PanelBorder  = Rgb(185, 174, 148);        // #B9AE94
        public static readonly Color HeaderBg     = Rgb( 46,  75,  78);        // #2E4B4E
        public static readonly Color HeaderText   = Rgb(242, 237, 226);        // #F2EDE2
        public static readonly Color Text         = Rgb( 51,  48,  43);        // #33302B
        public static readonly Color TextDim      = Rgb(107, 101,  92);        // #6B655C
        public static readonly Color SectionBg    = Rgb(255, 251, 240, 0.90f); // #FFFBF0
        public static readonly Color SectionBorder= Rgb(221, 211, 188);        // #DDD3BC
        public static readonly Color Primary      = Rgb( 62, 158, 140);        // #3E9E8C
        public static readonly Color PrimaryHover = Rgb( 71, 178, 158);        // #47B29E
        public static readonly Color PrimaryActive= Rgb( 54, 139, 123);        // #368B7B
        public static readonly Color Secondary    = Rgb(239, 231, 212);        // #EFE7D4
        public static readonly Color SecondaryHover= Rgb(246, 239, 222);       // #F6EFDE
        public static readonly Color SecondaryBorder= Rgb(201, 191, 168);      // #C9BFA8
        public static readonly Color Danger       = Rgb(201,  79,  61);        // #C94F3D
        public static readonly Color Warn         = Rgb(201, 134,  45);        // #C9862D
        public static readonly Color Success      = Rgb( 63, 157,  83);        // #3F9D53
        public static readonly Color DividerCol   = Rgb(217, 207, 184);        // #D9CFB8
        public static readonly Color FieldBg      = Rgb(255, 253, 246);        // #FFFDF6
        public static readonly Color FieldBorder  = Rgb(201, 191, 168);        // #C9BFA8
        public static readonly Color HudBg        = Rgb( 31,  29,  26, 0.78f);  // #1F1D1A

        // ---- built styles (all created once in EnsureBuilt) -----------------------------
        public static GUIStyle Window, HeaderStrip, Header, HeaderVersion, SectionHeader;
        public static GUIStyle Label, LabelDim, LabelBold, LabelWrap, LabelDanger, LabelWarn;
        public static GUIStyle SectionBox, Toggle;
        public static GUIStyle ButtonPrimary, ButtonSecondary, ButtonDanger;
        public static GUIStyle TextField;
        public static GUIStyle ChipSuccess, ChipWarn, ChipDanger, ChipInfo;
        public static GUIStyle HudPill, HudPillBig;
        public static GUIStyle RowEven, RowOdd;

        // ---- cached textures ------------------------------------------------------------
        private static Texture2D _panelTex, _headerTex, _shadowTex, _fieldTex, _fieldFocusTex,
            _sectionTex, _dividerTex, _rowEvenTex, _rowOddTex, _hudTex;
        private static Texture2D _primaryTex, _primaryHoverTex, _primaryActiveTex;
        private static Texture2D _secondaryTex, _secondaryHoverTex, _secondaryActiveTex;
        private static Texture2D _dangerTex, _dangerHoverTex, _dangerActiveTex;
        private static Texture2D _chipSuccessTex, _chipWarnTex, _chipDangerTex, _chipInfoTex;

        private static bool _built;

        // corner radii / texture sizes (small; 9-sliced up crisp)
        private const int WinTex = 28, WinRadius = 10;
        private const int CtlTex = 16, CtlRadius = 6;
        private const int SecTex = 20, SecRadius = 8;
        private const int PillTex = 20, PillRadius = 8;
        private const int HudTexSz = 32, HudRadius = 14;

        public static void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            // ---- textures -----------------------------------------------------------
            _panelTex   = MakeRounded(WinTex, WinTex, WinRadius, Panel, PanelBorder, 1);
            _headerTex  = MakeRoundedTop(WinTex, WinTex, WinRadius, HeaderBg);
            _shadowTex  = MakeShadow(40, 40, 12, 0.35f, 4f);
            _fieldTex   = MakeRounded(CtlTex, CtlTex, CtlRadius, FieldBg, FieldBorder, 1);
            _fieldFocusTex = MakeRounded(CtlTex, CtlTex, CtlRadius, FieldBg, Primary, 1);
            _sectionTex = MakeRounded(SecTex, SecTex, SecRadius, SectionBg, SectionBorder, 1);
            _dividerTex = Solid(DividerCol);
            _rowEvenTex = Solid(Rgb(255, 255, 255, 0.28f));
            _rowOddTex  = Solid(Rgb(120, 110,  90, 0.10f));
            _hudTex     = MakeRounded(HudTexSz, HudTexSz, HudRadius, HudBg, HudBg, 0);

            _primaryTex        = MakeRounded(CtlTex, CtlTex, CtlRadius, Primary,       Mul(Primary, 0.85f),       1);
            _primaryHoverTex   = MakeRounded(CtlTex, CtlTex, CtlRadius, PrimaryHover,  Mul(PrimaryHover, 0.85f),  1);
            _primaryActiveTex  = MakeRounded(CtlTex, CtlTex, CtlRadius, PrimaryActive, Mul(PrimaryActive, 0.85f), 1);

            _secondaryTex      = MakeRounded(CtlTex, CtlTex, CtlRadius, Secondary,      SecondaryBorder, 1);
            _secondaryHoverTex = MakeRounded(CtlTex, CtlTex, CtlRadius, SecondaryHover, SecondaryBorder, 1);
            _secondaryActiveTex= MakeRounded(CtlTex, CtlTex, CtlRadius, Mul(Secondary, 0.94f), SecondaryBorder, 1);

            _dangerTex         = MakeRounded(CtlTex, CtlTex, CtlRadius, Danger,             Mul(Danger, 0.85f), 1);
            _dangerHoverTex    = MakeRounded(CtlTex, CtlTex, CtlRadius, Lerp(Danger, Color.white, 0.10f), Mul(Danger, 0.85f), 1);
            _dangerActiveTex   = MakeRounded(CtlTex, CtlTex, CtlRadius, Mul(Danger, 0.88f), Mul(Danger, 0.80f), 1);

            _chipSuccessTex = MakeRounded(PillTex, PillTex, PillRadius, WithA(Success, 0.18f), WithA(Success, 0.55f), 1);
            _chipWarnTex    = MakeRounded(PillTex, PillTex, PillRadius, WithA(Warn,    0.18f), WithA(Warn,    0.55f), 1);
            _chipDangerTex  = MakeRounded(PillTex, PillTex, PillRadius, WithA(Danger,  0.18f), WithA(Danger,  0.55f), 1);
            _chipInfoTex    = MakeRounded(PillTex, PillTex, PillRadius, WithA(HeaderBg, 0.16f), WithA(HeaderBg, 0.45f), 1);

            // ---- styles -------------------------------------------------------------
            Window = new GUIStyle(GUI.skin.window)
            {
                richText = true,
                padding = new RectOffset(14, 14, 40, 12), // top 40 leaves room for the header strip
                border = new RectOffset(WinRadius, WinRadius, WinRadius, WinRadius)
            };
            Window.normal.background = _panelTex;
            Window.onNormal.background = _panelTex;
            Window.focused.background = _panelTex;
            Window.onFocused.background = _panelTex;
            Window.hover.background = _panelTex;
            Window.active.background = _panelTex;
            Window.normal.textColor = Clear();      // title is empty; we draw our own
            Window.onNormal.textColor = Clear();

            HeaderStrip = new GUIStyle
            {
                border = new RectOffset(WinRadius, WinRadius, WinRadius, 3)
            };
            HeaderStrip.normal.background = _headerTex;

            Header = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                fontSize = 15,
                alignment = TextAnchor.MiddleLeft
            };
            Header.normal.textColor = HeaderText;

            HeaderVersion = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 11,
                alignment = TextAnchor.MiddleRight
            };
            HeaderVersion.normal.textColor = WithA(HeaderText, 0.80f);

            SectionHeader = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                margin = new RectOffset(0, 0, 0, 4)
            };
            SectionHeader.normal.textColor = HeaderBg;

            Label = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
            Label.normal.textColor = Text;

            LabelDim = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11 };
            LabelDim.normal.textColor = TextDim;

            LabelBold = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, fontStyle = FontStyle.Bold };
            LabelBold.normal.textColor = Text;

            LabelWrap = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, wordWrap = true };
            LabelWrap.normal.textColor = Text;

            LabelDanger = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, wordWrap = true };
            LabelDanger.normal.textColor = Danger;

            LabelWarn = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, wordWrap = true };
            LabelWarn.normal.textColor = Warn;

            SectionBox = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(SecRadius, SecRadius, SecRadius, SecRadius),
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(0, 0, 4, 4)
            };
            SectionBox.normal.background = _sectionTex;

            Toggle = new GUIStyle(GUI.skin.toggle) { richText = true, fontSize = 12 };
            SetTextColorAllStates(Toggle, Text);

            ButtonPrimary   = MakeButton(_primaryTex,   _primaryHoverTex,   _primaryActiveTex,   Color.white);
            ButtonSecondary = MakeButton(_secondaryTex, _secondaryHoverTex, _secondaryActiveTex, Text);
            ButtonDanger    = MakeButton(_dangerTex,    _dangerHoverTex,    _dangerActiveTex,    Color.white);

            TextField = new GUIStyle(GUI.skin.textField)
            {
                richText = false, // user types literal text; never parse it as markup
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 4, 4),
                border = new RectOffset(CtlRadius, CtlRadius, CtlRadius, CtlRadius)
            };
            TextField.normal.background = _fieldTex;
            TextField.hover.background = _fieldTex;
            TextField.focused.background = _fieldFocusTex;
            SetTextColorAllStates(TextField, Text);

            ChipSuccess = MakeChip(_chipSuccessTex, Success);
            ChipWarn    = MakeChip(_chipWarnTex, Mul(Warn, 0.92f));
            ChipDanger  = MakeChip(_chipDangerTex, Danger);
            ChipInfo    = MakeChip(_chipInfoTex, HeaderBg);

            HudPill = new GUIStyle
            {
                richText = true,
                wordWrap = true,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(HudRadius, HudRadius, HudRadius, HudRadius),
                // vertical padding keeps the pill >= 2*radius tall so the 9-slice corners never squish
                padding = new RectOffset(14, 14, 9, 9)
            };
            HudPill.normal.background = _hudTex;
            HudPill.normal.textColor = Color.white;

            HudPillBig = new GUIStyle(HudPill)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(18, 18, 10, 10)
            };
            HudPillBig.normal.background = _hudTex;
            HudPillBig.normal.textColor = Color.white;

            RowEven = MakeRow(_rowEvenTex);
            RowOdd  = MakeRow(_rowOddTex);
        }

        // --------------------------------------------------------------------------------
        // Public draw helpers
        // --------------------------------------------------------------------------------

        /// <summary>Soft rounded drop shadow, offset (3,4) behind the window. MUST be drawn in
        /// screen space (from Draw, before GUILayout.Window) - a shadow drawn inside the window
        /// callback would be clipped to the window rect and never show the offset sliver.</summary>
        public static void DrawWindowShadow(Rect win)
        {
            if (_shadowTex == null) return;
            var r = new Rect(win.x + 3f, win.y + 4f, win.width, win.height);
            GUI.Box(r, GUIContent.none, HeaderShadow());
        }

        private static GUIStyle _shadowStyle;
        private static GUIStyle HeaderShadow()
        {
            if (_shadowStyle == null)
            {
                _shadowStyle = new GUIStyle { border = new RectOffset(16, 16, 16, 16) };
                _shadowStyle.normal.background = _shadowTex;
            }
            return _shadowStyle;
        }

        private static GUIContent _titleGc, _versionGc;

        /// <summary>Header strip (rounded-top teal bar), left-aligned title, right-aligned
        /// version. Called INSIDE the window callback with r = (0,0,width,height) in local
        /// coordinates.</summary>
        public static void DrawWindowChrome(Rect r, string title, string version)
        {
            const float stripH = 30f;
            GUI.Box(new Rect(0f, 0f, r.width, stripH), GUIContent.none, HeaderStrip);

            if (_titleGc == null || _titleGc.text != title) _titleGc = new GUIContent(title);
            GUI.Label(new Rect(13f, 5f, r.width - 26f, 20f), _titleGc, Header);

            if (_versionGc == null || _versionGc.text != version) _versionGc = new GUIContent(version);
            GUI.Label(new Rect(13f, 6f, r.width - 26f, 18f), _versionGc, HeaderVersion);
        }

        /// <summary>1px horizontal rule across the current layout width.</summary>
        public static void Divider()
        {
            GUILayout.Space(3f);
            var r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true), GUILayout.Height(1f));
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(r, _dividerTex);
            GUILayout.Space(3f);
        }

        /// <summary>A pill: a small tinted, rounded label that hugs its content.</summary>
        public static void Chip(string text, GUIStyle chipStyle)
        {
            GUILayout.Label(text, chipStyle, GUILayout.ExpandWidth(false));
        }

        /// <summary>Content-sized dimensions for a HUD pill, wrapping to maxW if the text is
        /// wider than that.</summary>
        public static Vector2 PillSize(GUIStyle style, GUIContent content, float maxW)
        {
            Vector2 s = style.CalcSize(content);
            if (s.x > maxW)
            {
                s.x = maxW;
                s.y = style.CalcHeight(content, maxW);
            }
            return s;
        }

        // --------------------------------------------------------------------------------
        // Style factories
        // --------------------------------------------------------------------------------

        private static GUIStyle MakeButton(Texture2D bg, Texture2D hover, Texture2D active, Color textColor)
        {
            var s = new GUIStyle(GUI.skin.button)
            {
                richText = true,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 26f,
                padding = new RectOffset(10, 10, 4, 4),
                margin = new RectOffset(2, 2, 3, 3),
                border = new RectOffset(CtlRadius, CtlRadius, CtlRadius, CtlRadius)
            };
            s.normal.background = bg;      s.normal.textColor = textColor;
            s.hover.background = hover;    s.hover.textColor = textColor;
            s.active.background = active;  s.active.textColor = textColor;
            s.focused.background = bg;     s.focused.textColor = textColor;
            s.onNormal.background = bg;    s.onNormal.textColor = textColor;
            s.onHover.background = hover;  s.onHover.textColor = textColor;
            s.onActive.background = active; s.onActive.textColor = textColor;
            s.onFocused.background = bg;   s.onFocused.textColor = textColor;
            return s;
        }

        private static GUIStyle MakeChip(Texture2D bg, Color textColor)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 3, 3),
                margin = new RectOffset(0, 0, 2, 2),
                border = new RectOffset(PillRadius, PillRadius, PillRadius, PillRadius)
            };
            s.normal.background = bg;
            s.normal.textColor = textColor;
            return s;
        }

        private static GUIStyle MakeRow(Texture2D bg)
        {
            var s = new GUIStyle
            {
                padding = new RectOffset(8, 6, 4, 4),
                margin = new RectOffset(0, 0, 1, 1),
                border = new RectOffset(2, 2, 2, 2)
            };
            s.normal.background = bg;
            return s;
        }

        private static void SetTextColorAllStates(GUIStyle s, Color c)
        {
            s.normal.textColor = c;   s.hover.textColor = c;
            s.active.textColor = c;   s.focused.textColor = c;
            s.onNormal.textColor = c; s.onHover.textColor = c;
            s.onActive.textColor = c; s.onFocused.textColor = c;
        }

        // --------------------------------------------------------------------------------
        // Texture generation (analytic anti-aliased rounded rects)
        // --------------------------------------------------------------------------------

        /// <summary>Rounded rect with all four corners rounded. Per-pixel alpha comes from the
        /// signed distance to the rounded-rect edge (1px smoothstep), so the corners are
        /// anti-aliased. Built small and 9-sliced by the caller via GUIStyle.border.</summary>
        public static Texture2D MakeRounded(int w, int h, int radius, Color fill, Color border, int borderWidth)
        {
            return MakeCore(w, h, radius, radius, radius, radius, fill, border, borderWidth);
        }

        /// <summary>Rounded TOP corners only (square bottom) - the header strip sits flush on
        /// the window's straight header line while echoing the rounded panel top.</summary>
        private static Texture2D MakeRoundedTop(int w, int h, int radius, Color fill)
        {
            return MakeCore(w, h, radius, radius, 0, 0, fill, fill, 0);
        }

        private static Texture2D MakeCore(int w, int h, float rTL, float rTR, float rBR, float rBL,
            Color fill, Color border, float bw)
        {
            var px = new Color[w * h];
            float bx = w * 0.5f, by = h * 0.5f;
            for (int srow = 0; srow < h; srow++)   // srow 0 = top on screen
            {
                float py = (srow + 0.5f) - by;     // py > 0 => toward bottom
                int arrayRow = h - 1 - srow;        // Texture2D row 0 is bottom
                for (int col = 0; col < w; col++)
                {
                    float pxpos = (col + 0.5f) - bx;
                    float d = SdRoundBox(pxpos, py, bx, by, rTL, rTR, rBR, rBL);
                    float aOuter = Coverage(d);
                    float aInner = bw > 0f ? Coverage(d + bw) : aOuter;
                    float borderFrac = Mathf.Max(0f, aOuter - aInner);
                    float fillFrac = aInner;
                    float outA = fill.a * fillFrac + border.a * borderFrac;
                    Color outC;
                    if (outA <= 0.0001f)
                    {
                        outC = new Color(fill.r, fill.g, fill.b, 0f);
                    }
                    else
                    {
                        float wr = fill.r * fill.a * fillFrac + border.r * border.a * borderFrac;
                        float wg = fill.g * fill.a * fillFrac + border.g * border.a * borderFrac;
                        float wb = fill.b * fill.a * fillFrac + border.b * border.a * borderFrac;
                        outC = new Color(wr / outA, wg / outA, wb / outA, outA);
                    }
                    px[arrayRow * w + col] = outC;
                }
            }
            return Bake(w, h, px);
        }

        /// <summary>Soft rounded shadow: black, fading over a ~2*blur px band. The shape is inset
        /// by <paramref name="blur"/> so the outer band fades to fully transparent at the texture
        /// edge - a real soft halo rather than a hard-clipped one. 9-slice with border radius+blur.</summary>
        public static Texture2D MakeShadow(int w, int h, int radius, float maxAlpha, float blur)
        {
            var px = new Color[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            float bx = cx - blur, by = cy - blur; // inset half-size leaves a blurred transparent margin
            for (int srow = 0; srow < h; srow++)
            {
                float py = (srow + 0.5f) - cy;
                int arrayRow = h - 1 - srow;
                for (int col = 0; col < w; col++)
                {
                    float pxpos = (col + 0.5f) - cx;
                    float d = SdRoundBox(pxpos, py, bx, by, radius, radius, radius, radius);
                    float a = maxAlpha * (1f - SmoothStep01(-blur, blur, d));
                    px[arrayRow * w + col] = new Color(0f, 0f, 0f, a);
                }
            }
            return Bake(w, h, px);
        }

        private static Texture2D Solid(Color c)
        {
            return Bake(1, 1, new[] { c });
        }

        private static Texture2D Bake(int w, int h, Color[] px)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false)
            {
                hideFlags = HideFlags.HideAndDontSave, // survive scene loads, never auto-unloaded
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels(px);
            tex.Apply(false, true); // upload + drop the CPU copy (we never read it again)
            return tex;
        }

        // signed distance to a rounded box with per-corner radius (iq's rounded box)
        private static float SdRoundBox(float px, float py, float bx, float by,
            float rTL, float rTR, float rBR, float rBL)
        {
            float r;
            if (px > 0f) r = (py > 0f) ? rBR : rTR;
            else r = (py > 0f) ? rBL : rTL;
            float qx = Mathf.Abs(px) - bx + r;
            float qy = Mathf.Abs(py) - by + r;
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            float outside = Mathf.Sqrt(ox * ox + oy * oy);
            float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
            return outside + inside - r;
        }

        // 1px anti-aliased coverage of a half-plane at signed distance d (negative = inside)
        private static float Coverage(float d)
        {
            return 1f - SmoothStep01(-0.5f, 0.5f, d);
        }

        private static float SmoothStep01(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // ---- color helpers --------------------------------------------------------------
        private static Color Rgb(int r, int g, int b, float a = 1f)
            => new Color(r / 255f, g / 255f, b / 255f, a);
        private static Color Mul(Color c, float m) => new Color(c.r * m, c.g * m, c.b * m, c.a);
        private static Color WithA(Color c, float a) => new Color(c.r, c.g, c.b, a);
        private static Color Lerp(Color a, Color b, float t) => Color.Lerp(a, b, t);
        private static Color Clear() => new Color(0f, 0f, 0f, 0f);
    }
}
