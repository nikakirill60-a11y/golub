using System;
using System.Collections;
using System.Collections.Generic;
using BlitzLegacy.Core;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Platform;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Code-built port of maps/_login_screen.tscn. There are deliberately no
    /// inspector references and no generated prefab: the sole Bootstrap scene
    /// instantiates this graph on startup.
    /// </summary>
    public sealed class LoginScreen : MonoBehaviour
    {
        private enum Tab
        {
            Play,
            Login,
            Accounts,
            Creator,
        }

        private BlitzBootstrap _bootstrap;
        private AtlasRegionTable _atlas;
        private LocalizationTable _lang;
        private Font _regular;
        private Font _bold;

        private readonly Dictionary<Tab, RectTransform> _tabs =
            new Dictionary<Tab, RectTransform>();
        private readonly Dictionary<Text, Coroutine> _errorRoutines =
            new Dictionary<Text, Coroutine>();
        private readonly List<AudioSource> _music = new List<AudioSource>();

        private Text _currentAccount;
        private RectTransform _accountList;
        private ScrollRect _accountScroll;

        private InputField _login;
        private InputField _password;
        private Text _loginError;

        private InputField _newNickname;
        private InputField _newLogin;
        private InputField _newPassword;
        private Text _createError;

        public static LoginScreen Create(BlitzBootstrap bootstrap)
        {
            if (bootstrap == null)
            {
                throw new ArgumentNullException("bootstrap");
            }

            var go = new GameObject("LoginScreen");
            go.transform.SetParent(bootstrap.transform, false);
            var screen = go.AddComponent<LoginScreen>();
            screen._bootstrap = bootstrap;
            screen.Build();
            return screen;
        }

        private void Build()
        {
            _atlas = MenuStyle.ApplyForMenus(AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData)));
            _lang = LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));
            _lang.Language = _bootstrap.Settings.Language;
            _regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);
            _bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);

            var canvas = CreateCanvas(transform);
            BuildBackground(canvas);

            // Godot lays the login screen out against the whole window and
            // never queries the display safe area, so its UI keeps running
            // under a camera hole instead of being pushed away from it.
            var root = UiFactory.Node("ui_root", canvas);
            GodotRect.FullRect(root);

            var tabsRoot = UiFactory.Node("tabs", root);
            GodotRect.FullRect(tabsRoot);
            // Godot's self_modulate only tints the TabContainer itself; it is
            // not inherited by child controls. A Unity CanvasGroup would dim
            // the whole login UI to 32%, which is both incorrect and illegible.

            BuildPlayTab(tabsRoot);
            BuildLoginTab(tabsRoot);
            BuildAccountsTab(tabsRoot);
            BuildCreatorTab(tabsRoot);
            BuildVersion(root);
            EnsureEventSystem();

            UpdateCurrentAccount();
            Show(Tab.Play);
            StartMusic();

            Debug.Log(
                "[Blitz] login UI built: regions=" + _atlas.Count +
                " strings=" + _lang.Count +
                " reference=" + UiLayout.ReferenceWidth + "x" + UiLayout.ReferenceHeight);
        }

        private static RectTransform CreateCanvas(Transform parent)
        {
            var go = new GameObject("Canvas", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution =
                new Vector2(UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referencePixelsPerUnit = 100f;
            MenuUiScale.Register(scaler, scaler.referenceResolution);

            go.AddComponent<GraphicRaycaster>();
            return (RectTransform)go.transform;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            var input = go.AddComponent<InputSystemUIInputModule>();
            input.AssignDefaultActions();
        }

        // ----------------------------------------------------------- background

        /// <summary>The authored size of Gfx2/UI/KeyArt frame0.</summary>
        public const float KeyArtWidth = 2048f;

        /// <inheritdoc cref="KeyArtWidth"/>
        public const float KeyArtHeight = 1536f;

        /// <summary>
        /// How far the key art is dimmed so the login fields stay legible over
        /// the muzzle flash in the middle of it.
        /// </summary>
        public const float KeyArtScrim = 0.45f;

        /// <summary>
        /// The 4.8.0 client's own login backdrop: Gfx2/UI/KeyArt frame0,
        /// exported by Tools/port_blitz_480_keyart.py.
        ///
        /// This replaces the seven parallax layers the Godot project stacked
        /// here (back_login1..7, still in the atlas and still covered by
        /// GodotAnchorsTests). Those layers only ever approximated the client
        /// screen, and four of them leaned on GPUParticles2D fire emitters
        /// that never made it into the port, so the screen never looked right.
        /// One 4:3 artwork enveloped over the canvas is what the client draws.
        /// </summary>
        private void BuildBackground(RectTransform canvas)
        {
            var baseImage = UiFactory.Picture("background_color", canvas, null);
            baseImage.color = new Color(0.24752441f, 0.085244425f, 0f, 1f);
            GodotRect.FullRect(baseImage.rectTransform);

            var art = UiFactory.Picture("keyart", canvas, _atlas.Get("login_keyart"));
            var rect = art.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;

            // The artwork is 4:3 and the canvas is ~1.89:1, so it is fitted by
            // width and the top and bottom are cropped, exactly as the client
            // does on a wide screen.
            var fitter = art.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = KeyArtWidth / KeyArtHeight;

            var scrim = UiFactory.Picture("keyart_scrim", canvas, null);
            scrim.color = new Color(0f, 0f, 0f, KeyArtScrim);
            GodotRect.FullRect(scrim.rectTransform);
        }

        // --------------------------------------------------------------- tabs

        private RectTransform NewTab(Tab id, RectTransform tabsRoot, string name)
        {
            var tab = UiFactory.Node(name, tabsRoot);
            GodotRect.FullRect(tab);
            _tabs[id] = tab;
            return tab;
        }

        private void BuildPlayTab(RectTransform tabsRoot)
        {
            var tab = NewTab(Tab.Play, tabsRoot, "play_button");

            var play = UiFactory.TextButton(
                "button_login2", tab, _atlas.Get("button_green"), _bold,
                UiTheme.PlayButtonFontSize, _lang.Get("PLAY"));
            GodotRect.Centered(
                (RectTransform)play.transform, -108f, 130f, 108f, 190f);
            Bind(play, OnPlay);

            var accounts = UiFactory.TextButton(
                "button_login3", tab, _atlas.Get("button_flat"), _bold,
                UiTheme.ButtonFontSize, _lang.Get("LOGIN_MANAGE_AKKS"));
            GodotRect.Centered(
                (RectTransform)accounts.transform, -76f, 201f, 77f, 236f);
            Bind(accounts, ToAccounts);

            _currentAccount = UiFactory.Label(
                "current_akk", tab, _regular, UiTheme.LabelFontSize,
                UiTheme.Label, TextAnchor.MiddleCenter, string.Empty);
            GodotRect.Centered(
                _currentAccount.rectTransform, -65f, 102f, 64f, 118f);
        }

        private void BuildLoginTab(RectTransform tabsRoot)
        {
            var tab = NewTab(Tab.Login, tabsRoot, "login_window");
            BuildGreyPanel(tab);

            _login = BuildField(
                tab, "l_login", -178f, -101f, 182f, -73f,
                _lang.Get("LOGIN/login"), false);
            _password = BuildField(
                tab, "l_password", -178f, -49f, 182f, -21f,
                _lang.Get("LOGIN/password"), true);

            var title = BoldLabel(
                "Label", tab, _lang.Get("LOGIN_USING_ID"), TextAnchor.MiddleCenter);
            GodotRect.Centered(title.rectTransform, -106.5f, -155f, 106.5f, -119f);

            _loginError = UiFactory.Label(
                "error_text", tab, _regular, UiTheme.SettingsLabelFontSize,
                UiTheme.ErrorText, TextAnchor.MiddleLeft, _lang.Get("LOGIN_WRONG_PASS"));
            GodotRect.Centered(_loginError.rectTransform, -177f, -13f, 36f, 23f);
            _loginError.gameObject.SetActive(false);

            var login = UiFactory.TextButton(
                "button_login", tab, _atlas.Get("button_green"), _bold,
                UiTheme.ButtonFontSize, _lang.Get("LOGIN"));
            GodotRect.Centered((RectTransform)login.transform, -177f, 60f, -8f, 109f);
            Bind(login, TryLogin);

            var create = UiFactory.TextButton(
                "button_login2", tab, _atlas.Get("button_flat"), _bold,
                UiTheme.ButtonFontSize, _lang.Get("LOGIN_CREATE_ID"));
            GodotRect.Centered((RectTransform)create.transform, 0f, 60f, 180f, 109f);
            Bind(create, ToCreator);

            BuildBackButton(tab);
        }

        private void BuildAccountsTab(RectTransform tabsRoot)
        {
            var tab = NewTab(Tab.Accounts, tabsRoot, "accounts_manager");

            var body = SlicedPicture("NinePatchRect", tab, "panel_body");
            GodotRect.Apply(
                body.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -191f, -169f, 581f, 645f, GodotRect.TopLeftPivot);
            body.rectTransform.localScale = new Vector3(0.5f, 0.5f, 1f);

            var header = SlicedPicture("NinePatchRect2", tab, "panel_header");
            GodotRect.Apply(
                header.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -191f, -213f, 322f, -154f, GodotRect.TopLeftPivot);
            header.rectTransform.localScale = new Vector3(0.75f, 0.75f, 1f);

            var title = BoldLabel(
                "top_label", tab, _lang.Get("LOGIN_MANAGE_AKKS"),
                TextAnchor.MiddleCenter);
            GodotRect.Centered(title.rectTransform, -106.5f, -208f, 106.5f, -172f);

            BuildAccountScroll(tab);
            BuildBackButton(tab);

            var create = UiFactory.TextButton(
                "button_login2", tab, _atlas.Get("button_flat"), _bold,
                UiTheme.ButtonFontSize, _lang.Get("LOGIN_CREATE_ID"));
            GodotRect.Centered(
                (RectTransform)create.transform, -181f, 182f, -12f, 231f);
            Bind(create, ToCreator);
        }

        private void BuildCreatorTab(RectTransform tabsRoot)
        {
            var tab = NewTab(Tab.Creator, tabsRoot, "account_creator");

            var body = SlicedPicture("NinePatchRect", tab, "panel_body");
            GodotRect.Apply(
                body.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -191f, -117f, 581f, 409f, GodotRect.TopLeftPivot);
            body.rectTransform.localScale = new Vector3(0.5f, 0.5f, 1f);

            var header = SlicedPicture("NinePatchRect2", tab, "panel_header");
            GodotRect.Apply(
                header.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -191f, -160f, 322f, -101f, GodotRect.TopLeftPivot);
            header.rectTransform.localScale = new Vector3(0.75f, 0.75f, 1f);

            // Exact output of the creator VBoxContainer. Three 28 px fields,
            // margins 10/20 + 0/10 + 10/10, and two 4 px separations = 152 px.
            _newNickname = BuildField(
                tab, "l_nickname", -180f, -98f, 180f, -70f,
                _lang.Get("LOGIN/nickname"), false);
            _newLogin = BuildField(
                tab, "l_login", -180f, -46f, 180f, -18f,
                _lang.Get("LOGIN/login"), false);
            _newPassword = BuildField(
                tab, "l_password", -180f, 6f, 180f, 34f,
                _lang.Get("LOGIN/password"), true);

            var create = UiFactory.TextButton(
                "button_create", tab, _atlas.Get("button_green"), _bold,
                UiTheme.ButtonFontSize, _lang.Get("LOGIN_CREATE_ID"));
            GodotRect.Apply(
                (RectTransform)create.transform, 0.498f, 0.5f, 0.498f, 0.5f,
                -122.696f, 85f, 135.304f, 134f);
            Bind(create, TryCreate);

            BuildBackButton(tab);

            var title = BoldLabel(
                "title", tab, _lang.Get("LOGIN_CREATE_ACCOUNT"),
                TextAnchor.MiddleCenter);
            GodotRect.Centered(title.rectTransform, -106.5f, -155f, 106.5f, -119f);

            _createError = UiFactory.Label(
                "error_text", tab, _regular, UiTheme.SettingsLabelFontSize,
                UiTheme.ErrorText, TextAnchor.MiddleLeft,
                _lang.Get(AccountBook.ErrorKeyEmptyFields));
            GodotRect.Centered(_createError.rectTransform, -170f, 46f, 43f, 82f);
            _createError.gameObject.SetActive(false);
        }

        private void BuildVersion(RectTransform parent)
        {
            var version = BoldLabel(
                "t_version", parent, BlitzVersion.Current, TextAnchor.MiddleLeft);
            GodotRect.Apply(
                version.rectTransform, 0f, 1f, 0f, 1f,
                7f, -25f, 64f, -2f);
        }

        // ------------------------------------------------------------ builders

        private InputField BuildField(
            RectTransform parent,
            string name,
            float left,
            float top,
            float right,
            float bottom,
            string placeholder,
            bool secret)
        {
            var field = UiFactory.Field(
                name, parent, _regular, placeholder, secret, 32);
            GodotRect.Centered(field.GetComponent<RectTransform>(), left, top, right, bottom);
            return field;
        }

        private Text BoldLabel(
            string name, Transform parent, string text, TextAnchor alignment)
        {
            var label = UiFactory.Label(
                name, parent, _bold, UiTheme.BoldLabelFontSize,
                UiTheme.BoldLabel, alignment, text);
            UiFactory.AddShadow(label, UiTheme.BoldLabelShadow);
            return label;
        }

        private Image SlicedPicture(
            string name, Transform parent, string region)
        {
            var image = UiFactory.Picture(name, parent, _atlas.Get(region));
            image.type = Image.Type.Sliced;
            return image;
        }

        private void BuildGreyPanel(RectTransform parent)
        {
            // grey_panel.tscn: the body is the scene root and its header is a
            // child, so the body's 0.5 scale applies to both.
            var body = SlicedPicture("panel", parent, "panel_body");
            GodotRect.Apply(
                body.rectTransform, 0.5f, 0.525f, 0.5f, 0.525f,
                -191.56f, -138.40002f, 580.43994f, 344.9f,
                GodotRect.TopLeftPivot);
            body.rectTransform.localScale = new Vector3(0.5f, 0.5f, 1f);

            var header = SlicedPicture("header", body.transform, "panel_header");
            GodotRect.Apply(
                header.rectTransform, 0f, 0f, 1f, 0f,
                0f, -84f, 0f, 2f, GodotRect.TopLeftPivot);
        }

        private Button BuildBackButton(RectTransform parent)
        {
            var back = UiFactory.IconButton(
                "back", parent, _atlas.Get("button_small_generic"),
                _atlas.Get("icon_back"));
            var rt = (RectTransform)back.transform;
            GodotRect.TopLeft(rt, 11f, 10f, 99f, 93f);
            rt.localScale = new Vector3(0.59f, 0.59f, 1f);
            Bind(back, BackToPlay);
            return back;
        }

        private void BuildAccountScroll(RectTransform parent)
        {
            var viewport = UiFactory.Node("ScrollContainer", parent);
            GodotRect.Apply(
                viewport, 0.502f, 0.507f, 0.502f, 0.507f,
                -181.304f, -167.536f, 187.696f, 167.464f);

            var hitArea = viewport.gameObject.AddComponent<Image>();
            hitArea.color = new Color(0f, 0f, 0f, 0.001f);
            viewport.gameObject.AddComponent<RectMask2D>();

            _accountList = UiFactory.Node("accounts", viewport);
            _accountList.anchorMin = new Vector2(0.5f, 1f);
            _accountList.anchorMax = new Vector2(0.5f, 1f);
            _accountList.pivot = new Vector2(0.5f, 1f);
            _accountList.anchoredPosition = Vector2.zero;
            _accountList.sizeDelta = new Vector2(360f, 0f);

            var layout = _accountList.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = UiTheme.Separation;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = _accountList.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _accountScroll = viewport.gameObject.AddComponent<ScrollRect>();
            _accountScroll.viewport = viewport;
            _accountScroll.content = _accountList;
            _accountScroll.horizontal = false;
            _accountScroll.vertical = true;
            _accountScroll.movementType = ScrollRect.MovementType.Elastic;
            _accountScroll.inertia = true;
            _accountScroll.decelerationRate = 0.135f;
            _accountScroll.scrollSensitivity = 40f;
        }

        // -------------------------------------------------------------- actions

        private void Bind(Button button, Action action)
        {
            button.onClick.AddListener(() =>
            {
                _bootstrap.Audio.PlayButtonClick(AudioService.SoundEnter);
                action();
            });
        }

        private void Show(Tab selected)
        {
            foreach (var pair in _tabs)
            {
                pair.Value.gameObject.SetActive(pair.Key == selected);
            }
        }

        private void BackToPlay()
        {
            UpdateCurrentAccount();
            Show(Tab.Play);
        }

        private void ToAccounts()
        {
            UpdateCurrentAccount();
            RebuildAccounts();
            Show(Tab.Accounts);
        }

        private void ToCreator()
        {
            Show(Tab.Creator);
        }

        private void TryLogin()
        {
            // This intentionally mirrors the effective Godot behaviour. Its
            // empty-field guard is a no-op, so empties reach the same invalid
            // credentials error as every other missing account.
            var account = _bootstrap.Accounts.FindByCredentials(
                _login.text, _password.text);
            if (account == null)
            {
                ShowError(_loginError, "LOGIN_WRONG_PASS");
                return;
            }

            _bootstrap.Accounts.SwitchAccount(account.Key);
            OnPlay();
        }

        private void OnPlay()
        {
            if (_bootstrap.Accounts.GetCurrentAccount() == null)
            {
                ToAccounts();
                return;
            }

            if (_bootstrap.Accounts.LogInToCurrentAccount(
                _bootstrap.Clock.NowUnixSeconds))
            {
                _bootstrap.Persist();
                UpdateCurrentAccount();
                _bootstrap.EnterHangar(this);
            }
        }

        private void TryCreate()
        {
            var result = _bootstrap.Accounts.CreateAccount(
                _newNickname.text,
                _newLogin.text,
                _newPassword.text,
                _bootstrap.Clock.NowUnixSeconds);

            if (!result.Ok)
            {
                ShowError(_createError, result.ErrorLocalizationKey);
                return;
            }

            _bootstrap.Persist();
            _newNickname.text = string.Empty;
            _newLogin.text = string.Empty;
            _newPassword.text = string.Empty;
            Show(Tab.Login);
        }

        private void SelectAccount(string key)
        {
            if (!_bootstrap.Accounts.SwitchAccount(key))
            {
                return;
            }

            _bootstrap.Persist();
            UpdateCurrentAccount();
            RebuildAccounts();
        }

        private void UpdateCurrentAccount()
        {
            if (_currentAccount == null)
            {
                return;
            }

            var prefix = _lang.Get("LOGIN_CURRENT_AKK");
            var account = _bootstrap.Accounts.GetCurrentAccount();
            _currentAccount.text = account == null
                ? prefix + _lang.Get("LOGIN_NO_ACCOUNT")
                : prefix + "'" + account.Name + "'";
        }

        private void RebuildAccounts()
        {
            if (_accountList == null)
            {
                return;
            }

            for (var i = _accountList.childCount - 1; i >= 0; i--)
            {
                Destroy(_accountList.GetChild(i).gameObject);
            }

            foreach (var pair in _bootstrap.Accounts.Accounts)
            {
                var key = pair.Key;
                AccountCard.Build(
                    _accountList,
                    pair.Value,
                    string.Equals(
                        key, _bootstrap.Accounts.CurrentAccountKey,
                        StringComparison.Ordinal),
                    _lang,
                    _atlas,
                    _bold,
                    Bind,
                    () => SelectAccount(key));
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_accountList);
            _accountScroll.verticalNormalizedPosition = 1f;
        }

        private void ShowError(Text label, string key)
        {
            if (label == null)
            {
                return;
            }

            Coroutine previous;
            if (_errorRoutines.TryGetValue(label, out previous) && previous != null)
            {
                StopCoroutine(previous);
            }

            label.text = _lang.Get(key);
            label.color = UiTheme.ErrorText;
            label.gameObject.SetActive(true);
            _errorRoutines[label] = StartCoroutine(HideErrorAfterDelay(label));
        }

        private IEnumerator HideErrorAfterDelay(Text label)
        {
            const float fadeSeconds = 0.1f;
            yield return new WaitForSecondsRealtime(
                UiTheme.ErrorVisibleSeconds - fadeSeconds);

            var elapsed = 0f;
            while (elapsed < fadeSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                var color = UiTheme.ErrorText;
                color.a = 1f - Mathf.Clamp01(elapsed / fadeSeconds);
                label.color = color;
                yield return null;
            }

            label.gameObject.SetActive(false);
            label.color = UiTheme.ErrorText;
            _errorRoutines.Remove(label);
        }

        // --------------------------------------------------------------- audio

        private void StartMusic()
        {
            AddMusic(AudioService.MusicHarmony);
            AddMusic(AudioService.MusicAmbient);
            AddMusic(AudioService.MusicDrums);
        }

        private void AddMusic(string clip)
        {
            var source = _bootstrap.Audio.PlaySound2D(
                clip, AudioBus.Music, true, 1f);
            if (source != null)
            {
                _music.Add(source);
            }
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _music.Count; i++)
            {
                if (_music[i] != null)
                {
                    _music[i].Stop();
                    Destroy(_music[i].gameObject);
                }
            }

            _music.Clear();
        }
    }
}
