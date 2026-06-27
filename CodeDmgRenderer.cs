using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Reptile;

namespace BRCCodeDmg
{
    public sealed class CodeDmgRenderer
    {
        private readonly AppCodeDmg _app;

        private GameObject _root;
        private RectTransform _rootRect;

        private RawImage _screenImage;
        private Texture2D _screenTexture;
        private RawImage _gridOverlay;

        private TextMeshProUGUI _missingRomText;

        private const int GbWidth = 160;
        private const int GbHeight = 144;
        private const int Scale = 6;

        private Image _fullScreenBlack;
        private RectTransform _fullScreenBlackRect;

        private RawImage _logoImage;
        private Color32[] _blitBuffer;

        // 2P phone overlay
        private Canvas        _p2Canvas;
        private RectTransform _p2PhoneRect;
        private RawImage      _p2PhoneImage;
        private RawImage      _p2ScreenImage;
        private Texture2D     _p2ScreenTexture;
        private RectTransform _p2MiniLogoRect;
        private RawImage      _p2MiniLogoImage;
        private Color32[]     _p2BlitBuffer;
        private float         _p2PhoneWidth;
        private float         _p2PhoneHeight;

        // Link cable status display
        private TextMeshProUGUI _linkStatusText;
        private TextMeshProUGUI _linkDelayText;
        private TextMeshProUGUI _holdHintText;
        private TextMeshProUGUI _romMismatchText;
        private TextMeshProUGUI _desyncWarningText;
        private float _linkPendingAnimTimer;
        private int _linkPendingAnimStep;

        private static TMP_FontAsset _pressStartFont;
        private static bool _pressStartFontLoadAttempted;

        private static Color LinkBlue      => HexColor(0x3259a6);
        private static Color LinkBlueSecondary => HexColor(0x384d9e);

        private static Color HexColor(int rgb)
        {
            return new Color(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8)  & 0xFF) / 255f,
                ( rgb        & 0xFF) / 255f,
                1f);
        }

        private static TMP_FontAsset GetPressStartFont()
        {
            if (_pressStartFontLoadAttempted) return _pressStartFont;
            _pressStartFontLoadAttempted = true;

            try
            {
                string fontPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "fonts", "PressStart2P.ttf");
                if (!File.Exists(fontPath)) return null;

                var font = new Font(fontPath);
                if (font == null) return null;

                _pressStartFont = TMP_FontAsset.CreateFontAsset(font);
                if (_pressStartFont != null)
                    _pressStartFont.name = "PressStart2P";
            }
            catch { }

            return _pressStartFont;
        }

        private static void ApplyPressStartFont(TextMeshProUGUI text)
        {
            var font = GetPressStartFont();
            if (font == null || text == null) return;

            text.font = font;
            text.enableWordWrapping = false;
        }

        internal static string TruncatePlayerName(string name, int maxChars = 28, bool appendEllipsis = true)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            if (name.Length <= maxChars) return name;
            return appendEllipsis ? name.Substring(0, maxChars) + "..." : name.Substring(0, maxChars);
        }

        public CodeDmgRenderer(AppCodeDmg app)
        {
            _app = app;
        }

        public void Build()
        {
            // --- Background Layer ---
            var blackGo = new GameObject("FullScreenBlack", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _fullScreenBlackRect = blackGo.GetComponent<RectTransform>();
            _fullScreenBlackRect.SetParent(_app.transform, false);
            _fullScreenBlackRect.anchorMin = _fullScreenBlackRect.anchorMax = _fullScreenBlackRect.pivot = new Vector2(0.5f, 0.5f);
            _fullScreenBlackRect.anchoredPosition = new Vector2(0f, -40f);
            _fullScreenBlackRect.sizeDelta = new Vector2(2000f, 2000f);
            _fullScreenBlackRect.SetAsFirstSibling();

            _fullScreenBlack = blackGo.GetComponent<Image>();
            _fullScreenBlack.color = GetConfiguredBackground();
            _fullScreenBlack.raycastTarget = false;

            if (_root != null) UnityEngine.Object.Destroy(_root);

            // --- Root Container ---
            _root = new GameObject("CodeDmgRoot", typeof(RectTransform));
            _rootRect = _root.GetComponent<RectTransform>();
            _rootRect.SetParent(_app.transform, false);
            _rootRect.anchorMin = _rootRect.anchorMax = _rootRect.pivot = new Vector2(0.5f, 0.5f);
            _rootRect.anchoredPosition = new Vector2(0f, -70f);
            _rootRect.localScale = Vector3.one;
            _rootRect.sizeDelta = new Vector2(GbWidth * Scale, GbHeight * Scale);

            // --- Main Game Screen ---
            var screenGo = new GameObject("Screen", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var screenRect = screenGo.GetComponent<RectTransform>();
            screenRect.SetParent(_rootRect, false);
            screenRect.anchorMin = screenRect.anchorMax = screenRect.pivot = new Vector2(0.5f, 0.5f);
            screenRect.anchoredPosition = Vector2.zero;
            screenRect.sizeDelta = _rootRect.sizeDelta;

            _screenImage = screenGo.GetComponent<RawImage>();
            _screenImage.color = Color.white;
            _screenImage.raycastTarget = false;

            _screenTexture = new Texture2D(GbWidth, GbHeight, TextureFormat.RGBA32, false, false);
            _screenTexture.filterMode = FilterMode.Point; // GPU handles 6× scaling
            _screenTexture.wrapMode = TextureWrapMode.Clamp;
            _screenImage.texture = _screenTexture;

            _blitBuffer = new Color32[GbWidth * GbHeight];
            ClearTexture();

            var gridGo = new GameObject("GridOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var gridRect = gridGo.GetComponent<RectTransform>();

            gridRect.SetParent(screenRect, false);

            gridRect.anchorMin = Vector2.zero;
            gridRect.anchorMax = Vector2.one;
            gridRect.offsetMin = Vector2.zero;
            gridRect.offsetMax = Vector2.zero;

            gridRect.localPosition = new Vector3(0, 0, -1f);
            gridRect.localScale = Vector3.one;

            _gridOverlay = gridGo.GetComponent<RawImage>();
            _gridOverlay.texture = CreateGridTexture();
            _gridOverlay.uvRect = new Rect(0, 0, GbWidth, GbHeight);
            _gridOverlay.raycastTarget = false;

            _gridOverlay.enabled = CodeDmgPlugin.ConfigSettings?.PixelGrid.Value ?? false;

            CreateLogo(_rootRect.sizeDelta.x, _rootRect.sizeDelta.y);
            CreateTopLogo(_rootRect.sizeDelta.x, _rootRect.sizeDelta.y);
            CreateMissingRomText();

            if (CodeDmgPlugin.LinkCable != null)
                CreateLinkCableUI(_rootRect.sizeDelta.x, _rootRect.sizeDelta.y);

            Build2PScreen();
        }

        private Texture2D CreateGridTexture()
        {
            // 16x16 resolution for smooth downsampling
            int size = 16;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Repeat;

            Color32 clear = new Color32(0, 0, 0, 0);

            Color32 line = new Color32(0, 0, 0, 240);

            Color32[] pixels = new Color32[size * size];

            int lineThickness = 4; //3

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Draw lines on the edges (tiled across 160x144)
                    bool isVerticalEdge = x >= (size - lineThickness);
                    bool isHorizontalEdge = y < lineThickness;

                    pixels[y * size + x] = (isVerticalEdge || isHorizontalEdge) ? line : clear;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        // ── 2P Phone ────────────────────────────────────────────────────
        private const float P2PhoneAspect = 323f / 429f;
        private const float P2ScreenUMin = 0.0712f;
        private const float P2ScreenUMax = 0.9288f;
        private const float P2ScreenVMin = 0.1958f;
        private const float P2ScreenVMax = 0.8834f;
        private const float P2ScreenYOffset = 4f;
        private const float P2MiniLogoGap = 6f;

        public void Teardown()
        {
            HideSecondPlayerScreen();
        }

        public void HideSecondPlayerScreen()
        {
            if (_p2Canvas != null)
                _p2Canvas.enabled = false;

            if (_p2ScreenImage != null)
                _p2ScreenImage.enabled = false;
        }

        private void Build2PScreen()
        {
            if (_p2Canvas != null)
            {
                UnityEngine.Object.Destroy(_p2Canvas.gameObject);
                _p2Canvas = null;
            }

            var canvasGo = new GameObject("P2PhoneCanvas");
            _p2Canvas = canvasGo.AddComponent<Canvas>();
            _p2Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _p2Canvas.sortingOrder = 10;
            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            _p2Canvas.enabled = false;

            _p2PhoneHeight = GbHeight * Scale * 0.5f;
            _p2PhoneWidth  = _p2PhoneHeight * P2PhoneAspect;

            // ── 2P Phone body ───────────────────────────────────────────────
            string phonePng = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "gfx", "flipPhoneTex_PhoneOpen2P.png");
            Texture2D phoneTex = null;
            if (System.IO.File.Exists(phonePng))
            {
                byte[] bytes = System.IO.File.ReadAllBytes(phonePng);
                phoneTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                phoneTex.filterMode = FilterMode.Bilinear;
                phoneTex.wrapMode   = TextureWrapMode.Clamp;
                if (!ImageConversion.LoadImage(phoneTex, bytes, false)) phoneTex = null;
            }

            var phoneGo = new GameObject("P2PhoneBody",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            _p2PhoneRect = phoneGo.GetComponent<RectTransform>();
            _p2PhoneRect.SetParent(_p2Canvas.transform, false);
            _p2PhoneRect.anchorMin = _p2PhoneRect.anchorMax = new Vector2(0f, 0f);
            _p2PhoneRect.pivot = new Vector2(0f, 0.5f);
            _p2PhoneRect.sizeDelta = new Vector2(_p2PhoneWidth, _p2PhoneHeight);

            _p2PhoneImage = phoneGo.GetComponent<RawImage>();
            _p2PhoneImage.color = Color.white;
            _p2PhoneImage.raycastTarget = false;
            if (phoneTex != null) _p2PhoneImage.texture = phoneTex;

            // ── Emulator screen ─────────────────────────────────────────────
            float availW = (P2ScreenUMax - P2ScreenUMin) * _p2PhoneWidth;
            float availH = (P2ScreenVMax - P2ScreenVMin) * _p2PhoneHeight;
            int   p2Scale = Mathf.Max(1, (int)Mathf.Min(availW / GbWidth, availH / GbHeight));
            float screenW = GbWidth  * p2Scale;
            float screenH = GbHeight * p2Scale;
            float screenOffX = (P2ScreenUMin + P2ScreenUMax) * 0.5f * _p2PhoneWidth;
            float screenOffY = ((P2ScreenVMin + P2ScreenVMax) * 0.5f - 0.5f) * _p2PhoneHeight + P2ScreenYOffset;

            var screenGo = new GameObject("P2EmuScreen",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var screenRt = screenGo.GetComponent<RectTransform>();
            screenRt.SetParent(_p2PhoneRect, false);
            screenRt.anchorMin = screenRt.anchorMax = screenRt.pivot = new Vector2(0.5f, 0.5f);
            screenRt.anchoredPosition = new Vector2(
                screenOffX - _p2PhoneWidth * 0.5f, screenOffY);
            screenRt.sizeDelta = new Vector2(screenW, screenH);

            _p2ScreenImage = screenGo.GetComponent<RawImage>();
            _p2ScreenImage.color = Color.white;
            _p2ScreenImage.raycastTarget = false;
            _p2ScreenImage.enabled = false;

            _p2ScreenTexture = new Texture2D(GbWidth, GbHeight, TextureFormat.RGBA32, false, false);
            _p2ScreenTexture.filterMode = FilterMode.Point;
            _p2ScreenTexture.wrapMode   = TextureWrapMode.Clamp;
            _p2ScreenImage.texture = _p2ScreenTexture;

            CreateP2MiniLogo(screenW, screenH, screenRt.anchoredPosition.y);

            _p2BlitBuffer = new Color32[GbWidth * GbHeight];
        }

        private void CreateP2MiniLogo(float screenW, float screenH, float screenY)
        {
            string logoPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "gfx", "microboyminilogo.png");
            if (!System.IO.File.Exists(logoPath)) return;

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(logoPath); } catch { return; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!ImageConversion.LoadImage(tex, bytes, false)) return;

            var go = new GameObject("P2MicroBoyMiniLogo",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            _p2MiniLogoRect = go.GetComponent<RectTransform>();
            _p2MiniLogoRect.SetParent(_p2PhoneRect, false);
            _p2MiniLogoRect.anchorMin = _p2MiniLogoRect.anchorMax = _p2MiniLogoRect.pivot = new Vector2(0.5f, 0.5f);

            _p2MiniLogoImage = go.GetComponent<RawImage>();
            _p2MiniLogoImage.texture = tex;
            _p2MiniLogoImage.color = Color.white;
            _p2MiniLogoImage.raycastTarget = false;

            UpdateP2MiniLogoLayout(screenW, screenH, screenY);
        }

        private void UpdateP2MiniLogoLayout(float screenW, float screenH, float screenY)
        {
            if (_p2MiniLogoRect == null || _p2MiniLogoImage == null || _p2MiniLogoImage.texture == null) return;

            float logoW = screenW * 0.5833f;
            float logoH = logoW * ((float)_p2MiniLogoImage.texture.height / _p2MiniLogoImage.texture.width);
            _p2MiniLogoRect.sizeDelta = new Vector2(logoW, logoH);
            _p2MiniLogoRect.anchoredPosition = new Vector2(0f, screenY - screenH * 0.5f - P2MiniLogoGap - logoH * 0.5f);
        }

        private void UpdateP2PhonePosition()
        {
            if (_p2PhoneRect == null) return;

            var phoneCanvas = _app.GetComponentInParent<Canvas>();
            Camera uiCam = phoneCanvas != null ? phoneCanvas.worldCamera : null;

            var appRect = _app.GetComponent<RectTransform>();
            if (appRect == null) return;

            Vector3[] corners = new Vector3[4];
            appRect.GetWorldCorners(corners); // corners[2]=TR, corners[3]=BR

            float rightEdge = uiCam != null
                ? uiCam.WorldToScreenPoint(corners[2]).x
                : corners[2].x;

            float phoneH = uiCam != null
                ? uiCam.WorldToScreenPoint(corners[2]).y - uiCam.WorldToScreenPoint(corners[3]).y
                : corners[2].y - corners[3].y;

            float targetH = Mathf.Max(1f, phoneH * 0.5f);
            float targetW = targetH * P2PhoneAspect;
            _p2PhoneRect.sizeDelta = new Vector2(targetW, targetH);

            var screenRt = _p2ScreenImage?.GetComponent<RectTransform>();
            if (screenRt != null)
            {
                float aW = (P2ScreenUMax - P2ScreenUMin) * targetW;
                float aH = (P2ScreenVMax - P2ScreenVMin) * targetH;
                int   sc = Mathf.Max(1, (int)Mathf.Min(aW / GbWidth, aH / GbHeight));
                screenRt.sizeDelta = new Vector2(GbWidth * sc, GbHeight * sc);
                float screenY = ((P2ScreenVMin + P2ScreenVMax) * 0.5f - 0.5f) * targetH + P2ScreenYOffset;
                screenRt.anchoredPosition = new Vector2(
                    (P2ScreenUMin + P2ScreenUMax) * 0.5f * targetW - targetW * 0.5f,
                    screenY);
                UpdateP2MiniLogoLayout(screenRt.sizeDelta.x, screenRt.sizeDelta.y, screenY);
            }

            _p2PhoneRect.anchoredPosition = new Vector2(rightEdge + 32f, targetH * 0.5f);
        }


        public void Render(CodeDmgEmulator emulator)
        {
            if (_fullScreenBlack != null)
                _fullScreenBlack.color = GetConfiguredBackground();

            if (_missingRomText != null)
                _missingRomText.enabled = emulator == null;

            // Refresh link cable status each render
            if (_linkStatusText != null)
                RenderLinkCableStatus(CodeDmgPlugin.LinkCable);

            if (emulator == null || _screenTexture == null)
                return;

            // Live toggle update
            bool gridOn = CodeDmgPlugin.ConfigSettings?.PixelGrid.Value ?? false;
            if (_gridOverlay != null && _gridOverlay.enabled != gridOn)
                _gridOverlay.enabled = gridOn;

            //Color32[] source = emulator.Ppu.GetUnityFrame();
            //UpscaleAndBlit(source, _blitBuffer);

            Color32[] source = emulator.Ppu.GetUnityFrame();
            FlipRowsForUnity(source, _blitBuffer);

            _screenTexture.SetPixels32(_blitBuffer);
            _screenTexture.Apply(false, false);

            emulator.Ppu.ClearDirtyFlag();

            bool show2P   = CodeDmgPlugin.ConfigSettings?.ShowPeerScreen?.Value ?? false;
            bool linked2P = show2P && CodeDmgPlugin.LinkCable?.State == LinkCableState.Connected;


            if (linked2P)
            {
                var core = Reptile.Core.Instance;
                if (core != null && core.IsCorePaused)
                {
                    AppCodeDmg.HandleGamePauseStarted();
                    linked2P = false;
                }
            }
            if (_p2Canvas != null) _p2Canvas.enabled = linked2P;

            if (linked2P)
            {
                UpdateP2PhonePosition();

                var linked = emulator.LinkedEmulator;
                bool hasLinked = linked != null;
                if (_p2ScreenImage != null && _p2ScreenImage.enabled != hasLinked)
                    _p2ScreenImage.enabled = hasLinked;

                if (hasLinked)
                {
                    FlipRowsForUnity(linked.Ppu.GetUnityFrame(), _p2BlitBuffer);
                    linked.Ppu.ClearDirtyFlag();
                    _p2ScreenTexture.SetPixels32(_p2BlitBuffer);
                    _p2ScreenTexture.Apply(false, false);
                }
            }
        }

        private static void FlipRowsForUnity(Color32[] source, Color32[] dest)
        {
            for (int y = 0; y < GbHeight; y++)
            {
                int srcRow = y * GbWidth;
                int dstRow = (GbHeight - 1 - y) * GbWidth;
                for (int x = 0; x < GbWidth; x++)
                    dest[dstRow + x] = source[srcRow + x];
            }
        }


        private static Color GetConfiguredBackground()
        {
            string hex = CodeDmgPlugin.ConfigSettings?.BackgroundColor.Value ?? "#000000";
            hex = hex.TrimStart('#').Trim();
            if (hex.Length < 6) return Color.black;
            try
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                byte a = hex.Length >= 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
                return new Color32(r, g, b, a);
            }
            catch { return Color.black; }
        }

        private void ClearTexture()
        {
            Color32 black = new Color32(0, 0, 0, 255);
            for (int i = 0; i < _blitBuffer.Length; i++) _blitBuffer[i] = black;
            _screenTexture.SetPixels32(_blitBuffer);
            _screenTexture.Apply(false, false);
        }

        private void CreateLogo(float screenWidth, float screenHeight)
        {
            string logoPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "gfx", "microboylogo.png");
            if (!System.IO.File.Exists(logoPath)) return;

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(logoPath); } catch { return; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!ImageConversion.LoadImage(tex, bytes, false)) return;

            float logoW = screenWidth * 0.7f;
            float logoH = logoW * ((float)tex.height / tex.width);

            var go = new GameObject("MicroBoyLogo", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_rootRect, false);

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0f, 1f);
            float paddingX = 5f;
            float paddingY = -15f;

            rect.anchoredPosition = new Vector2(paddingX, paddingY);
            rect.sizeDelta = new Vector2(logoW, logoH);

            _logoImage = go.GetComponent<RawImage>();
            _logoImage.texture = tex;
            _logoImage.raycastTarget = false;
        }

        private void CreateTopLogo(float screenWidth, float screenHeight)
        {
            string logoPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "gfx", "toplogo.png");

            if (!System.IO.File.Exists(logoPath))
            {
                Debug.LogError($"[CodeDmgRenderer] Error: Required file 'gfx/toplogo.png' was not found at {logoPath}.");
                return;
            }

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(logoPath); }
            catch (Exception e)
            {
                Debug.LogError($"[CodeDmgRenderer] Error: Could not read 'gfx/toplogo.png'. {e.Message}");
                return;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);

            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            if (!ImageConversion.LoadImage(tex, bytes, false))
            {
                Debug.LogError($"[CodeDmgRenderer] Error: ImageConversion.LoadImage failed for 'gfx/toplogo.png'.");
                return;
            }

            float logoW = screenWidth;
            float logoH = logoW * ((float)tex.height / tex.width);

            float gap = 15f;
            float centerY = (screenHeight * 0.5f) + gap + (logoH * 0.5f);

            var go = new GameObject("SkateMatrixLogo", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_rootRect, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, centerY);
            rect.sizeDelta = new Vector2(logoW, logoH);

            var topLogoImage = go.GetComponent<RawImage>();
            topLogoImage.texture = tex;
            topLogoImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            topLogoImage.color = Color.white;
            topLogoImage.raycastTarget = false;
        }

        private void CreateLinkCableUI(float screenWidth, float screenHeight)
        {
            if (_logoImage == null) return;

            float logoH = _logoImage.rectTransform.sizeDelta.y;
            float holdHintY = -15f - logoH - 4f;

            // Menu Hint
            var holdHintGo = new GameObject("HoldHint", typeof(RectTransform));
            var holdHintRect = holdHintGo.GetComponent<RectTransform>();
            holdHintRect.SetParent(_rootRect, false);
            holdHintRect.anchorMin = Vector2.zero;
            holdHintRect.anchorMax = Vector2.zero;
            holdHintRect.pivot = new Vector2(0f, 1f);
            holdHintRect.anchoredPosition = new Vector2(5f, holdHintY);
            holdHintRect.sizeDelta = new Vector2(screenWidth * 0.95f, 34f);

            _holdHintText = holdHintGo.AddComponent<TextMeshProUGUI>();
            _holdHintText.fontSize = 24f;
            _holdHintText.color = HexColor(0x828282);
            _holdHintText.alignment = TextAlignmentOptions.TopLeft;
            _holdHintText.text = "HOLD START & SELECT FOR MENU";
            _holdHintText.raycastTarget = false;
            ApplyPressStartFont(_holdHintText);
            _holdHintText.gameObject.SetActive(true);

            // Link Cable Status
            float statusY = holdHintY - 32f;
            var statusGo = new GameObject("LinkCableStatus", typeof(RectTransform));
            var statusRect = statusGo.GetComponent<RectTransform>();
            statusRect.SetParent(_rootRect, false);
            statusRect.anchorMin = Vector2.zero;
            statusRect.anchorMax = Vector2.zero;
            statusRect.pivot = new Vector2(0f, 1f);
            statusRect.anchoredPosition = new Vector2(5f, statusY);
            statusRect.sizeDelta = new Vector2(screenWidth * 0.95f, 34f);

            _linkStatusText = statusGo.AddComponent<TextMeshProUGUI>();
            _linkStatusText.fontSize = 24f;
            _linkStatusText.color = Color.red;
            _linkStatusText.alignment = TextAlignmentOptions.TopLeft;
            _linkStatusText.text = "LINK: DISCONNECTED";
            _linkStatusText.raycastTarget = false;
            ApplyPressStartFont(_linkStatusText);

            // Delay
            float delayY = statusY - 36f;
            var delayGo = new GameObject("LinkCableDelay", typeof(RectTransform));
            var delayRect = delayGo.GetComponent<RectTransform>();
            delayRect.SetParent(_rootRect, false);
            delayRect.anchorMin = Vector2.zero;
            delayRect.anchorMax = Vector2.zero;
            delayRect.pivot = new Vector2(0f, 1f);
            delayRect.anchoredPosition = new Vector2(5f, delayY);
            delayRect.sizeDelta = new Vector2(screenWidth * 0.95f, 32f);

            _linkDelayText = delayGo.AddComponent<TextMeshProUGUI>();
            _linkDelayText.fontSize = 24f;
            _linkDelayText.color = Color.yellow;
            _linkDelayText.alignment = TextAlignmentOptions.TopLeft;
            _linkDelayText.text = string.Empty;
            _linkDelayText.raycastTarget = false;
            ApplyPressStartFont(_linkDelayText);
            _linkDelayText.gameObject.SetActive(false);

            // ROM Warning
            float mismatchY = statusY - 34f;
            var mismatchGo = new GameObject("RomMismatch", typeof(RectTransform));
            var mismatchRect = mismatchGo.GetComponent<RectTransform>();
            mismatchRect.SetParent(_rootRect, false);
            mismatchRect.anchorMin = Vector2.zero;
            mismatchRect.anchorMax = Vector2.zero;
            mismatchRect.pivot = new Vector2(0f, 1f);
            mismatchRect.anchoredPosition = new Vector2(5f, mismatchY);
            mismatchRect.sizeDelta = new Vector2(screenWidth * 0.95f, 56f);

            _romMismatchText = mismatchGo.AddComponent<TextMeshProUGUI>();
            _romMismatchText.fontSize = 24f;
            _romMismatchText.color = Color.yellow;
            _romMismatchText.alignment = TextAlignmentOptions.TopLeft;
            _romMismatchText.text = string.Empty;
            _romMismatchText.raycastTarget = false;
            ApplyPressStartFont(_romMismatchText);
            _romMismatchText.gameObject.SetActive(false);

            float desyncY = mismatchY - 56f;
            var desyncGo = new GameObject("DesyncWarning", typeof(RectTransform));
            var desyncRect = desyncGo.GetComponent<RectTransform>();
            desyncRect.SetParent(_rootRect, false);
            desyncRect.anchorMin = Vector2.zero;
            desyncRect.anchorMax = Vector2.zero;
            desyncRect.pivot = new Vector2(0f, 1f);
            desyncRect.anchoredPosition = new Vector2(5f, desyncY);
            desyncRect.sizeDelta = new Vector2(screenWidth * 0.95f, 34f);

            _desyncWarningText = desyncGo.AddComponent<TextMeshProUGUI>();
            _desyncWarningText.fontSize = 24f;
            _desyncWarningText.color = Color.yellow;
            _desyncWarningText.alignment = TextAlignmentOptions.TopLeft;
            _desyncWarningText.text = string.Empty;
            _desyncWarningText.raycastTarget = false;
            ApplyPressStartFont(_desyncWarningText);
            _desyncWarningText.gameObject.SetActive(false);
        }

        private void UpdateLinkPendingAnimation(bool active)
        {
            if (!active)
            {
                _linkPendingAnimTimer = 0f;
                _linkPendingAnimStep = 0;
                return;
            }

            _linkPendingAnimTimer += Time.unscaledDeltaTime;
            if (_linkPendingAnimTimer >= 0.35f)
            {
                _linkPendingAnimTimer = 0f;
                _linkPendingAnimStep = (_linkPendingAnimStep + 1) % 4;
            }
        }

        private string GetLinkPendingDots()
        {
            switch (_linkPendingAnimStep)
            {
                case 1: return ".";
                case 2: return "..";
                case 3: return "...";
                default: return string.Empty;
            }
        }

        public void RenderLinkCableStatus(LinkCableManager lc)
        {
            if (_linkStatusText == null) return;

            if (_holdHintText != null) _holdHintText.gameObject.SetActive(true);

            if (lc == null || !CodeDmgPlugin.ConfigSettings.LinkCableEnabled.Value)
            {
                _linkStatusText.gameObject.SetActive(false);
                if (_linkDelayText != null) _linkDelayText.gameObject.SetActive(false);
                return;
            }

            if (lc.HasFailureStatus)
            {
                UpdateLinkPendingAnimation(false);
                _linkStatusText.text = lc.FailureStatusText;
                _linkStatusText.color = Color.red;
                _linkStatusText.gameObject.SetActive(true);
                if (_linkDelayText != null) _linkDelayText.gameObject.SetActive(false);
                // fall through to show _romMismatchText below if active
            }
            else
            {

            bool pending = lc.State == LinkCableState.Linking || lc.State == LinkCableState.WaitingAccept;
            UpdateLinkPendingAnimation(pending);

            switch (lc.State)
            {
                case LinkCableState.Disconnected:
                    _linkStatusText.text = "LINK: DISCONNECTED";
                    _linkStatusText.color = Color.red;
                    _linkStatusText.gameObject.SetActive(!(CodeDmgPlugin.ConfigSettings.HideLinkStatusWhenDisconnected?.Value ?? true));
                    if (_linkDelayText != null) _linkDelayText.gameObject.SetActive(false);
                    break;
                case LinkCableState.Linking:
                case LinkCableState.WaitingAccept:
                    string pendingName = string.IsNullOrEmpty(lc.LinkedPlayerName) ? "PLAYER" : TruncatePlayerName(lc.LinkedPlayerName.ToUpperInvariant(), 28, false);
                    _linkStatusText.text = "LINKING: " + pendingName + GetLinkPendingDots();
                    _linkStatusText.color = Color.yellow;
                    _linkStatusText.gameObject.SetActive(true);
                    if (_linkDelayText != null) _linkDelayText.gameObject.SetActive(false);
                    break;
                case LinkCableState.Connected:
                    string linkedName = string.IsNullOrEmpty(lc.LinkedPlayerName) ? "???" : TruncatePlayerName(lc.LinkedPlayerName);
                    _linkStatusText.text = "LINKED: " + linkedName;
                    _linkStatusText.color = Color.green;
                    _linkStatusText.gameObject.SetActive(true);
                    if (_linkDelayText != null)
                    {
                        _linkDelayText.text = "DELAY: " + lc.EffectiveLinkInputDelayFrames;
                        _linkDelayText.gameObject.SetActive(true);
                    }
                    break;
            }
            } // end else (not HasFailureStatus)

            if (_romMismatchText != null)
            {
                bool show = lc.ShowRomMismatch;
                _romMismatchText.gameObject.SetActive(show);
                if (show) _romMismatchText.text = lc.RomMismatchMessage ?? "Different ROMs detected!";
            }

            if (_desyncWarningText != null)
            {
                bool show = lc.ShowDesyncWarning;
                _desyncWarningText.gameObject.SetActive(show);
                if (show) _desyncWarningText.text = "Possible desync detected!!";
            }
        }

        private void CreateMissingRomText()
        {
            var go = new GameObject("MissingRomText", typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_rootRect, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(220f, 50f);
            rect.anchoredPosition = Vector2.zero;
            _missingRomText = go.AddComponent<TextMeshProUGUI>();
            _missingRomText.text = "Missing rom.gb";
            _missingRomText.alignment = TextAlignmentOptions.Center;
            _missingRomText.fontSize = 10f;
            _missingRomText.color = Color.white;
            _missingRomText.enabled = false;
        }
    }
}