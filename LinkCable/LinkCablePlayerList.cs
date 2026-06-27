using BombRushMP.Plugin;
using Reptile;
using BRCCodeDmg.Patches;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BRCCodeDmg
{
    internal sealed class LinkCablePlayerList : MonoBehaviour
    {
        private static LinkCablePlayerList _instance;
        private static TMP_FontAsset _gameFont;

        // Layout constants
        private const float PanelW      = 686f;
        private const float RowH        = 56f;
        private const float RowSpacing  = 6f;
        private const float TitleAreaH  = 88f;
        private const float HintH       = 44f;
        private const float PaddingV    = 18f;
        private const int   MaxVisible  = 7;

        private GameObject _panelGo;
        private TextMeshProUGUI _titleLine1;
        private TextMeshProUGUI _titleLine2;
        private TextMeshProUGUI _hintText;
        private RectTransform _listViewport;
        private RectTransform _listContent;
        private Action<ushort, string> _onPick;
        private bool _fontApplied;

        private List<KeyValuePair<ushort, string>> _players = new List<KeyValuePair<ushort, string>>();
        private List<GameObject> _rows = new List<GameObject>();
        private int _selectedIndex;
        private int _windowStart;

        // D-pad DAS
        private float _repeatTimer;
        private const float RepeatDelay = 0.34f;
        private const float RepeatRate  = 0.075f;
        private bool _upHeld, _downHeld;

        // Skip a frame of input after opening so menu doesn't advance immediately
        private bool _inputGrace;
        private bool _cancelWasDown;
        private bool _confirmWasDown;

        public static bool IsVisible => _instance != null && _instance.gameObject.activeSelf;

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

        public static void Show(Action<ushort, string> onPick)
        {
            EnsureInstance();
            _instance._onPick = onPick;
            _instance._inputGrace = true;
            _instance._cancelWasDown = true;
            _instance._confirmWasDown = true;
            _instance.gameObject.SetActive(true);
            _instance._fontApplied = false;
            _instance.Refresh();
        }

        public static void Hide()
        {
            if (_instance == null) return;
            _instance.Close();
        }

        private void Close()
        {
            gameObject.SetActive(false);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        private static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("CodeDmgLinkCableList");
            go.transform.SetParent(Core.Instance.UIManager.transform, false);
            _instance = go.AddComponent<LinkCablePlayerList>();
            _instance.Build();
        }

        // ── Build ─────────────────────────────────────────────────────────────
        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            gameObject.AddComponent<GraphicRaycaster>();

            // Panel
            _panelGo = new GameObject("Panel");
            _panelGo.transform.SetParent(transform, false);
            var panelImg = _panelGo.AddComponent<Image>();
            panelImg.color = new Color(0.08f, 0.08f, 0.08f, 0.97f);
            var panelRt = panelImg.rectTransform;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot     = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;
            panelRt.sizeDelta = new Vector2(PanelW, 400f);

            // Blue accent bar
            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(_panelGo.transform, false);
            var accentImg = accentGo.AddComponent<Image>();
            accentImg.color = new Color(0x32 / 255f, 0x59 / 255f, 0xa6 / 255f, 1f);
            var accentRt = accentImg.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot     = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = Vector2.zero;
            accentRt.sizeDelta = new Vector2(0f, 5f);

            // Title bar
            var t1Go = new GameObject("TitleLine1");
            t1Go.transform.SetParent(_panelGo.transform, false);
            _titleLine1 = t1Go.AddComponent<TextMeshProUGUI>();
            _titleLine1.text = "GB-EMU MENU";
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
            t2Go.transform.SetParent(_panelGo.transform, false);
            _titleLine2 = t2Go.AddComponent<TextMeshProUGUI>();
            _titleLine2.text = "LINK WITH ANOTHER PLAYER";
            _titleLine2.fontSize = 22f;
            _titleLine2.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            _titleLine2.alignment = TextAlignmentOptions.Center;
            var t2Rt = _titleLine2.rectTransform;
            t2Rt.anchorMin = new Vector2(0f, 1f);
            t2Rt.anchorMax = new Vector2(1f, 1f);
            t2Rt.pivot     = new Vector2(0.5f, 1f);
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
            sepRt.pivot     = new Vector2(0.5f, 1f);
            sepRt.anchoredPosition = new Vector2(0f, -(PaddingV + TitleAreaH - 6f));
            sepRt.sizeDelta = new Vector2(-24f, 1f);

            // Viewport
            var vpGo = new GameObject("Viewport");
            vpGo.transform.SetParent(_panelGo.transform, false);
            vpGo.AddComponent<RectMask2D>();
            _listViewport = vpGo.GetComponent<RectTransform>();
            _listViewport.anchorMin = new Vector2(0f, 1f);
            _listViewport.anchorMax = new Vector2(1f, 1f);
            _listViewport.pivot     = new Vector2(0.5f, 1f);
            _listViewport.anchoredPosition = new Vector2(0f, -(PaddingV + TitleAreaH + 4f));
            _listViewport.sizeDelta = new Vector2(-24f, MaxVisible * (RowH + RowSpacing));

            // Scroll
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(vpGo.transform, false);
            _listContent = contentGo.AddComponent<RectTransform>();
            _listContent.anchorMin = new Vector2(0f, 1f);
            _listContent.anchorMax = new Vector2(1f, 1f);
            _listContent.pivot     = new Vector2(0.5f, 1f);
            _listContent.anchoredPosition = Vector2.zero;
            _listContent.sizeDelta = Vector2.zero;

            // Hint texts
            var hintGo = new GameObject("Hint");
            hintGo.transform.SetParent(_panelGo.transform, false);
            _hintText = hintGo.AddComponent<TextMeshProUGUI>();
            _hintText.text = "Up/Down: Nav      A/Enter: Link      B/Left: Quit";
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

        // ── Refresh ───────────────────────────────────────────────────────────
        private void Refresh()
        {
            foreach (var row in _rows)
                if (row != null) Destroy(row);
            _rows.Clear();

            _players = GetAvailablePlayers();
            _selectedIndex = 0;
            _windowStart = 0;
            _repeatTimer = 0f;
            _upHeld = _downHeld = false;

            int visRows = Mathf.Min(_players.Count == 0 ? 1 : _players.Count, MaxVisible);
            float vpH = visRows * (RowH + RowSpacing);

            if (_players.Count == 0)
            {
                var emptyGo = new GameObject("Empty");
                emptyGo.transform.SetParent(_listContent, false);
                var t = emptyGo.AddComponent<TextMeshProUGUI>();
                t.text = "No players nearby.";
                t.fontSize = 24f;
                t.color = new Color(0.55f, 0.55f, 0.55f, 1f);
                t.alignment = TextAlignmentOptions.Center;
                var ert = t.rectTransform;
                ert.anchorMin = new Vector2(0f, 1f);
                ert.anchorMax = new Vector2(1f, 1f);
                ert.pivot     = new Vector2(0.5f, 1f);
                ert.anchoredPosition = new Vector2(0f, -12f);
                ert.sizeDelta = new Vector2(0f, RowH);
                _rows.Add(emptyGo);
                _listContent.sizeDelta = new Vector2(0f, RowH + 24f);
            }
            else
            {
                float rowStep = RowH + RowSpacing;
                for (int i = 0; i < visRows; i++)
                    _rows.Add(BuildRow(i, i * rowStep));
                _listContent.sizeDelta = new Vector2(0f, vpH);
                UpdateVisibleRows();
            }

            _listContent.anchoredPosition = Vector2.zero;
            _listViewport.sizeDelta = new Vector2(-24f, vpH);

            float panelH = PaddingV + TitleAreaH + 4f + vpH + 14f + HintH + PaddingV;
            _panelGo.GetComponent<RectTransform>().sizeDelta = new Vector2(PanelW, panelH);

            ScrollToSelected();
            UpdateHighlights();
        }

        private GameObject BuildRow(int slot, float yOffset)
        {
            var rowGo = new GameObject("Row_" + slot);
            rowGo.transform.SetParent(_listContent, false);

            var bg = rowGo.AddComponent<Image>();
            bg.color = Color.clear;

            var rt = bg.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yOffset);
            rt.sizeDelta = new Vector2(0f, RowH);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = string.Empty;
            label.fontSize = 21f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(18f, 0f);
            lrt.offsetMax = new Vector2(-8f, 0f);

            return rowGo;
        }

        private void UpdateVisibleRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null) continue;
                int index = _windowStart + i;
                bool active = index >= 0 && index < _players.Count;
                row.SetActive(active);
                if (!active) continue;

                var lbl = row.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
                if (lbl != null) lbl.text = _players[index].Value;
            }
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

            HandleNavigation();
            HandleConfirmCancel();
        }

        private void HandleNavigation()
        {
            if (_players.Count == 0) return;

            CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;
            bool upDown   = Input.GetKey(cfg.Up.Value)
                         || Input.GetKey(KeyCode.UpArrow)
                         || Input.GetAxisRaw("Vertical") > 0.5f;
            bool downDown = Input.GetKey(cfg.Down.Value)
                         || Input.GetKey(KeyCode.DownArrow)
                         || Input.GetAxisRaw("Vertical") < -0.5f;

            if (PhoneMenuInputRouter.ConsumedUpThisFrame() || PhoneMenuInputRouter.ConsumedDownThisFrame())
            {
                _upHeld = upDown;
                _downHeld = downDown;
                _repeatTimer = 0f;
                return;
            }

            if (upDown && !_upHeld)
            {
                _upHeld = true; _repeatTimer = 0f; Move(-1);
            }
            else if (!upDown) _upHeld = false;

            if (downDown && !_downHeld)
            {
                _downHeld = true; _repeatTimer = 0f; Move(1);
            }
            else if (!downDown) _downHeld = false;

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
        }

        private void Move(int dir)
        {
            if (_players.Count == 0) return;
            int prev = _selectedIndex;
            _selectedIndex = (_selectedIndex + dir + _players.Count) % _players.Count;
            bool wrapped = (dir < 0 && _selectedIndex > prev) || (dir > 0 && _selectedIndex < prev);
            ScrollToSelected(wrapped);
            UpdateHighlights();
            PlaySelectSfx();
        }

        private void HandleConfirmCancel()
        {
            CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;

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

        private void ConfirmCurrent()
        {
            if (_players.Count == 0) return;
            var chosen = _players[_selectedIndex];
            PlayConfirmSfx();
            PopupState.SuppressPhoneNavFor();
            Close();
            _onPick?.Invoke(chosen.Key, chosen.Value);
        }

        private void BackOut()
        {
            PlayBackSfx();
            PopupState.SuppressPhoneNavFor();
            Close();
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

        // ── Current Selection ─────────────────────────────────────────────────
        private void UpdateHighlights()
        {
            if (_players.Count == 0) return;
            UpdateVisibleRows();
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] == null) continue;
                int index = _windowStart + i;
                bool sel = index == _selectedIndex;

                var bg  = _rows[i].GetComponent<Image>();
                var lbl = _rows[i].transform.Find("Label")?.GetComponent<TextMeshProUGUI>();

                if (bg  != null) bg.color  = sel ? new Color(0x38 / 255f, 0x4d / 255f, 0x9e / 255f, 0.35f) : Color.clear;
                if (lbl != null) lbl.color = sel ? Color.white : new Color(0.65f, 0.65f, 0.65f, 1f);
            }
        }

        private void ScrollToSelected(bool wrapped = false)
        {
            if (_players.Count == 0) return;
            int visibleRows = Mathf.Min(_players.Count, MaxVisible);

            if (_players.Count <= visibleRows)
                _windowStart = 0;
            else if (_selectedIndex < _windowStart || (wrapped && _selectedIndex == 0))
                _windowStart = _selectedIndex;
            else if (_selectedIndex >= _windowStart + visibleRows || wrapped)
                _windowStart = _selectedIndex - visibleRows + 1;

            _windowStart = Mathf.Clamp(_windowStart, 0, Mathf.Max(0, _players.Count - visibleRows));
            _listContent.anchoredPosition = Vector2.zero;
            UpdateVisibleRows();
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

        // ── Link Cable Players ────────────────────────────────────────────────
        private static List<KeyValuePair<ushort, string>> GetAvailablePlayers()
        {
            var result = new List<KeyValuePair<ushort, string>>();
            try
            {
                var cc = ClientController.Instance;
                if (cc == null || cc.Players == null) return result;
                ushort localId = cc.LocalID;
                foreach (var kvp in cc.Players)
                {
                    if (kvp.Key == localId) continue;
                    var playerEntry = kvp.Value;
                    if (playerEntry == null) continue;

                    // Use MPUtility directly — same API SyncVideo uses successfully
                    string rawName = string.Empty;
                    try { rawName = MPUtility.GetPlayerDisplayName(playerEntry.ClientState); }
                    catch { rawName = string.Empty; }

                    string name = LinkCableManager.SanitizeName(rawName);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    result.Add(new KeyValuePair<ushort, string>(kvp.Key, name));
                }
            }
            catch { }
            return result;
        }
    }
}
