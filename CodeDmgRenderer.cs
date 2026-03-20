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

        private TextMeshProUGUI _missingRomText;

        private const int GbWidth = 160;
        private const int GbHeight = 144;

        // Area available on the phone app for the emulator screen.
        // These numbers are UI-space guesses that fit well on the BRC phone.
        // The integer scale is calculated from them.
        private const float MaxScreenWidth = 480f;
        private const float MaxScreenHeight = 430f;

        private const int Scale = 6; // <- change this anytime (2, 3, 4, 6, etc.)

        private Image _fullScreenBlack;
        private RectTransform _fullScreenBlackRect;

        private Color32[] _blitBuffer;

        public CodeDmgRenderer(AppCodeDmg app)
        {
            _app = app;
        }

        public void Build()
        {

            var blackGo = new GameObject("FullScreenBlack", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _fullScreenBlackRect = blackGo.GetComponent<RectTransform>();
            _fullScreenBlackRect.SetParent(_app.transform, false);

            _fullScreenBlackRect.anchorMin = new Vector2(0.5f, 0.5f);
            _fullScreenBlackRect.anchorMax = new Vector2(0.5f, 0.5f);
            _fullScreenBlackRect.pivot = new Vector2(0.5f, 0.5f);
            _fullScreenBlackRect.anchoredPosition = new Vector2(0f, -40f);
            _fullScreenBlackRect.sizeDelta = new Vector2(2000f, 2000f);
            _fullScreenBlackRect.SetAsFirstSibling();

            _fullScreenBlack = blackGo.GetComponent<Image>();
            _fullScreenBlack.color = Color.black;
            _fullScreenBlack.raycastTarget = false;


            if (_root != null)
                Object.Destroy(_root);

            _root = new GameObject("CodeDmgRoot", typeof(RectTransform));
            _rootRect = _root.GetComponent<RectTransform>();
            _rootRect.SetParent(_app.transform, false);
            _rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rootRect.pivot = new Vector2(0.5f, 0.5f);
            _rootRect.anchoredPosition = new Vector2(0f, -70f);
            _rootRect.localScale = Vector3.one;

            int scale = Scale;
            float scaledWidth = GbWidth * scale;
            float scaledHeight = GbHeight * scale;

            _rootRect.sizeDelta = new Vector2(scaledWidth, scaledHeight);

            var screenGo = new GameObject("Screen", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var screenRect = screenGo.GetComponent<RectTransform>();
            screenRect.SetParent(_rootRect, false);
            screenRect.anchorMin = new Vector2(0.5f, 0.5f);
            screenRect.anchorMax = new Vector2(0.5f, 0.5f);
            screenRect.pivot = new Vector2(0.5f, 0.5f);
            screenRect.anchoredPosition = Vector2.zero;
            screenRect.sizeDelta = new Vector2(scaledWidth, scaledHeight);

            _screenImage = screenGo.GetComponent<RawImage>();
            _screenImage.color = Color.white;
            _screenImage.texture = null;
            _screenImage.raycastTarget = false;

            _screenTexture = new Texture2D(GbWidth, GbHeight, TextureFormat.RGBA32, false, false);
            _screenTexture.filterMode = FilterMode.Point;
            _screenTexture.wrapMode = TextureWrapMode.Clamp;

            _blitBuffer = new Color32[GbWidth * GbHeight];

            ClearTexture();
            _screenImage.texture = _screenTexture;

            CreateMissingRomText();
        }

        public void Render(CodeDmgEmulator emulator)
        {
            if (_missingRomText != null)
                _missingRomText.enabled = emulator == null;

            if (emulator == null || _screenTexture == null)
                return;

            Color32[] source = emulator.Ppu.GetUnityFrame();
            FlipRowsForUnity(source, _blitBuffer);

            _screenTexture.SetPixels32(_blitBuffer);
            _screenTexture.Apply(false, false);

            emulator.Ppu.ClearDirtyFlag();
        }

        /*
        private int GetIntegerScale(float maxWidth, float maxHeight)
        {
            int scaleX = Mathf.FloorToInt(maxWidth / GbWidth);
            int scaleY = Mathf.FloorToInt(maxHeight / GbHeight);
            int scale = Mathf.Min(scaleX, scaleY);
            return Mathf.Max(1, scale);
        }
        */

        private void FlipRowsForUnity(Color32[] source, Color32[] dest)
        {
            for (int y = 0; y < GbHeight; y++)
            {
                int srcRow = y * GbWidth;
                int dstRow = (GbHeight - 1 - y) * GbWidth;
                for (int x = 0; x < GbWidth; x++)
                    dest[dstRow + x] = source[srcRow + x];
            }
        }

        private void ClearTexture()
        {
            for (int i = 0; i < _blitBuffer.Length; i++)
                _blitBuffer[i] = Color.black;

            _screenTexture.SetPixels32(_blitBuffer);
            _screenTexture.Apply(false, false);
        }

        private void CreateMissingRomText()
        {
            var go = new GameObject("MissingRomText", typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_rootRect, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
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