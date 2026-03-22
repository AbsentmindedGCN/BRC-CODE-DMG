using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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

        //private const int TexWidth = GbWidth * Scale;
        //private const int TexHeight = GbHeight * Scale;

        private RawImage _logoImage;
        private Color32[] _blitBuffer;

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

            /*
            _screenTexture = new Texture2D(TexWidth, TexHeight, TextureFormat.RGBA32, false, false);
            _screenTexture.filterMode = FilterMode.Point;
            _screenTexture.wrapMode = TextureWrapMode.Clamp;
            _screenImage.texture = _screenTexture;

            _blitBuffer = new Color32[TexWidth * TexHeight];
            */

            _screenTexture = new Texture2D(GbWidth, GbHeight, TextureFormat.RGBA32, false, false);
            _screenTexture.filterMode = FilterMode.Point; // GPU handles 6× scaling
            _screenTexture.wrapMode = TextureWrapMode.Clamp;
            _screenImage.texture = _screenTexture;

            _blitBuffer = new Color32[GbWidth * GbHeight];
            ClearTexture();

            // --- Grid Overlay (The Fix) ---
            var gridGo = new GameObject("GridOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var gridRect = gridGo.GetComponent<RectTransform>();

            // Parent to screenGo so they stay perfectly aligned
            gridRect.SetParent(screenRect, false);

            // Anchors to 0,0 and 1,1 forces it to match the parent size perfectly
            gridRect.anchorMin = Vector2.zero;
            gridRect.anchorMax = Vector2.one;
            gridRect.offsetMin = Vector2.zero;
            gridRect.offsetMax = Vector2.zero;

            // Fix: Move slightly forward on Z to ensure it's not inside/behind the screen texture
            gridRect.localPosition = new Vector3(0, 0, -1f);
            gridRect.localScale = Vector3.one;

            _gridOverlay = gridGo.GetComponent<RawImage>();
            _gridOverlay.texture = CreateGridTexture();
            _gridOverlay.uvRect = new Rect(0, 0, GbWidth, GbHeight);
            _gridOverlay.raycastTarget = false;

            // Make sure it starts visible if the config says so
            _gridOverlay.enabled = CodeDmgPlugin.ConfigSettings?.PixelGrid.Value ?? false;

            CreateLogo(_rootRect.sizeDelta.x, _rootRect.sizeDelta.y);
            CreateTopLogo(_rootRect.sizeDelta.x, _rootRect.sizeDelta.y);
            CreateMissingRomText();
        }

        private Texture2D CreateGridTexture()
        {
            // 16x16 resolution for smooth downsampling
            int size = 16;

            // Enable Mipmaps (true) to prevent moiré/checkerboarding
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Repeat;

            Color32 clear = new Color32(0, 0, 0, 0);

            // CRITICAL CHANGE: Increased alpha to 220 for a much darker/stronger grid.
            // (255 is fully black, 0 is invisible)
            Color32 line = new Color32(0, 0, 0, 240);

            Color32[] pixels = new Color32[size * size];

            // CRITICAL CHANGE: Increased thickness to 3 for wider, more visible "pixel gaps"
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

        public void Render(CodeDmgEmulator emulator)
        {
            if (_fullScreenBlack != null)
                _fullScreenBlack.color = GetConfiguredBackground();

            if (_missingRomText != null)
                _missingRomText.enabled = emulator == null;

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
        }

        /*
        private static void UpscaleAndBlit(Color32[] src, Color32[] dst)
        {
            for (int gbY = 0; gbY < GbHeight; gbY++)
            {
                int srcRow = gbY * GbWidth;
                int dstRowBase = (GbHeight - 1 - gbY) * Scale * TexWidth;
                for (int gbX = 0; gbX < GbWidth; gbX++)
                {
                    Color32 pixel = src[srcRow + gbX];
                    int dstColBase = gbX * Scale;
                    for (int dy = 0; dy < Scale; dy++)
                    {
                        int dstRow = dstRowBase + dy * TexWidth;
                        for (int dx = 0; dx < Scale; dx++)
                            dst[dstRow + dstColBase + dx] = pixel;
                    }
                }
            }
        }
        */

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
        /*
        private void CreateLogo(float screenWidth, float screenHeight)
        {
            string logoPath = System.IO.Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "microboylogo.png");
            if (!System.IO.File.Exists(logoPath)) return;
            byte[] bytes = System.IO.File.ReadAllBytes(logoPath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!ImageConversion.LoadImage(tex, bytes, false)) return;

            float logoW = screenWidth * 0.8f;
            float logoH = logoW * ((float)tex.height / tex.width);
            float centerY = -(screenHeight * 0.5f) - 128f - (logoH * 0.5f); //-28

            var go = new GameObject("MicroBoyLogo", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_rootRect, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, centerY);
            rect.sizeDelta = new Vector2(logoW, logoH);
            _logoImage = go.GetComponent<RawImage>();
            _logoImage.texture = tex;
            _logoImage.raycastTarget = false;
        }
        */

        private void CreateLogo(float screenWidth, float screenHeight)
        {
            string logoPath = System.IO.Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "microboylogo.png");
            if (!System.IO.File.Exists(logoPath)) return;

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(logoPath); } catch { return; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!ImageConversion.LoadImage(tex, bytes, false)) return;

            // --- SIZE RESTORATION ---
            // Bumped back up to 70% of screen width. Change to 0.8f for even bigger.
            float logoW = screenWidth * 0.7f;
            float logoH = logoW * ((float)tex.height / tex.width);

            var go = new GameObject("MicroBoyLogo", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_rootRect, false);

            // --- POSITIONING (Bottom-Left) ---
            // Anchors at (0,0) = Bottom Left of the game screen container
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;

            // Pivot at (0,1) means the "handle" is the Top-Left of the logo
            rect.pivot = new Vector2(0f, 1f);

            // Offset to give it some breathing room from the edges
            float paddingX = 5f;
            float paddingY = -15f; // Moves it 15 pixels below the screen

            rect.anchoredPosition = new Vector2(paddingX, paddingY);
            rect.sizeDelta = new Vector2(logoW, logoH);

            _logoImage = go.GetComponent<RawImage>();
            _logoImage.texture = tex;
            _logoImage.raycastTarget = false;
        }

        private void CreateTopLogo(float screenWidth, float screenHeight)
        {
            string logoPath = System.IO.Path.Combine(
                CodeDmgPlugin.Instance.PluginDirectory, "toplogo.png");

            if (!System.IO.File.Exists(logoPath))
            {
                Debug.LogError($"[CodeDmgRenderer] Error: Required file 'toplogo.png' was not found at {logoPath}.");
                return;
            }

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(logoPath); }
            catch (Exception e)
            {
                Debug.LogError($"[CodeDmgRenderer] Error: Could not read 'toplogo.png'. {e.Message}");
                return;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            // Using Point filtering for logos with sharp edges/stripes is often best.
            // Change to Bilinear if the lines look jagged when scaled.
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            if (!ImageConversion.LoadImage(tex, bytes, false))
            {
                Debug.LogError($"[CodeDmgRenderer] Error: ImageConversion.LoadImage failed for 'toplogo.png'.");
                return;
            }

            // Keep the text banner matching the screen width.
            float logoW = screenWidth;
            // Calculate proportional height based on the aspect ratio of toplogo.png.
            float logoH = logoW * ((float)tex.height / tex.width);

            // --- POSITIONING ---
            // A gap of 15f from the top edge of the screen to the bottom of the banner.
            float gap = 15f;
            // We add these positive values to move upwards from the center.
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