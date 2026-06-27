using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Reptile;
using BRCCodeDmg.Patches;

namespace BRCCodeDmg
{
    internal sealed class RomSelectMenu : MonoBehaviour
    {
        private static RomSelectMenu _instance;
        private static TMP_FontAsset _gameFont;

        private const float PanelW     = 686f;
        private const float RowH       = 56f;
        private const float RowSpacing = 6f;
        private const float TitleAreaH = 88f;
        private const float HintH      = 44f;
        private const float PaddingV   = 18f;
        private const int   MaxVisible = 7;

        private GameObject _panelGo;
        private TextMeshProUGUI _hintText;
        private RectTransform _listViewport;
        private RectTransform _listContent;
        private TextMeshProUGUI _loadingText;

        private List<string> _romPaths  = new List<string>();
        private List<GameObject> _rows  = new List<GameObject>();
        private int _selectedIndex;
        private int _windowStart;
        private bool _fontApplied;
        private bool _inputGrace;
        private bool _cancelWasDown;
        private bool _confirmWasDown;
        private bool _loading;

        // DAS
        private float _repeatTimer;
        private const float RepeatDelay = 0.34f;
        private const float RepeatRate  = 0.075f;
        private bool _upHeld, _downHeld;

        private Action<string> _onPick;

        // Cache ROM list
        private static List<string> _cachedRoms;
        private static string _cachedDir;
        private static int _cachedCount;
        private static long _cachedTick;

        public static bool IsVisible => _instance != null && _instance.gameObject.activeSelf;

        public static void Show(Action<string> onPick)
        {
            EnsureInstance();
            _instance._onPick = onPick;
            _instance._inputGrace = true;
            _instance._cancelWasDown = true;
            _instance._confirmWasDown = true;
            _instance._fontApplied = false;
            _instance._selectedIndex = 0;
            _instance._repeatTimer = 0f;
            _instance._upHeld = _instance._downHeld = false;
            _instance.gameObject.SetActive(true);
            _instance.StartCoroutine(_instance.LoadRoms());
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
            if (!_instance._loading) _instance.Move(-1);
            return true;
        }

        public static bool TryHandlePhoneDown()
        {
            if (!IsVisible) return false;
            if (!_instance._loading) _instance.Move(1);
            return true;
        }

        public static bool TryHandlePhoneRight()
        {
            if (!IsVisible) return false;
            if (!_instance._loading) _instance.ConfirmCurrent();
            return true;
        }

        public static bool TryHandlePhoneBack()
        {
            if (!IsVisible) return false;
            _instance.BackOut();
            return true;
        }

        public static void InvalidateCache() => _cachedRoms = null; // Make cache invalid so next it rescans next time player opens it

        private static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("CodeDmgRomSelect");
            go.transform.SetParent(Core.Instance.UIManager.transform, false);
            _instance = go.AddComponent<RomSelectMenu>();
            _instance.Build();
        }

        // ── Build ─────────────────────────────────────────────────────────────
        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 503;
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
            t2.text = "CHANGE GAME";
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

            // Viewport
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

            // Loading
            var loadGo = new GameObject("Loading");
            loadGo.transform.SetParent(_panelGo.transform, false);
            _loadingText = loadGo.AddComponent<TextMeshProUGUI>();
            _loadingText.text = "Scanning ROMs...";
            _loadingText.fontSize = 22f;
            _loadingText.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            _loadingText.alignment = TextAlignmentOptions.Center;
            var loadRt = _loadingText.rectTransform;
            loadRt.anchorMin = new Vector2(0f, 1f);
            loadRt.anchorMax = new Vector2(1f, 1f);
            loadRt.pivot = new Vector2(0.5f, 1f);
            loadRt.anchoredPosition = new Vector2(0f, -(PaddingV + TitleAreaH + 4f + RowH * 0.5f));
            loadRt.sizeDelta = new Vector2(0f, RowH);
            _loadingText.gameObject.SetActive(false);

            // Hint texts
            float vpH = MaxVisible * (RowH + RowSpacing);
            float panelH = PaddingV + TitleAreaH + 4f + vpH + 14f + HintH + PaddingV;
            panelRt.sizeDelta = new Vector2(PanelW, panelH);

            var hintGo = new GameObject("Hint");
            hintGo.transform.SetParent(_panelGo.transform, false);
            _hintText = hintGo.AddComponent<TextMeshProUGUI>();
            _hintText.text = "Up/Down: Nav      A/Right: Load      B/Left: Exit";
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

        // ── ROM Loading ───────────────────────────────────────────────────────
        private IEnumerator LoadRoms()
        {
            ClearRows();
            _loading = true;
            _loadingText.gameObject.SetActive(true);

            string dir = GetRomDirectory();
            bool needsScan = _cachedRoms == null
                          || _cachedDir != dir
                          || DirectoryChanged(dir);

            if (needsScan)
            {
                yield return null; // Wait a frame so the popup can render the loading text

                var found = new List<string>();
                try
                {
                    if (Directory.Exists(dir))
                    {
                        var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(f =>
                            {
                                string ext = Path.GetExtension(f).ToLowerInvariant();
                                return ext == ".gb" || ext == ".gbc";
                            })
                            .OrderBy(f => Path.GetFileNameWithoutExtension(f),
                                     StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        found.AddRange(files);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CODE-DMG] ROM scan failed: " + ex.Message);
                }

                _cachedRoms  = found;
                _cachedDir   = dir;
                _cachedCount = found.Count;
                _cachedTick  = GetDirectoryTick(dir);
            }

            _romPaths = new List<string>(_cachedRoms ?? new List<string>());

            // Put Tobu Tobu Girl DX in alphabetical order!!
            string tobuPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gb");
            if (File.Exists(tobuPath) && !_romPaths.Contains(tobuPath))
            {
                const string tobuLabel = "Tobu Tobu Girl DX";
                int insertAt = _romPaths.Count;
                for (int i = 0; i < _romPaths.Count; i++)
                {
                    string label = Path.GetFileNameWithoutExtension(_romPaths[i]);
                    if (StringComparer.OrdinalIgnoreCase.Compare(tobuLabel, label) <= 0)
                    { insertAt = i; break; }
                }
                _romPaths.Insert(insertAt, tobuPath);
            }
            _loading = false;
            _loadingText.gameObject.SetActive(false);

            BuildRows();
            ApplyGameFont();
            ScrollToSelected();
            UpdateHighlights();
        }

        private static bool DirectoryChanged(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            long tick = GetDirectoryTick(dir);
            if (tick != _cachedTick) return true;
            try
            {
                int count = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .Count(f => { string e = Path.GetExtension(f).ToLowerInvariant(); return e == ".gb" || e == ".gbc"; });
                return count != _cachedCount;
            }
            catch { return false; }
        }

        // Grabs the newest last-write tick among all ROM files in the dir
        private static long GetDirectoryTick(string dir)
        {
            try
            {
                long newest = 0;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".gb" && ext != ".gbc") continue;
                    long t = File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > newest) newest = t;
                }
                return newest;
            }
            catch { return 0; }
        }

        private static string GetRomDirectory()
        {
            string configured = CodeDmgPlugin.ConfigSettings?.RomDirectory?.Value;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try { Directory.CreateDirectory(configured); } catch { }
                return configured;
            }

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Bomb Rush Cyberfunk Modding",
                "BRCGameBoyEmu",
                "Roms");
            Directory.CreateDirectory(defaultDir);
            return defaultDir;
        }

        private void BuildRows()
        {
            ClearRows();
            _windowStart = 0;

            if (_romPaths.Count == 0)
            {
                var emptyGo = new GameObject("Empty");
                emptyGo.transform.SetParent(_listContent, false);
                var t = emptyGo.AddComponent<TextMeshProUGUI>();
                t.text = "No ROMs found.";
                t.fontSize = 22f;
                t.color = new Color(0.55f, 0.55f, 0.55f, 1f);
                t.alignment = TextAlignmentOptions.Center;
                var ert = t.rectTransform;
                ert.anchorMin = new Vector2(0f, 1f);
                ert.anchorMax = new Vector2(1f, 1f);
                ert.pivot = new Vector2(0.5f, 1f);
                ert.anchoredPosition = new Vector2(0f, -12f);
                ert.sizeDelta = new Vector2(0f, RowH);
                _rows.Add(emptyGo);
                _listContent.sizeDelta = new Vector2(0f, RowH + 24f);
                return;
            }

            int visibleRows = Mathf.Min(_romPaths.Count, MaxVisible);
            float rowStep = RowH + RowSpacing;
            for (int i = 0; i < visibleRows; i++)
                _rows.Add(BuildRow(i, i * rowStep));

            _listContent.anchoredPosition = Vector2.zero;
            _listContent.sizeDelta = new Vector2(0f, visibleRows * rowStep);
            UpdateVisibleRows();
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
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yOffset);
            rt.sizeDelta = new Vector2(0f, RowH);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var lbl = labelGo.AddComponent<TextMeshProUGUI>();
            lbl.text = string.Empty;
            lbl.fontSize = 21f;
            lbl.color = Color.white;
            lbl.alignment = TextAlignmentOptions.MidlineLeft;
            var lrt = lbl.rectTransform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(18f, 0f);
            lrt.offsetMax = new Vector2(-8f, 0f);

            return rowGo;
        }

        private void UpdateVisibleRows()
        {
            string tobuPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gb");
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null) continue;
                int index = _windowStart + i;
                bool active = index >= 0 && index < _romPaths.Count;
                row.SetActive(active);
                if (!active) continue;

                string path = _romPaths[index];
                string label = string.Equals(path, tobuPath, StringComparison.OrdinalIgnoreCase)
                    ? "Tobu Tobu Girl DX"
                    : Path.GetFileNameWithoutExtension(path);
                var lbl = row.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
                if (lbl != null) lbl.text = label;
            }
        }

        private void ClearRows()
        {
            foreach (var r in _rows) if (r) Destroy(r);
            _rows.Clear();
            _listContent.anchoredPosition = Vector2.zero;
        }

        // ── Update ────────────────────────────────────────────────────────────
        private void Update()
        {
            if (!gameObject.activeSelf) return;
            if (!_fontApplied) ApplyGameFont();
            if (_inputGrace) { _inputGrace = false; return; }
            if (_loading) return;

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
            if (_romPaths.Count == 0) return;

            CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;
            bool upDown   = Input.GetKey(cfg.Up.Value)   || Input.GetKey(KeyCode.UpArrow)   || Input.GetAxisRaw("Vertical") > 0.5f;
            bool downDown = Input.GetKey(cfg.Down.Value)  || Input.GetKey(KeyCode.DownArrow) || Input.GetAxisRaw("Vertical") < -0.5f;

            if (PhoneMenuInputRouter.ConsumedUpThisFrame() || PhoneMenuInputRouter.ConsumedDownThisFrame())
            {
                _upHeld = upDown;
                _downHeld = downDown;
                _repeatTimer = 0f;
                return;
            }

            if (upDown && !_upHeld)
            { _upHeld = true; _repeatTimer = 0f; Move(-1); }
            else if (!upDown) _upHeld = false;

            if (downDown && !_downHeld)
            { _downHeld = true; _repeatTimer = 0f; Move(1); }
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
            if (_romPaths.Count == 0) return;
            int prev = _selectedIndex;
            _selectedIndex = (_selectedIndex + dir + _romPaths.Count) % _romPaths.Count;
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
            if (_romPaths.Count == 0) return;
            string picked = _romPaths[_selectedIndex];
            PlayConfirmSfx();
            PopupState.SuppressPhoneNavFor();
            Hide();
            _onPick?.Invoke(picked);
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

        // ── Visual Stuff ──────────────────────────────────────────────────────
        private void UpdateHighlights()
        {
            if (_romPaths.Count == 0) return;
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
            if (_romPaths.Count == 0) return;
            int visibleRows = Mathf.Min(_romPaths.Count, MaxVisible);

            if (_romPaths.Count <= visibleRows)
                _windowStart = 0;
            else if (_selectedIndex < _windowStart || (wrapped && _selectedIndex == 0))
                _windowStart = _selectedIndex;
            else if (_selectedIndex >= _windowStart + visibleRows || wrapped)
                _windowStart = _selectedIndex - visibleRows + 1;

            _windowStart = Mathf.Clamp(_windowStart, 0, Mathf.Max(0, _romPaths.Count - visibleRows));
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
    }
}
