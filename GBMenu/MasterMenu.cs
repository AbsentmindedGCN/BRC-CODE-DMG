using Reptile;
using BRCCodeDmg.Patches;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BRCCodeDmg
{
    internal sealed class MasterMenu : MonoBehaviour
    {
        private static MasterMenu _instance;
        private static TMP_FontAsset _gameFont;

        private const float PanelW   = 686f;
        private const float RowH     = 56f;
        private const float RowGap   = 6f;
        private const float TitleAreaH = 88f;
        private const float HintH    = 44f;
        private const float PaddingV = 18f;

        private static readonly string[] OptionLabels = { "Change Game", "Link Cable", "Volume", "GB Palette", "Reboot Game" };
        private static readonly int[] OptionActions = { 0, 1, 2, 3, 4 };

        private GameObject _panelGo;
        private RectTransform _panelRect;
        private GameObject[] _rows;
        private TextMeshProUGUI _hintText;
        private int[] _visibleActions;
        private int _visibleCount;
        private float _listTop;
        private int _selected;
        private bool _fontApplied;
        private bool _inputGrace;
        private bool _cancelWasDown;
        private bool _confirmWasDown;

        private Action _onChangeGame;
        private Action _onLinkCable;
        private Action _onVolume;
        private Action _onGBPalette;
        private Action _onReboot;

        private bool _upHeld;
        private bool _downHeld;
        private float _repeatTimer;
        private const float RepeatDelay = 0.34f;
        private const float RepeatRate = 0.075f;

        public static bool IsVisible => _instance != null && _instance.gameObject.activeSelf;

        public static void Show(Action onChangeGame, Action onLinkCable, Action onVolume, Action onGBPalette, Action onReboot)
        {
            EnsureInstance();
            _instance._onChangeGame = onChangeGame;
            _instance._onLinkCable  = onLinkCable;
            _instance._onVolume     = onVolume;
            _instance._onGBPalette  = onGBPalette;
            _instance._onReboot     = onReboot;
            _instance.RefreshOptions();
            _instance._selected     = 0;
            _instance._inputGrace   = true;
            _instance._cancelWasDown = true;
            _instance._confirmWasDown = true;
            _instance._upHeld       = false;
            _instance._downHeld     = false;
            _instance._repeatTimer  = 0f;
            _instance._fontApplied  = false;
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
            _instance.MoveSelection(-1);
            return true;
        }

        public static bool TryHandlePhoneDown()
        {
            if (!IsVisible) return false;
            _instance.MoveSelection(1);
            return true;
        }

        public static bool TryHandlePhoneRight()
        {
            if (!IsVisible) return false;
            _instance.SelectCurrent();
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
            var go = new GameObject("CodeDmgMasterMenu");
            go.transform.SetParent(Core.Instance.UIManager.transform, false);
            _instance = go.AddComponent<MasterMenu>();
            _instance.Build();
        }

        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 502;
            gameObject.AddComponent<GraphicRaycaster>();

            _panelGo = new GameObject("Panel");
            _panelGo.transform.SetParent(transform, false);
            var panelImg = _panelGo.AddComponent<Image>();
            panelImg.color = new Color(0.08f, 0.08f, 0.08f, 0.97f);
            var panelRt = panelImg.rectTransform;
            _panelRect = panelRt;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot     = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;

            // Blue accent bar
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

            // Title
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

            // Subtitle
            var t2Go = new GameObject("Title2");
            t2Go.transform.SetParent(_panelGo.transform, false);
            var t2 = t2Go.AddComponent<TextMeshProUGUI>();
            t2.text = "SELECT OPTION";
            t2.fontSize = 22f;
            t2.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            t2.alignment = TextAlignmentOptions.Center;
            var t2Rt = t2.rectTransform;
            t2Rt.anchorMin = new Vector2(0f, 1f);
            t2Rt.anchorMax = new Vector2(1f, 1f);
            t2Rt.pivot = new Vector2(0.5f, 1f);
            t2Rt.anchoredPosition = new Vector2(0f, -(PaddingV + 42f));
            t2Rt.sizeDelta = new Vector2(0f, 30f);

            // Separator
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

            // Option rows
            _listTop = PaddingV + TitleAreaH + 4f;
            _rows = new GameObject[OptionLabels.Length];
            for (int i = 0; i < OptionLabels.Length; i++)
                _rows[i] = BuildRow(OptionLabels[i], i, _listTop + i * (RowH + RowGap));

            // Hint text
            float panelH = _listTop + OptionLabels.Length * (RowH + RowGap) + 14f + HintH + PaddingV;
            panelRt.sizeDelta = new Vector2(PanelW, panelH);

            var hintGo = new GameObject("Hint");
            hintGo.transform.SetParent(_panelGo.transform, false);
            _hintText = hintGo.AddComponent<TextMeshProUGUI>();
            _hintText.text = "Up/Down: Nav      A/Right: Select      B/Left: Exit";
            _hintText.fontSize = 19f;
            _hintText.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            _hintText.alignment = TextAlignmentOptions.Center;
            var hintRt = _hintText.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = new Vector2(0f, PaddingV);
            hintRt.sizeDelta = new Vector2(0f, HintH);

            gameObject.SetActive(false);
        }

        private void RefreshOptions()
        {
            bool linkEnabled = CodeDmgPlugin.ConfigSettings?.LinkCableEnabled.Value == true
                && CodeDmgPlugin.LinkCable != null
                && CodeDmgPlugin.IsBombRushMPReady();
            if (_visibleActions == null || _visibleActions.Length != OptionActions.Length)
                _visibleActions = new int[OptionActions.Length];

            _visibleCount = 0;
            for (int i = 0; i < OptionLabels.Length; i++)
            {
                if (OptionActions[i] == 1 && !linkEnabled)
                {
                    if (_rows != null && i < _rows.Length && _rows[i] != null)
                        _rows[i].SetActive(false);
                    continue;
                }

                _visibleActions[_visibleCount] = OptionActions[i];
                if (_rows != null && i < _rows.Length && _rows[i] != null)
                {
                    _rows[i].SetActive(true);
                    var rt = _rows[i].GetComponent<RectTransform>();
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(0f, -(_listTop + _visibleCount * (RowH + RowGap)));

                    var lbl = _rows[i].transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
                    if (lbl != null) lbl.text = GetOptionLabel(i);
                }
                _visibleCount++;
            }

            if (_panelRect != null)
            {
                float panelH = _listTop + _visibleCount * (RowH + RowGap) + 14f + HintH + PaddingV;
                _panelRect.sizeDelta = new Vector2(PanelW, panelH);
            }

            if (_visibleCount > 0 && _selected >= _visibleCount)
                _selected = _visibleCount - 1;
        }

        private static string GetOptionLabel(int index)
        {
            if (index == 1 && CodeDmgPlugin.LinkCable != null && CodeDmgPlugin.LinkCable.State == LinkCableState.Connected)
                return "Disconnect Link Cable";
            return OptionLabels[index];
        }

        private GameObject BuildRow(string label, int index, float yOffset)
        {
            var rowGo = new GameObject("Row_" + index);
            rowGo.transform.SetParent(_panelGo.transform, false);
            var bg = rowGo.AddComponent<Image>();
            bg.color = Color.clear;
            var rt = bg.rectTransform;
            rt.anchorMin = new Vector2(0.03f, 1f);
            rt.anchorMax = new Vector2(0.97f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yOffset);
            rt.sizeDelta = new Vector2(0f, RowH);

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
            }
            else
            {
                if (upNow && !_upHeld)
                { _upHeld = true; _repeatTimer = 0f; MoveSelection(-1); }
                else if (!upNow) _upHeld = false;

                if (downNow && !_downHeld)
                { _downHeld = true; _repeatTimer = 0f; MoveSelection(1); }
                else if (!downNow) _downHeld = false;

                if (_upHeld || _downHeld)
                {
                    _repeatTimer += Time.unscaledDeltaTime;
                    if (_repeatTimer >= RepeatDelay)
                    {
                        _repeatTimer -= RepeatRate;
                        if (_upHeld) MoveSelection(-1);
                        if (_downHeld) MoveSelection(1);
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
                SelectCurrent();
                return;
            }

            if (cancel) BackOut();
        }


        private void MoveSelection(int delta)
        {
            if (_visibleCount <= 0) return;
            _selected = (_selected + delta + _visibleCount) % _visibleCount;
            UpdateHighlights();
            PlaySelectSfx();
        }

        private void SelectCurrent()
        {
            PlayConfirmSfx();
            PopupState.SuppressPhoneNavFor();
            Hide();
            int action = (_visibleActions != null && _selected >= 0 && _selected < _visibleCount) ? _visibleActions[_selected] : -1;
            switch (action)
            {
                case 0: _onChangeGame?.Invoke(); break;
                case 1: _onLinkCable?.Invoke();  break;
                case 2: _onVolume?.Invoke();     break;
                case 3: _onGBPalette?.Invoke();  break;
                case 4: _onReboot?.Invoke();     break;
            }
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

        private void UpdateHighlights()
        {
            int visibleIndex = 0;
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i] == null) continue;
                if (!_rows[i].activeSelf)
                {
                    var hiddenBg = _rows[i].GetComponent<Image>();
                    if (hiddenBg != null) hiddenBg.color = Color.clear;
                    continue;
                }

                bool sel = visibleIndex == _selected;
                var bg  = _rows[i].GetComponent<Image>();
                var lbl = _rows[i].transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
                if (bg  != null) bg.color  = sel ? new Color(0x38 / 255f, 0x4d / 255f, 0x9e / 255f, 0.35f) : Color.clear;
                if (lbl != null) lbl.color = sel ? Color.white : new Color(0.65f, 0.65f, 0.65f, 1f);
                visibleIndex++;
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
