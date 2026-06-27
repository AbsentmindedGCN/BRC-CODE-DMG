using Reptile;
using BRCCodeDmg.Patches;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BRCCodeDmg
{
    internal sealed class LinkCableConnectDialog : MonoBehaviour
    {
        private static LinkCableConnectDialog _instance;
        private static TMP_FontAsset _gameFont;

        private const float PanelW   = 686f;
        private const float PaddingV = 18f;
        private const float TitleAreaH = 88f;
        private const float HintH    = 44f;

        private TextMeshProUGUI _titleLine1;
        private TextMeshProUGUI _titleLine2; // Playername
        private TextMeshProUGUI _hintText;
        private GameObject _yesRow;
        private GameObject _noRow;
        private bool _fontApplied;
        private Action _onYes;
        private Action _onNo;
        private int _selected;
        private bool _inputGrace;
        private bool _cancelWasDown;
        private bool _confirmWasDown;
        private bool _upHeld;
        private bool _downHeld;
        private float _repeatTimer;
        private const float RepeatDelay = 0.34f;
        private const float RepeatRate = 0.075f;

        public static bool IsVisible => _instance != null && _instance.gameObject.activeSelf;

        public static void Show(string hostName, Action onYes, Action onNo)
        {
            EnsureInstance();
            _instance._onYes = onYes;
            _instance._onNo  = onNo;
            _instance._selected = 0;
            _instance._inputGrace = true;
            _instance._cancelWasDown  = true;
            _instance._confirmWasDown = true;
            _instance._upHeld         = false;
            _instance._downHeld       = false;
            _instance._repeatTimer    = 0f;
            _instance._fontApplied = false;
            _instance._titleLine2.text = "Connect to " + CodeDmgRenderer.TruncatePlayerName(hostName) + "?";
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
            _instance.MoveSelection();
            return true;
        }

        public static bool TryHandlePhoneDown()
        {
            if (!IsVisible) return false;
            _instance.MoveSelection();
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
            var go = new GameObject("CodeDmgLinkCableDialog");
            go.transform.SetParent(Core.Instance.UIManager.transform, false);
            _instance = go.AddComponent<LinkCableConnectDialog>();
            _instance.Build();
        }

        // ── Build ─────────────────────────────────────────────────────────────
        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 501;
            gameObject.AddComponent<GraphicRaycaster>();

            // Panel
            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(transform, false);
            var panelImg = panelGo.AddComponent<Image>();
            panelImg.color = new Color(0.08f, 0.08f, 0.08f, 0.97f);
            var panelRt = panelImg.rectTransform;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot     = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;

            // Blue accent bar
            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(panelGo.transform, false);
            var accentImg = accentGo.AddComponent<Image>();
            accentImg.color = new Color(0x32 / 255f, 0x59 / 255f, 0xa6 / 255f, 1f);
            var accentRt = accentImg.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot     = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = Vector2.zero;
            accentRt.sizeDelta = new Vector2(0f, 5f);

            // Title
            var t1Go = new GameObject("TitleLine1");
            t1Go.transform.SetParent(panelGo.transform, false);
            _titleLine1 = t1Go.AddComponent<TextMeshProUGUI>();
            _titleLine1.text = "GB-EMU LINK";
            _titleLine1.fontSize = 30f;
            _titleLine1.fontStyle = FontStyles.Bold;
            _titleLine1.color = new Color(0x5b / 255f, 0x8f / 255f, 0xdf / 255f, 1f);
            _titleLine1.alignment = TextAlignmentOptions.Center;
            var t1Rt = _titleLine1.rectTransform;
            t1Rt.anchorMin = new Vector2(0f, 1f);
            t1Rt.anchorMax = new Vector2(1f, 1f);
            t1Rt.pivot     = new Vector2(0.5f, 1f);
            t1Rt.anchoredPosition = new Vector2(0f, -PaddingV);
            t1Rt.sizeDelta = new Vector2(0f, 40f);

            // Subtitle
            var t2Go = new GameObject("TitleLine2");
            t2Go.transform.SetParent(panelGo.transform, false);
            _titleLine2 = t2Go.AddComponent<TextMeshProUGUI>();
            _titleLine2.text = "Connect to ...?";
            _titleLine2.fontSize = 22f;
            _titleLine2.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            _titleLine2.alignment = TextAlignmentOptions.Center;
            _titleLine2.enableWordWrapping = false;
            var t2Rt = _titleLine2.rectTransform;
            t2Rt.anchorMin = new Vector2(0f, 1f);
            t2Rt.anchorMax = new Vector2(1f, 1f);
            t2Rt.pivot     = new Vector2(0.5f, 1f);
            t2Rt.anchoredPosition = new Vector2(0f, -(PaddingV + 42f));
            t2Rt.sizeDelta = new Vector2(0f, 30f);

            // Separator
            var sepGo = new GameObject("Sep");
            sepGo.transform.SetParent(panelGo.transform, false);
            var sepImg = sepGo.AddComponent<Image>();
            sepImg.color = new Color(1f, 1f, 1f, 0.12f);
            var sepRt = sepImg.rectTransform;
            sepRt.anchorMin = new Vector2(0f, 1f);
            sepRt.anchorMax = new Vector2(1f, 1f);
            sepRt.pivot     = new Vector2(0.5f, 1f);
            sepRt.anchoredPosition = new Vector2(0f, -(PaddingV + TitleAreaH - 6f));
            sepRt.sizeDelta = new Vector2(-24f, 1f);

            // Option Select
            float rowH    = 56f;
            float rowGap  = 6f;
            float listTop = PaddingV + TitleAreaH + 4f;
            float listH   = rowH * 2 + rowGap;

            _yesRow = BuildOptionRow(panelGo.transform, "Yes!", 0f, rowH, listTop);
            _noRow  = BuildOptionRow(panelGo.transform, "Nope!", rowH + rowGap, rowH, listTop);

            // Hint texts
            float panelH = listTop + listH + 14f + HintH + PaddingV;
            panelRt.sizeDelta = new Vector2(PanelW, panelH);

            var hintGo = new GameObject("Hint");
            hintGo.transform.SetParent(panelGo.transform, false);
            _hintText = hintGo.AddComponent<TextMeshProUGUI>();
            _hintText.text = "Up/Down: Nav      A/Right: Link      B/Left: Exit";
            _hintText.fontSize = 19f;
            _hintText.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            _hintText.alignment = TextAlignmentOptions.Center;
            var hintRt = _hintText.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot     = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = new Vector2(0f, PaddingV);
            hintRt.sizeDelta = new Vector2(0f, HintH);

            gameObject.SetActive(false);
        }

        private GameObject BuildOptionRow(Transform parent, string label, float yOffset, float rowH, float listTop)
        {
            var rowGo = new GameObject("Row_" + label);
            rowGo.transform.SetParent(parent, false);
            var bg = rowGo.AddComponent<Image>();
            bg.color = Color.clear;
            var rt = bg.rectTransform;
            rt.anchorMin = new Vector2(0.03f, 1f);
            rt.anchorMax = new Vector2(0.97f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -(listTop + yOffset));
            rt.sizeDelta = new Vector2(0f, rowH);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var t = labelGo.AddComponent<TextMeshProUGUI>();
            t.text = label;
            t.fontSize = 24f;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.Center;
            var lrt = t.rectTransform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;

            return rowGo;
        }

        // ── Update ────────────────────────────────────────────────────────────
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

            // Nav Up/Down to toggle
            bool upNow    = Input.GetKey(cfg.Up.Value)   || Input.GetKey(KeyCode.UpArrow)   || Input.GetAxisRaw("Vertical") > 0.5f;
            bool downNow  = Input.GetKey(cfg.Down.Value)  || Input.GetKey(KeyCode.DownArrow) || Input.GetAxisRaw("Vertical") < -0.5f;

            if (PhoneMenuInputRouter.ConsumedUpThisFrame() || PhoneMenuInputRouter.ConsumedDownThisFrame())
            {
                _upHeld = upNow;
                _downHeld = downNow;
                _repeatTimer = 0f;
            }
            else
            {
                if (upNow && !_upHeld)
                { _upHeld = true; _repeatTimer = 0f; MoveSelection(); }
                else if (!upNow) _upHeld = false;

                if (downNow && !_downHeld)
                { _downHeld = true; _repeatTimer = 0f; MoveSelection(); }
                else if (!downNow) _downHeld = false;

                if (_upHeld || _downHeld)
                {
                    _repeatTimer += Time.unscaledDeltaTime;
                    if (_repeatTimer >= RepeatDelay)
                    {
                        _repeatTimer -= RepeatRate;
                        MoveSelection();
                    }
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

        private void MoveSelection()
        {
            _selected = _selected == 0 ? 1 : 0;
            UpdateHighlights();
            PlaySelectSfx();
        }

        private void ConfirmCurrent()
        {
            PlayConfirmSfx();
            PopupState.SuppressPhoneNavFor();
            if (_selected == 0) { Hide(); _onYes?.Invoke(); }
            else                { Hide(); _onNo?.Invoke();  }
        }

        private void BackOut()
        {
            PlayBackSfx();
            PopupState.SuppressPhoneNavFor();
            Hide();
            _onNo?.Invoke();
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

        private void UpdateHighlights()
        {
            SetRowHighlight(_yesRow,  _selected == 0);
            SetRowHighlight(_noRow,   _selected == 1);
        }

        private static void SetRowHighlight(GameObject row, bool selected)
        {
            if (row == null) return;
            var bg  = row.GetComponent<Image>();
            var lbl = row.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
            if (bg  != null) bg.color  = selected ? new Color(0x38 / 255f, 0x4d / 255f, 0x9e / 255f, 0.35f) : Color.clear;
            if (lbl != null) lbl.color = selected ? Color.white : new Color(0.65f, 0.65f, 0.65f, 1f);
        }

        // ── Font ──────────────────────────────────────────────────────────────
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
