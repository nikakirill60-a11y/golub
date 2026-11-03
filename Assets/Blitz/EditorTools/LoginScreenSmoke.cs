using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Starts Bootstrap.unity in real Play Mode and validates the code-built
    /// login graph. This catches failures that edit-mode Core tests cannot see:
    /// missing Resources, fonts, uGUI/Input System wiring, or runtime exceptions.
    /// </summary>
    [InitializeOnLoad]
    public static class LoginScreenSmoke
    {
        private const string ActiveKey = "Blitz.LoginSmoke.Active";
        private const string ExitCodeKey = "Blitz.LoginSmoke.ExitCode";
        private const string DeadlineKey = "Blitz.LoginSmoke.Deadline";
        private static bool _hooked;

        static LoginScreenSmoke()
        {
            if (SessionState.GetBool(ActiveKey, false))
            {
                Hook();
            }
        }

        public static void Run()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ExitCodeKey, -1);
            SessionState.SetString(
                DeadlineKey, DateTime.UtcNow.AddMinutes(3).ToString("O"));
            Hook();

            try
            {
                EditorSceneManager.OpenScene(
                    SceneGen.BootstrapScenePath, OpenSceneMode.Single);
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Quit(1);
            }
        }

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void Unhook()
        {
            if (!_hooked)
            {
                return;
            }

            _hooked = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // Awake builds the graph before EnteredPlayMode, but delaying one
                // editor tick also lets uGUI calculate its first layout pass.
                EditorApplication.delayCall += Validate;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var exitCode = SessionState.GetInt(ExitCodeKey, -1);
                if (exitCode >= 0)
                {
                    Quit(exitCode);
                }
            }
        }

        private static void OnEditorUpdate()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                return;
            }

            var exitCode = SessionState.GetInt(ExitCodeKey, -1);
            if (exitCode >= 0 && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Quit(exitCode);
                return;
            }

            DateTime deadline;
            if (DateTime.TryParse(SessionState.GetString(DeadlineKey, string.Empty), out deadline) &&
                DateTime.UtcNow > deadline.ToUniversalTime())
            {
                Debug.LogError("[Blitz] login smoke timed out");
                Finish(1);
            }
        }

        private static void Validate()
        {
            try
            {
                var bootstrap = FindSceneObject<BlitzBootstrap>();
                Require(bootstrap != null, "BlitzBootstrap was not created");

                var screen = FindSceneObject<LoginScreen>();
                Require(screen != null, "LoginScreen was not created");

                var canvas = screen.GetComponentInChildren<Canvas>(true);
                Require(canvas != null, "Canvas is missing");
                Require(canvas.renderMode == RenderMode.ScreenSpaceOverlay,
                    "Canvas is not ScreenSpaceOverlay");

                var scaler = canvas.GetComponent<CanvasScaler>();
                Require(scaler != null, "CanvasScaler is missing");
                Require(scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.Expand,
                    "CanvasScaler is not in Expand mode");
                Require(Mathf.Approximately(
                        scaler.referenceResolution.x, UiLayout.ReferenceWidth) &&
                    Mathf.Approximately(
                        scaler.referenceResolution.y, UiLayout.ReferenceHeight),
                    "Canvas reference resolution is wrong");

                var rects = screen.GetComponentsInChildren<RectTransform>(true);
                RectTransform uiRoot = null;
                RectTransform tabsRoot = null;
                var requiredTabs = new HashSet<string>
                {
                    "play_button",
                    "login_window",
                    "accounts_manager",
                    "account_creator",
                };
                for (var i = 0; i < rects.Length; i++)
                {
                    requiredTabs.Remove(rects[i].name);
                    if (string.Equals(rects[i].name, "tabs", StringComparison.Ordinal))
                    {
                        tabsRoot = rects[i];
                    }

                    if (string.Equals(rects[i].name, "ui_root", StringComparison.Ordinal))
                    {
                        uiRoot = rects[i];
                    }
                }
                Require(requiredTabs.Count == 0, "One or more login tabs are missing");

                // Godot stretches the scene over the whole window, so this root
                // must not be inset by the display safe area: doing that leaves
                // an empty band along a camera hole.
                Require(uiRoot != null, "UI root is missing");
                Require(uiRoot.anchorMin == Vector2.zero &&
                    uiRoot.anchorMax == Vector2.one &&
                    uiRoot.offsetMin == Vector2.zero &&
                    uiRoot.offsetMax == Vector2.zero,
                    "UI root does not cover the whole window");
                Require(tabsRoot != null, "Login tabs root is missing");

                var inheritedOpacity = tabsRoot.GetComponent<CanvasGroup>();
                Require(
                    inheritedOpacity == null || Mathf.Approximately(inheritedOpacity.alpha, 1f),
                    "Login children are incorrectly dimmed by inherited opacity");

                var buttons = screen.GetComponentsInChildren<Button>(true);
                var fields = screen.GetComponentsInChildren<InputField>(true);
                var texts = screen.GetComponentsInChildren<Text>(true);
                var images = screen.GetComponentsInChildren<Image>(true);
                Require(buttons.Length >= 9, "Expected at least nine buttons");
                Require(fields.Length == 5, "Expected exactly five input fields");

                for (var i = 0; i < texts.Length; i++)
                {
                    Require(texts[i].font != null, "Text has no imported font: " + texts[i].name);
                }

                var spriteImages = 0;
                for (var i = 0; i < images.Length; i++)
                {
                    if (images[i].sprite != null)
                    {
                        spriteImages++;
                    }
                }
                // The backdrop used to be eight stacked parallax layers; it is
                // now the client's single key art, so the count is eight lower.
                Require(spriteImages >= 18, "Too few atlas-backed images were built");

                var keyArt = screen.transform.Find("Canvas/keyart");
                Require(keyArt != null, "The login key art backdrop is missing");
                Require(
                    keyArt != null && keyArt.GetComponent<Image>().sprite != null,
                    "The login key art has no sprite");

                Require(EventSystem.current != null, "EventSystem is missing");
                Require(EventSystem.current.GetComponent<InputSystemUIInputModule>() != null,
                    "InputSystemUIInputModule is missing");

                var audio = bootstrap.GetComponentInChildren<AudioService>(true);
                Require(audio != null, "AudioService is missing");

                var listener = audio.Listener != null ? audio.Listener.Listener : null;
                Require(listener != null && listener.enabled,
                    "Persistent 2D AudioListener is missing or disabled");

                var voices = audio.GetComponentsInChildren<AudioSource>(true);
                var loopingMusic = 0;
                var expectedMusicVolume = AudioBusGains.Volume(
                    AudioBus.Music, bootstrap.Settings.VolumeMusic,
                    bootstrap.Settings.VolumeMaster, 1f);
                for (var i = 0; i < voices.Length; i++)
                {
                    if (voices[i].loop && voices[i].clip != null)
                    {
                        loopingMusic++;
                        Require(
                            Mathf.Approximately(voices[i].volume, expectedMusicVolume),
                            "Music volume is wrong: " + voices[i].clip.name);
                        Require(
                            voices[i].clip.loadState != AudioDataLoadState.Failed,
                            "Music data failed to load: " + voices[i].clip.name);
                    }
                }
                Require(loopingMusic == 3, "Expected three looping login music stems");

                var click = audio.PlaySound2D(
                    AudioService.SoundEnter, AudioBus.Ui, false,
                    AudioService.ButtonClickVolume);
                Require(click != null && click.clip != null,
                    "Button click clip could not be loaded");
                Require(
                    click.clip.loadState != AudioDataLoadState.Failed,
                    "Button click audio data failed to load");
                var expectedClickVolume = AudioBusGains.Volume(
                    AudioBus.Ui, bootstrap.Settings.VolumeUi,
                    bootstrap.Settings.VolumeMaster, AudioService.ButtonClickVolume);
                Require(Mathf.Approximately(click.volume, expectedClickVolume),
                    "Button click volume is wrong");

                Debug.Log(
                    "[Blitz] login smoke ok: buttons=" + buttons.Length +
                    " fields=" + fields.Length +
                    " texts=" + texts.Length +
                    " sprites=" + spriteImages +
                    " music=" + loopingMusic);
                Finish(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static T FindSceneObject<T>() where T : Component
        {
            var matches = Resources.FindObjectsOfTypeAll<T>();
            for (var i = 0; i < matches.Length; i++)
            {
                if (matches[i] != null && matches[i].gameObject.scene.IsValid())
                {
                    return matches[i];
                }
            }

            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Finish(int exitCode)
        {
            SessionState.SetInt(ExitCodeKey, exitCode);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
            }
            else
            {
                Quit(exitCode);
            }
        }

        private static void Quit(int exitCode)
        {
            SessionState.SetBool(ActiveKey, false);
            Unhook();
            EditorApplication.delayCall += () => EditorApplication.Exit(exitCode);
        }
    }
}
