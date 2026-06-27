using Reptile;
using BRCCodeDmg.Patches;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BRCCodeDmg
{
    internal sealed class GBPaletteMenu : MonoBehaviour
    {
        private static GBPaletteMenu _instance;
        private static TMP_FontAsset _gameFont;

        private const float PanelW     = 686f;
        private const float RowH       = 56f;
        private const float RowSpacing = 6f;
        private const float TitleAreaH = 88f;
        private const float HintH      = 44f;
        private const float PaddingV   = 18f;
        private const int   MaxVisible = 7;

        private static readonly string[] Labels =
            { "DMG", "Cyber", "Emu", "Autumn", "Paris", "Grayscale", "Early", "Crow", "Coffee", "Winter", "Bomb Rush Orange", "Bomb Rush Blue" };

        private GameObject _panelGo;
        private RectTransform _listViewport;
        private RectTransform _listContent;
        private System.Collections.Generic.List<GameObject> _rows =
            new System.Collections.Generic.List<GameObject>();
        private int _selectedIndex;
        private int _windowStart;
        private bool _fontApplied;
        private bool _inputGrace;
        private bool _upHeld, _downHeld;
        private bool _cancelWasDown;
        private bool _confirmWasDown;
        private float _repeatTimer;
        private const float RepeatDelay = 0.34f;
        private const float RepeatRate  = 0.075f;

        public static bool IsVisible => _instance != null && _instance.gameObject.activeSelf;

        public static void Show()
        {
            EnsureInstance();
            string cur = Helper.paletteName;
            _instance._selectedIndex = 0;
            for (int i = 0; i < Helper.PaletteNames.Length; i++)
                if (Helper.PaletteNames[i] == cur) { _instance._selectedIndex = i; break; }
            _instance._windowStart  = 0;
            _instance._inputGrace    = true;
            _instance._cancelWasDown = true;
            _instance._confirmWasDown = true;
            _instance._upHeld        = false;
            _instance._downHeld     = false;
            _instance._repeatTimer  = 0f;
            _instance._fontApplied  = false;
            _instance.BuildRows();
            _instance.ScrollToSelected();
            _instance.UpdateHighlights();
            _instance.gameObject.SetActive(true);
        }

        public static void Hide()
        {
            if (_instance == null) return;
            _instance.gameObject.SetActive(false);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        public static bool TryHandlePhoneUp()
        {
            if (!IsVisible) return false;
            _instance.Move(-1);
            return true;
        }

        public static bool TryHandlePhoneDown()
        {
            if (!IsVisible) return false;
            _instance.Move(1);
            return true;
        }

        public static bool TryHandlePhoneRight()
        {
            if (!IsVisible) return false;
            _instance.ConfirmCurrent();
            return true;
        }

        public static bool TryHandlePhoneBack()
        {
            if (!IsVisible) return false;
            _instance.BackOut();
            return true;
        }

        private static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("CodeDmgGBPaletteMenu");
            go.transform.SetParent(Core.Instance.UIManager.transform, false);
            _instance = go.AddComponent<GBPaletteMenu>();
            _instance.Build();
        }

        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 504;
            gameObject.AddComponent<GraphicRaycaster>();

            _panelGo = new GameObject("Panel");
            _panelGo.transform.SetParent(transform, false);
            var panelImg = _panelGo.AddComponent<Image>();
            panelImg.color = new Color(0.08f, 0.08f, 0.08f, 0.97f);
            var panelRt = panelImg.rectTransform;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot     = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(_panelGo.transform, false);
            var accentImg = accentGo.AddComponent<Image>();
            accentImg.color = new Color(0x32 / 255f, 0x59 / 255f, 0xa6 / 255f, 1f);
            var accentRt = accentImg.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = Vector2.zero;
            accentRt.sizeDelta = new Vector2(0f, 5f);

            var t1Go = new GameObject("Title1");
            t1Go.transform.SetParent(_panelGo.transform, false);
            var t1 = t1Go.AddComponent<TextMeshProUGUI>();
            t1.text = "GB-EMU MENU";
            t1.fontSize = 30f;
            t1.fontStyle = FontStyles.Bold;
            t1.color = new Color(0x5b / 255f, 0x8f / 255f, 0xdf / 255f, 1f);
            t1.alignment = TextAlignmentOptions.Center;
            var t1Rt = t1.rectTransform;
            t1Rt.anchorMin = new Vector2(0f, 1f);
            t1Rt.anchorMax = new Vector2(1f, 1f);
            t1Rt.pivot = new Vector2(0.5f, 1f);
            t1Rt.anchoredPosition = new Vector2(0f, -PaddingV);
            t1Rt.sizeDelta = new Vector2(0f, 40f);

            var t2Go = new GameObject("Title2");
            t2Go.transform.SetParent(_panelGo.transform, false);
            var t2 = t2Go.AddComponent<TextMeshProUGUI>();
            t2.text = "CHANGE GAMEBOY PALETTE";
            t2.fontSize = 22f;
            t2.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            t2.alignment = TextAlignmentOptions.Center;
            var t2Rt = t2.rectTransform;
            t2Rt.anchorMin = new Vector2(0f, 1f);
            t2Rt.anchorMax = new Vector2(1f, 1f);
            t2Rt.pivot = new Vector2(0.5f, 1f);
            t2Rt.anchoredPosition = new Vector2(0f, -(PaddingV + 42f));
            t2Rt.sizeDelta = new Vector2(0f, 30f);

            var sepGo = new GameObject("Sep");
            sepGo.transform.SetParent(_panelGo.transform, false);
            var sepImg = sepGo.AddComponent<Image>();
            sepImg.color = new Color(1f, 1f, 1f, 0.12f);
            var sepRt = sepImg.rectTransform;
            sepRt.anchorMin = new Vector2(0f, 1f);
            sepRt.anchorMax = new Vector2(1f, 1f);
            sepRt.pivot = new Vector2(0.5f, 1f);
            sepRt.anchoredPosition = new Vector2(0f, -(PaddingV + TitleAreaH - 6f));
            sepRt.sizeDelta = new Vector2(-24f, 1f);

            var vpGo = new GameObject("Viewport");
            vpGo.transform.SetParent(_panelGo.transform, false);
            vpGo.AddComponent<RectMask2D>();
            _listViewport = vpGo.GetComponent<RectTransform>();
            _listViewport.anchorMin = new Vector2(0f, 1f);
            _listViewport.anchorMax = new Vector2(1f, 1f);
            _listViewport.pivot = new Vector2(0.5f, 1f);
            _listViewport.anchoredPosition = new Vector2(0f, -(PaddingV + TitleAreaH + 4f));
            _listViewport.sizeDelta = new Vector2(-24f, MaxVisible * (RowH + RowSpacing));

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(vpGo.transform, false);
            _listContent = contentGo.AddComponent<RectTransform>();
            _listContent.anchorMin = new Vector2(0f, 1f);
            _listContent.anchorMax = new Vector2(1f, 1f);
            _listContent.pivot = new Vector2(0.5f, 1f);
            _listContent.anchoredPosition = Vector2.zero;
            _listContent.sizeDelta = Vector2.zero;

            float vpH = MaxVisible * (RowH + RowSpacing);
            float panelH = PaddingV + TitleAreaH + 4f + vpH + 14f + HintH + PaddingV;
            panelRt.sizeDelta = new Vector2(PanelW, panelH);

            var hintGo = new GameObject("Hint");
            hintGo.transform.SetParent(_panelGo.transform, false);
            var hintText = hintGo.AddComponent<TextMeshProUGUI>();
            hintText.text = "Up/Down: Nav      A/Right: Select      B/Left: Exit";
            hintText.fontSize = 19f;
            hintText.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            hintText.alignment = TextAlignmentOptions.Center;
            var hintRt = hintText.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = new Vector2(0f, PaddingV);
            hintRt.sizeDelta = new Vector2(0f, HintH);

            gameObject.SetActive(false);
        }

        private void BuildRows()
        {
            foreach (var r in _rows) if (r) Destroy(r);
            _rows.Clear();
            int vis = Mathf.Min(Labels.Length, MaxVisible);
            float step = RowH + RowSpacing;
            for (int i = 0; i < vis; i++)
            {
                var rowGo = new GameObject("Row_" + i);
                rowGo.transform.SetParent(_listContent, false);
                var bg = rowGo.AddComponent<Image>();
                bg.color = Color.clear;
                var rt = bg.rectTransform;
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -i * step);
                rt.sizeDelta = new Vector2(0f, RowH);

                var lGo = new GameObject("Label");
                lGo.transform.SetParent(rowGo.transform, false);
                var lbl = lGo.AddComponent<TextMeshProUGUI>();
                lbl.fontSize = 24f;
                lbl.alignment = TextAlignmentOptions.Center;
                var lrt = lbl.rectTransform;
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = lrt.offsetMax = Vector2.zero;
                _rows.Add(rowGo);
            }
        }

        private void Update()
        {
            if (!gameObject.activeSelf) return;
            if (!_fontApplied) ApplyGameFont();
            if (_inputGrace) { _inputGrace = false; return; }

            if (PopupState.ShouldSuppressMenuInputForChat())
            {
                _upHeld = false;
                _downHeld = false;
                _repeatTimer = 0f;
                _confirmWasDown = true;
                _cancelWasDown = true;
                return;
            }

            CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;
            bool upNow   = Input.GetKey(cfg.Up.Value)   || Input.GetKey(KeyCode.UpArrow)   || Input.GetAxisRaw("Vertical") > 0.5f;
            bool downNow = Input.GetKey(cfg.Down.Value)  || Input.GetKey(KeyCode.DownArrow) || Input.GetAxisRaw("Vertical") < -0.5f;

            if (PhoneMenuInputRouter.ConsumedUpThisFrame() || PhoneMenuInputRouter.ConsumedDownThisFrame())
            {
                _upHeld = upNow;
                _downHeld = downNow;
                _repeatTimer = 0f;
                return;
            }

            if (upNow && !_upHeld)   { _upHeld   = true; _repeatTimer = 0f; Move(-1); }
            else if (!upNow)          _upHeld   = false;
            if (downNow && !_downHeld){ _downHeld = true; _repeatTimer = 0f; Move(1);  }
            else if (!downNow)         _downHeld = false;

            if (_upHeld || _downHeld)
            {
                _repeatTimer += Time.unscaledDeltaTime;
                if (_repeatTimer >= RepeatDelay)
                {
                    _repeatTimer -= RepeatRate;
                    if (_upHeld)   Move(-1);
                    if (_downHeld) Move(1);
                }
            }

            bool confirmNow = Input.GetKey(cfg.A.Value)
                          || Input.GetKey(cfg.Right.Value)
                          || Input.GetKey(KeyCode.Return)
                          || Input.GetKey(KeyCode.KeypadEnter)
                          || Input.GetKey(KeyCode.RightArrow)
                          || Input.GetKey(KeyCode.JoystickButton0)
                          || Input.GetAxisRaw("Horizontal") > 0.5f;
            bool confirm = confirmNow && !_confirmWasDown;
            _confirmWasDown = confirmNow;

            bool cancelDown = Input.GetKey(cfg.B.Value)
                          || Input.GetKey(cfg.Left.Value)
                          || Input.GetKey(KeyCode.Escape)
                          || Input.GetKey(KeyCode.LeftArrow)
                          || Input.GetKey(KeyCode.JoystickButton1)
                          || Input.GetAxisRaw("Horizontal") < -0.5f;
            bool cancel = cancelDown && !_cancelWasDown;
            _cancelWasDown = cancelDown;

            if (confirm)
            {
                ConfirmCurrent();
                return;
            }
            if (cancel) BackOut();
        }

        private void Move(int dir)
        {
            _selectedIndex = (_selectedIndex + dir + Labels.Length) % Labels.Length;
            ScrollToSelected();
            UpdateHighlights();
            PlaySelectSfx();
        }

        private void ConfirmCurrent()
        {
            string name = Helper.PaletteNames[_selectedIndex];
            Helper.paletteName = name;
            if (CodeDmgPlugin.ConfigSettings?.Palette != null)
                CodeDmgPlugin.ConfigSettings.Palette.Value = name;
            PlayConfirmSfx();
            PopupState.SuppressPhoneNavFor();
            Hide();
        }

        private void BackOut()
        {
            PlayBackSfx();
            PopupState.SuppressPhoneNavFor();
            Hide();
        }

        private static void PlaySelectSfx()
        {
            try { AppCodeDmg.PlayMenuSelectSFX(); } catch { }
        }

        private static void PlayConfirmSfx()
        {
            try { AppCodeDmg.PlayMenuConfirmSFX(); } catch { }
        }

        private static void PlayBackSfx()
        {
            try { AppCodeDmg.PlayMenuBackSFX(); } catch { }
        }

        private void ScrollToSelected()
        {
            int vis = Mathf.Min(Labels.Length, MaxVisible);
            if (_selectedIndex < _windowStart)
                _windowStart = _selectedIndex;
            else if (_selectedIndex >= _windowStart + vis)
                _windowStart = _selectedIndex - vis + 1;
            _windowStart = Mathf.Clamp(_windowStart, 0, Mathf.Max(0, Labels.Length - vis));
            UpdateVisibleRows();
        }

        private void UpdateVisibleRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] == null) continue;
                int idx = _windowStart + i;
                bool active = idx < Labels.Length;
                _rows[i].SetActive(active);
                if (!active) continue;
                var lbl = _rows[i].transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
                if (lbl != null) lbl.text = Labels[idx];
            }
        }

        private void UpdateHighlights()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] == null) continue;
                bool sel = (_windowStart + i) == _selectedIndex;
                var bg  = _rows[i].GetComponent<Image>();
                var lbl = _rows[i].transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
                if (bg  != null) bg.color  = sel ? new Color(0x38 / 255f, 0x4d / 255f, 0x9e / 255f, 0.35f) : Color.clear;
                if (lbl != null) lbl.color = sel ? Color.white : new Color(0.65f, 0.65f, 0.65f, 1f);
            }
        }

        private void ApplyGameFont()
        {
            if (_gameFont == null)
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (var t in texts)
                    if (t != null && t.font != null && t.name == "HeaderLabel") { _gameFont = t.font; break; }
                if (_gameFont == null)
                    foreach (var t in texts)
                        if (t != null && t.font != null) { _gameFont = t.font; break; }
            }
            if (_gameFont == null) return;
            foreach (var t in GetComponentsInChildren<TMP_Text>(true))
                if (t != null && t.font != _gameFont) t.font = _gameFont;
            _fontApplied = true;
        }
    }
}
