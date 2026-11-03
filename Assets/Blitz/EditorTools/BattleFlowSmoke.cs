using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Play-mode gate for the complete queue-to-result route.</summary>
    [InitializeOnLoad]
    public static class BattleFlowSmoke
    {
        private const string ActiveKey = "Blitz.FlowSmoke.Active";
        private const string ExitCodeKey = "Blitz.FlowSmoke.ExitCode";
        private const string DeadlineKey = "Blitz.FlowSmoke.Deadline";
        private const string Login = "flow-smoke";
        private const string Password = "smoke";

        private enum Stage
        {
            None,
            WaitHangar,
            WaitIntro,
            WaitCountdown,
            WaitBattle,
            WaitResult,
            WaitStatistics,
            WaitHistoricalResult,
        }

        private static bool _hooked;
        private static BlitzBootstrap _bootstrap;
        private static Stage _stage;
        private static bool _observedBlackLoading;
        private static long _creditsBefore;
        private static int _expectedParticipants;
        private static string _accountKey;
        private static BattleOutcome _requestedOutcome = BattleOutcome.Victory;

        static BattleFlowSmoke()
        {
            if (SessionState.GetBool(ActiveKey, false))
            {
                Hook();
            }
        }

        public static void Run()
        {
            _requestedOutcome = ParseRequestedOutcome(
                Environment.GetEnvironmentVariable("BLITZ_FLOW_OUTCOME"));
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ExitCodeKey, -1);
            SessionState.SetString(
                DeadlineKey, DateTime.UtcNow.AddMinutes(5).ToString("O"));
            Hook();
            try
            {
                EditorSceneManager.OpenScene(SceneGen.BootstrapScenePath, OpenSceneMode.Single);
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
            if (_hooked) { return; }
            _hooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void Unhook()
        {
            if (!_hooked) { return; }
            _hooked = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false)) { return; }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                EditorApplication.delayCall += Prepare;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var code = SessionState.GetInt(ExitCodeKey, -1);
                if (code >= 0) { Quit(code); }
            }
        }

        private static void Prepare()
        {
            try
            {
                _bootstrap = FindSceneObject<BlitzBootstrap>();
                Require(_bootstrap != null, "BlitzBootstrap was not created");
                var old = _bootstrap.Accounts.FindByCredentials(Login, Password);
                if (old != null)
                {
                    _bootstrap.Accounts.DeleteAccount(old.Key);
                }
                var created = _bootstrap.Accounts.CreateAccount(
                    "FlowSmoke", Login, Password, _bootstrap.Clock.NowUnixSeconds);
                Require(created.Ok, "Flow smoke account could not be created");
                _accountKey = created.Account.Key;
                Require(_bootstrap.Accounts.SwitchAccount(_accountKey),
                    "Flow smoke account could not be selected");
                _bootstrap.Accounts.LogInToCurrentAccount(_bootstrap.Clock.NowUnixSeconds);
                _creditsBefore = created.Account.Credits;

                var login = FindSceneObject<LoginScreen>();
                Require(login != null, "LoginScreen was not created");
                _bootstrap.EnterHangar(login);
                _stage = Stage.WaitHangar;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void OnEditorUpdate()
        {
            if (!SessionState.GetBool(ActiveKey, false)) { return; }
            DateTime deadline;
            if (DateTime.TryParse(SessionState.GetString(DeadlineKey, string.Empty),
                    out deadline) && DateTime.UtcNow > deadline)
            {
                Debug.LogError("[Blitz] battle flow smoke timed out at " + _stage);
                Finish(1);
                return;
            }
            if (!EditorApplication.isPlaying || _bootstrap == null)
            {
                return;
            }

            try
            {
                TickStage();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void TickStage()
        {
            switch (_stage)
            {
                case Stage.WaitHangar:
                    if (_bootstrap.Hangar == null) { return; }
                    _bootstrap.EnterBattle(_bootstrap.Hangar);
                    Require(_bootstrap.Queue != null, "Queue screen did not open");
                    Require(_bootstrap.Queue.TierCounts.Count == 10
                            && _bootstrap.Queue.TypeCounts.Count == 4
                            && _bootstrap.Queue.TotalPlayers > 0,
                        "Queue counters were not populated");
                    Require(_bootstrap.Queue.MatchDelay >= 1f
                            && _bootstrap.Queue.MatchDelay <= 3f,
                        "Queue match delay left the source 1..3 second range");

                    // Source cancel returns to the same live hangar.
                    _bootstrap.Queue.Cancel();
                    Require(_bootstrap.Queue == null && _bootstrap.Hangar != null
                            && _bootstrap.Hangar.gameObject.activeSelf,
                        "Queue cancel did not return to the live hangar");
                    _bootstrap.EnterBattle(_bootstrap.Hangar);
                    Require(_bootstrap.Queue != null,
                        "Queue did not reopen after cancellation");
                    _bootstrap.Queue.Match();
                    _stage = Stage.WaitIntro;
                    break;

                case Stage.WaitIntro:
                    if (_bootstrap.Loading != null)
                    {
                        _observedBlackLoading = true;
                    }
                    if (_bootstrap.MapIntro == null) { return; }
                    Require(_observedBlackLoading,
                        "The black yellow-spinner transition was skipped");
                    Require(_bootstrap.Battle != null
                            && !_bootstrap.Battle.Roster.IsOngoing
                            && _bootstrap.Battle.Player.Blocked,
                        "Battle was not held paused behind the map intro");
                    var teamSize = _bootstrap.Battle.MatchRoster.TeamSize;
                    _expectedParticipants = teamSize * 2;
                    Require(_bootstrap.MapIntro.AllyRows == teamSize
                            && _bootstrap.MapIntro.EnemyRows == teamSize,
                        "Map intro does not list both complete matched teams");
                    RequireIntroDrawOrder(_bootstrap.MapIntro);
                    Require(_bootstrap.Battle.Participants.Count == _expectedParticipants,
                        "Battle participant registry is incomplete");
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < _bootstrap.Battle.Participants.Count; i++)
                    {
                        names.Add(_bootstrap.Battle.Participants[i].Nickname);
                    }
                    Require(names.Count == _expectedParticipants,
                        "Source nickname pool did not produce unique matched players");
                    _bootstrap.MapIntro.Complete();
                    _stage = Stage.WaitCountdown;
                    break;

                case Stage.WaitCountdown:
                    if (_bootstrap.Countdown == null) { return; }
                    Require(_bootstrap.Countdown.SecondsLeft == 5
                            && !_bootstrap.Battle.Roster.IsOngoing,
                        "Five-second countdown did not keep battle paused");
                    _bootstrap.Countdown.Complete();
                    _stage = Stage.WaitBattle;
                    break;

                case Stage.WaitBattle:
                    if (_bootstrap.Battle == null
                        || !_bootstrap.Battle.Roster.IsOngoing) { return; }
                    Require(!_bootstrap.Battle.Player.Blocked,
                        "Player did not unlock at battle_start");
                    for (var i = 0; i < _bootstrap.Battle.Bots.Count; i++)
                    {
                        Require(_bootstrap.Battle.Bots[i].Active,
                            "A bot did not unlock after countdown");
                    }
                    _bootstrap.Battle.ForceFinish(WinnerFor(_requestedOutcome));
                    _stage = Stage.WaitResult;
                    break;

                case Stage.WaitResult:
                    if (_bootstrap.Result == null) { return; }
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null
                        && !_bootstrap.Result.BackdropReady) return;
                    if (_bootstrap.Result.BackdropReady) ValidateBackdrop(_bootstrap.Result.BackdropTexture);
                    var result = _bootstrap.LastBattleResult;
                    Require(result != null && result.Outcome == _requestedOutcome,
                        "Forced finish produced the wrong battle outcome");
                    Require(result.Participants.Count == _expectedParticipants
                            && result.Player != null && result.Player.IsPlayer,
                        "Result did not retain the complete roster/player row");
                    var account = _bootstrap.Accounts.GetCurrentAccount();
                    Require(account != null && account.BattleResults.Count == 1,
                        "Result was not saved exactly once");
                    Require(account.Credits > _creditsBefore,
                        "Battle credits were not applied");
                    Require(_bootstrap.Battle == null && _bootstrap.Hangar != null,
                        "Result was not presented over the rebuilt hangar");
                    Require(_bootstrap.Result.TeamRowCount == _expectedParticipants,
                        "Result team tab lost participants");
                    _bootstrap.Result.ShowTab(1);
                    Require(_bootstrap.Result.ActiveTab == 1,
                        "Result team tab cannot open");
                    _bootstrap.Result.ShowTab(2);
                    Require(_bootstrap.Result.ActiveTab == 2,
                        "Result details tab cannot open");
                    _bootstrap.Result.transform.Find("Canvas/battle_result/btn_menu_battleres2").GetComponent<Button>().onClick.Invoke();
                    Require(_bootstrap.Result == null && _bootstrap.Statistics != null,
                        "Result profile button did not open account statistics");
                    _stage = Stage.WaitStatistics;
                    break;

                case Stage.WaitStatistics:
                    var statistics = _bootstrap.Statistics;
                    if (statistics == null) return;
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && !statistics.Backdrop.Ready) return;
                    if (statistics.Backdrop.Ready) ValidateBackdrop(statistics.Backdrop.Texture);
                    statistics.SourceUi.Button("btn_battles").onClick.Invoke();
                    Require(statistics.ActiveTab == 1 && statistics.VisibleResultCount == 1,
                        "Battle history did not contain the saved result");
                    statistics.SourceUi.Nodes["tabs/battles/ScrollContainer/battles_box"].GetChild(0).Find("btn").GetComponent<Button>().onClick.Invoke();
                    Require(_bootstrap.Statistics == null && _bootstrap.Result != null,
                        "History result did not open");
                    _stage = Stage.WaitHistoricalResult;
                    break;

                case Stage.WaitHistoricalResult:
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && !_bootstrap.Result.BackdropReady) return;
                    var historicalAccount = _bootstrap.Accounts.GetCurrentAccount();
                    Require(historicalAccount.BattleResults.Count == 1,
                        "History result was applied for a second time");
                    var savedCredits = historicalAccount.Credits;
                    var savedGold = historicalAccount.Gold;
                    _bootstrap.Result.Close();
                    Require(_bootstrap.Result == null, "History home did not return to hangar");
                    _bootstrap.Hangar.transform.Find("Canvas/ui_root/z1/z1_Main/btn_acc").GetComponent<Button>().onClick.Invoke();
                    Require(_bootstrap.Statistics != null, "Hangar nickname did not open profile");
                    _bootstrap.CloseAccountStatistics();
                    Require(historicalAccount.Credits == savedCredits && historicalAccount.Gold == savedGold,
                        "Viewing statistics changed the account balance");
                    _bootstrap.Accounts.DeleteAccount(_accountKey);
                    _bootstrap.Persist();
                    Debug.Log("[Blitz] battle flow smoke ok: queue/cancel/intro/"
                        + "countdown/" + _requestedOutcome.ToString().ToLowerInvariant()
                        + "/result/profile/history/read-only-result/nickname/persist/hangar");
                    Finish(0);
                    break;
            }
        }

        private static void ValidateBackdrop(RenderTexture target)
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0,0,target.width,target.height),0,0); image.Apply();
                var pixels = image.GetPixels32();
                var lo = 255; var hi = 0;
                for (var i = 0; i < pixels.Length; i += 19)
                {
                    lo = Math.Min(lo, pixels[i].r); hi = Math.Max(hi, pixels[i].r);
                }
                Require(hi - lo > 20, "Backdrop capture is blank/uniform");
                System.IO.File.WriteAllBytes("Logs/result-backdrop-capture.png", image.EncodeToPNG());
                Debug.Log("[Blitz] result backdrop capture ok: range=" + (hi - lo));
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(image); }
        }

        private static void Finish(int code)
        {
            SessionState.SetInt(ExitCodeKey, code);
            _bootstrap = null;
            _stage = Stage.None;
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
            }
            else
            {
                Quit(code);
            }
        }

        private static T FindSceneObject<T>() where T : UnityEngine.Object
        {
            var objects = Resources.FindObjectsOfTypeAll<T>();
            for (var i = 0; i < objects.Length; i++)
            {
                var component = objects[i] as Component;
                if (component != null && component.gameObject.scene.IsValid())
                {
                    return objects[i];
                }
            }
            return null;
        }

        private static BattleOutcome ParseRequestedOutcome(string value)
        {
            if (string.Equals(value, "draw", StringComparison.OrdinalIgnoreCase))
            {
                return BattleOutcome.Draw;
            }
            if (string.Equals(value, "defeat", StringComparison.OrdinalIgnoreCase))
            {
                return BattleOutcome.Defeat;
            }
            return BattleOutcome.Victory;
        }

        private static int WinnerFor(BattleOutcome outcome)
        {
            return outcome == BattleOutcome.Draw ? -1
                : outcome == BattleOutcome.Victory ? 0 : 1;
        }

        /// <summary>
        /// uGUI draws canvas children in sibling order, so the full-screen
        /// splash has to stay between the curtain and the authored root. When it
        /// is appended last it paints over the team lists, the map name and the
        /// mode label, which is exactly the regression this guards.
        /// </summary>
        private static void RequireIntroDrawOrder(BattleLoadingScreen intro)
        {
            var canvas = intro.GetComponentInChildren<Canvas>(true);
            Require(canvas != null, "Map intro has no canvas");
            if (intro.IsModern)
            {
                // The modern intro has no authored Godot root: the curtain, the
                // map art and the content are direct canvas children.
                var modernCurtain = canvas.transform.Find("curtain");
                var art = canvas.transform.Find("bg_holder");
                Require(modernCurtain != null && art != null
                        && modernCurtain.GetSiblingIndex() < art.GetSiblingIndex(),
                    "Modern map intro is missing the curtain or the map art");
                var modernName = canvas.GetComponentsInChildren<Text>(true);
                var hasMapName = false;
                for (var i = 0; i < modernName.Length; i++)
                {
                    hasMapName |= modernName[i].name == "mapname"
                        && !string.IsNullOrEmpty(modernName[i].text);
                }

                Require(hasMapName, "Modern map intro shows no map name");
                return;
            }

            var curtain = canvas.transform.Find("curtain");
            var background = canvas.transform.Find("bg");
            var root = canvas.transform.Find("authored_1040x550");
            Require(curtain != null && background != null && root != null,
                "Map intro is missing the curtain, the splash or the authored root");
            Require(curtain.GetSiblingIndex() < background.GetSiblingIndex()
                    && background.GetSiblingIndex() < root.GetSiblingIndex(),
                "The loading splash is not drawn behind the authored map intro content");

            var mapName = root.Find("mapname");
            var mode = root.Find("gamemode");
            var teams = root.Find("teams");
            Require(mapName != null && mode != null && teams != null,
                "Map intro lost the map name, the mode label or the team lists");
            var mapLabel = mapName.GetComponent<Text>();
            Require(mapLabel != null && !string.IsNullOrEmpty(mapLabel.text),
                "Map intro shows no map name");
            Require(teams.Find("team_ally") != null
                    && teams.Find("team_enemies") != null,
                "Map intro lost one of the scoreboard team lists");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Quit(int code)
        {
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseInt(ExitCodeKey);
            SessionState.EraseString(DeadlineKey);
            Unhook();
            EditorApplication.Exit(code);
        }
    }
}
