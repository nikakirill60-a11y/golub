using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Time;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Play Mode timing of the hangar with the whole roster owned, the garage
    /// that weighs most on the carousel: the debug toolbox unlocks every
    /// vehicle, then each step below runs a few times through the same
    /// buttons a player presses. A step's time is its synchronous work plus a forced canvas
    /// update, and its frame time adds the two frames after it, which carry
    /// the deferred destruction and the redraw. Numbers describe this port in
    /// the editor on this machine, not a device.
    /// </summary>
    [InitializeOnLoad]
    public static class HangarPerformanceCli
    {
        private const string ActiveKey = "Blitz.HangarPerf.Active";
        private const string ExitCodeKey = "Blitz.HangarPerf.ExitCode";
        private const string Output = "Logs/hangar-perf";
        private const int Repeats = 3;
        private const int SettleFrames = 2;
        private const int ScrollSteps = 10;

        private static bool _hooked;
        private static readonly List<Step> Steps = new List<Step>();
        private static readonly Dictionary<string, Timing> Timings = new Dictionary<string, Timing>();
        private static readonly List<string> Order = new List<string>();
        private static int _next;
        private static int _settleUntil;
        private static Step _running;
        private static double _startedMs;
        private static double _actionMs;
        private static BlitzBootstrap _bootstrap;
        private static HangarScreen _screen;
        private static AccountData _account;
        private static TanksDatabase _tanks;
        private static int _errors;

        /// <summary>
        /// The wall clock of the run: a week after the Halloween 2026 hangar,
        /// so the steps time the 4.8 hangar on any date; the event steps move
        /// it in and out of the event.
        /// </summary>
        private static FixedClock _clock;

        private sealed class Step
        {
            public string Name;
            public Action Act;
        }

        private sealed class Timing
        {
            public readonly List<double> Action = new List<double>();
            public readonly List<double> Frame = new List<double>();
        }

        static HangarPerformanceCli()
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
            if (_hooked)
            {
                return;
            }

            _hooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                Application.logMessageReceived += CountErrors;
                EditorApplication.delayCall += Prepare;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var exitCode = SessionState.GetInt(ExitCodeKey, -1);
                Quit(exitCode < 0 ? 1 : exitCode);
            }
        }

        private static void CountErrors(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception)
            {
                _errors++;
            }
        }

        private static void Prepare()
        {
            try
            {
                _bootstrap = Object.FindFirstObjectByType<BlitzBootstrap>();
                var login = Object.FindFirstObjectByType<LoginScreen>();
                Require(_bootstrap != null && login != null, "The bootstrap scene did not reach the login screen");

                _account = new AccountData
                {
                    Name = "HangarPerf",
                    Credits = 250000,
                    Gold = 250,
                    FreeXp = 0,
                    SelectedTankName = "ms-1",
                };
                _account.AddTank("ms-1", true);
                Object.Destroy(login.gameObject);
                _clock = new FixedClock(HangarSchedule.UnixSeconds(
                    HangarSchedule.Halloween2026LastDay.AddDays(7).AddHours(12), null));
                _bootstrap.UseClock(_clock);
                _screen = HangarScreen.Create(_bootstrap, _account);
                _tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                Require(_screen.Toolbox != null, "The debug account toolbox was not built");

                // Cold: nothing has asked for these models in this session yet.
                Add("has_model_first", () =>
                {
                    foreach (var tank in _tanks.Tanks)
                    {
                        TankAssetSource.HasModel(tank);
                    }
                }, 1);
                Add("unlock_all", () =>
                {
                    _screen.Toolbox.UnlockAllTanks();
                    _screen.Toolbox.FullUpgradeAllTanks();
                }, 1);
                Add("idle", () => { }, Repeats);
                for (var i = 0; i < Repeats; i++)
                {
                    Add("filter_on", () => FilterToggle("lvl10").onClick.Invoke(), 1);
                    Add("filter_off", () => FilterToggle("lvl10").onClick.Invoke(), 1);
                }

                for (var i = 0; i < Repeats; i++)
                {
                    var card = 100 + (i * 37);
                    Add("select", () => CardButton(card).onClick.Invoke(), 1);
                }

                for (var i = 1; i <= ScrollSteps; i++)
                {
                    var position = i / (float)ScrollSteps;
                    Add("scroll", () => Scroll().horizontalNormalizedPosition = position, 1);
                }

                // The path a mounted module or equipment takes: the loadout,
                // the shop and the toolbox all raise the hangar's account change.
                Add("account_changed", () => _screen.Toolbox.SetCredits(250000 + _next), Repeats);
                Add("save_json", () => _account.ToJson().ToJson(), Repeats);
                Add("has_model_all", () =>
                {
                    foreach (var tank in _account.Tanks)
                    {
                        TankAssetSource.HasModel(_tanks.Get(tank.Name));
                    }
                }, Repeats);
                Add("recreate_hangar", () =>
                {
                    _screen.gameObject.SetActive(false);
                    Object.Destroy(_screen.gameObject);
                    _screen = HangarScreen.Create(_bootstrap, _account);
                }, 2);

                // The seasonal hangar: what the screen's once-a-second
                // schedule poll does as the clock enters and leaves the event.
                for (var i = 0; i < 2; i++)
                {
                    Add("halloween_on", () => ShowEvent(true), 1);
                    Add("halloween_idle", () => { }, Repeats);
                    Add("halloween_off", () => ShowEvent(false), 1);
                }

                EditorApplication.update += Tick;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void ShowEvent(bool on)
        {
            _clock.NowUnixSeconds = HangarSchedule.UnixSeconds(on
                ? HangarSchedule.Halloween2026FirstDay.AddDays(3).AddHours(12)
                : HangarSchedule.Halloween2026LastDay.AddDays(7).AddHours(12), null);
            var kind = HangarSchedule.ResolveLocal(
                _clock.NowUnixSeconds, _account.HasPremium(_clock.NowUnixSeconds));
            Require(on == (kind == HangarKind.Halloween2026), "The pinned clock missed the event window");
            Require(_screen.World.SetKind(kind), "The hangar was already " + kind);
        }

        private static void Add(string name, Action act, int times)
        {
            for (var i = 0; i < times; i++)
            {
                Steps.Add(new Step { Name = name, Act = act });
            }
        }

        private static Button FilterToggle(string name)
        {
            if (_screen.IsModern)
            {
                var panel = _screen.CarouselBar.parent.Find("filter");
                var found = panel == null ? null : FindByName<Button>(panel, name);
                Require(found != null, "The Modern filter has no " + name);
                return found;
            }

            var toggle = _screen.FilterDrawer.FindToggle(name);
            Require(toggle != null, "The classic filter has no " + name);
            return toggle;
        }

        private static ScrollRect Scroll()
        {
            var scroll = _screen.CarouselRoot.GetComponentInParent<ScrollRect>();
            Require(scroll != null, "The carousel has no ScrollRect");
            return scroll;
        }

        /// <summary>Scrolls card <paramref name="index"/> on and returns its button.</summary>
        private static Button CardButton(int index)
        {
            Require(index < _screen.CarouselCardCount, "The carousel has no card " + index);
            var card = _screen.ShowCarouselCard(index);
            var button = FindByName<Button>(card, "button");
            Require(button != null, "Carousel card " + index + " has no button");
            return button;
        }

        private static void Tick()
        {
            try
            {
                if (_running != null)
                {
                    if (Time.frameCount < _settleUntil)
                    {
                        return;
                    }

                    Record(_running.Name, _actionMs, Now() - _startedMs);
                    _running = null;
                }

                if (_next >= Steps.Count)
                {
                    EditorApplication.update -= Tick;
                    Report();
                    return;
                }

                _running = Steps[_next++];
                _startedMs = Now();
                _running.Act();
                Canvas.ForceUpdateCanvases();
                _actionMs = Now() - _startedMs;
                _settleUntil = Time.frameCount + SettleFrames;
            }
            catch (Exception exception)
            {
                EditorApplication.update -= Tick;
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void Record(string name, double action, double frame)
        {
            Timing timing;
            if (!Timings.TryGetValue(name, out timing))
            {
                timing = new Timing();
                Timings.Add(name, timing);
                Order.Add(name);
            }

            timing.Action.Add(action);
            timing.Frame.Add(frame);
        }

        private static void Report()
        {
            var style = _screen.IsModern ? "modern" : "classic";
            var json = new StringBuilder();
            var line = new StringBuilder();
            json.Append("{\n  \"unity\": \"").Append(Application.unityVersion)
                .Append("\",\n  \"style\": \"").Append(style)
                .Append("\",\n  \"cards\": ").Append(_screen.CarouselCardCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"built\": ").Append(_screen.CarouselCardsBuilt.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"steps\": {");
            for (var i = 0; i < Order.Count; i++)
            {
                var timing = Timings[Order[i]];
                json.Append(i == 0 ? "\n" : ",\n")
                    .Append("    \"").Append(Order[i]).Append("\": { \"action_ms\": ")
                    .Append(List(timing.Action)).Append(", \"frame_ms\": ").Append(List(timing.Frame)).Append(" }");
                line.Append(' ').Append(Order[i]).Append('=').Append(Ms(Median(timing.Action)))
                    .Append('/').Append(Ms(Median(timing.Frame)));
            }

            json.Append("\n  }\n}\n");
            Directory.CreateDirectory(Output);
            var path = Path.Combine(Output, style + ".json");
            File.WriteAllText(path, json.ToString());
            Debug.Log("[Blitz] hangar perf " + style + " (median action/frame ms, " +
                _screen.CarouselCardCount + " cards, " + _screen.CarouselCardsBuilt + " built):" + line + "; " + path);
            Finish(_errors == 0 ? 0 : 1);
        }

        private static string List(List<double> values)
        {
            var text = new StringBuilder("[");
            for (var i = 0; i < values.Count; i++)
            {
                text.Append(i == 0 ? string.Empty : ", ").Append(Ms(values[i]));
            }

            return text.Append(']').ToString();
        }

        private static double Median(List<double> values)
        {
            var sorted = new List<double>(values);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        private static string Ms(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static double Now()
        {
            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }

        private static T FindByName<T>(Transform root, string name) where T : Component
        {
            foreach (var candidate in root.GetComponentsInChildren<T>(true))
            {
                if (candidate.name == name)
                {
                    return candidate;
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
            Application.logMessageReceived -= CountErrors;
            SessionState.SetInt(ExitCodeKey, exitCode);
            EditorApplication.isPlaying = false;
        }

        private static void Quit(int exitCode)
        {
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseInt(ExitCodeKey);
            EditorApplication.Exit(exitCode);
        }
    }
}
