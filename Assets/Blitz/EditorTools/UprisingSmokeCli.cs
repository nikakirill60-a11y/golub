using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using BlitzLegacy.Runtime.Ui;
using BlitzLegacy.Runtime.Platform;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Real Play Mode lifecycle gate, isolated from bootstrap/account/save services.</summary>
    [InitializeOnLoad]
    public static class UprisingSmokeCli
    {
        private const string ActiveKey = "Blitz.UprisingSmoke.Active";
        private const string ResultKey = "Blitz.UprisingSmoke.Result";
        private static BattleSession _session;
        private static DateTime _deadline;
        private static float _stageAt;
        private static int _stage;
        private static int _errors;
        private static bool _hooked;
        private static int _firstLife;
        private static bool _perkClicked;
        private static Mouse _mouse;
        private static UprisingMouseInputProbe _mouseProbe;
        private static InputSettings _previousInputSettings, _smokeInputSettings;
        private static bool _previousBackground;
        private static Vector2 _pointer;
        private static float _pointerAt, _menuBalanceAt, _menuYaw, _menuPitch, _menuLength;
        private static int _pointerStage, _menuBalance, _round;

        static UprisingSmokeCli()
        {
            if (SessionState.GetBool(ActiveKey, false)) Hook();
        }

        public static void Run()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ResultKey, -1);
            Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            _deadline = DateTime.UtcNow.AddMinutes(4);
            EditorApplication.playModeStateChanged += Changed;
            EditorApplication.update += Tick;
            Application.logMessageReceived += Message;
        }

        private static void Message(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception) _errors++;
        }

        private static void Changed(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                EditorApplication.delayCall += Prepare;
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var result = SessionState.GetInt(ResultKey, -1);
                if (result >= 0) Finish(result);
            }
        }

        private static void Prepare()
        {
            try
            {
                // Batch mode has no focused Game View. Keep virtual-device events
                // in the game/UI pipeline without modifying the saved settings.
                _previousInputSettings=InputSystem.settings;
                _smokeInputSettings=UnityEngine.Object.Instantiate(_previousInputSettings);
                InputSystem.settings=_smokeInputSettings;
                _smokeInputSettings.backgroundBehavior=InputSettings.BackgroundBehavior.IgnoreFocus;
                _smokeInputSettings.editorInputBehaviorInPlayMode=InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                _previousBackground=Application.runInBackground;Application.runInBackground=true;
                UnityEngine.Random.InitState(1120);
                _session = BattleSession.Create(null, false, autoStart: true, mode: BattleMode.Uprising);
                // Keep the opponent line-up alive without allowing AI fire to race the staged deaths.
                foreach (var tank in _session.GetComponentsInChildren<BattleTank>())
                {
                    Require(tank.GetComponent<UprisingTank>() != null, "Missing Uprising controller");
                    var ai = tank.GetComponent<AiTankController>();
                    if (ai != null) ai.enabled = false;
                    if (tank != _session.Player) tank.GetComponent<UprisingTank>().enabled = false;
                }
                var controller = _session.Player.GetComponent<UprisingTank>();
                foreach (var id in new[] { "damageBoost2", "reloadBoost2", "engineBoost2" })
                    Require(controller.State.Grant(id), "Passive grant failed: " + id);
                _mouse=InputSystem.AddDevice<Mouse>("UprisingSmokeMouse");_mouse.MakeCurrent();
                _mouseProbe=new GameObject("game-loop mouse probe").AddComponent<UprisingMouseInputProbe>();_mouseProbe.Device=_mouse;
                Require(EventSystem.current.GetComponent<InputSystemUIInputModule>()!=null,"Missing real UI input module");
                _round=0;_perkClicked=false;
                _firstLife = controller.State.LifeSerial;
                _stageAt = Time.time;
                _stage = 1;
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void Tick()
        {
            if (DateTime.UtcNow >= _deadline)
            {
                Fail(new TimeoutException("Uprising Play Mode lifecycle timed out"));
                return;
            }
            if (!EditorApplication.isPlaying || _session == null) return;
            try
            {
                var player = _session.Player;
                var controller = player.GetComponent<UprisingTank>();
                if (_stage == 1 && Time.time - _stageAt >= 5)
                {
                    MouseLock.Set(false);
                    _pointer=new Vector2(Screen.width*.5f,Screen.height*.45f);
                    QueueMouse(true);_stageAt=Time.time;_stage=10;
                }
                else if (_stage == 10 && Time.time - _stageAt >= .15f)
                {
                    var liveHits=new System.Collections.Generic.List<RaycastResult>();
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=_pointer},liveHits);
                    Require(_session.Controls.Surface.PointerCount==1,"Mouse down missed live surface: hit="+(liveHits.Count>0?liveHits[0].gameObject.name:"none")+" mouse="+_mouse.position.ReadValue()+" held="+_mouse.leftButton.isPressed+" enabled="+_mouse.enabled+" focus="+EventSystem.current.isFocused+" cursor="+Cursor.lockState);
                    MouseLock.Set(true);
                    // FixedUpdate has now accumulated a real rewind history, not a synthetic test fixture.
                    if (controller.State.ActiveId == "vortex" || controller.State.ActiveId == "huntersMark" || controller.State.ActiveId == "curse")
                    {
                        var target = _session.Enemies[0];
                        target.SetSpotted(true);
                        typeof(BattleTank).GetProperty("GunTraceTank").SetValue(player, target);
                    }
                    Require(controller.UseActive(), "Active ability failed in Play Mode");
                    Kill(player);
                    Require(_session.Started && controller.State.Respawning, "First death prematurely ended battle");
                    Require(controller.State.Offer.Count == 3, "Missing three-card offer");
                    controller.State.AddElixir(1000);
                    var id = controller.State.Offer[0];
                    var balance = controller.State.Elixir;
                    Require(controller.State.Toggle(id, Time.time, true), "Purchase failed");
                    Require(controller.State.Toggle(id, Time.time, true), "Refund failed");
                    Require(controller.State.Elixir == balance, "Refund changed balance");
                    Require(controller.State.Toggle(id, Time.time, true), "Final purchase failed");
                    BeginPerkInput();
                    _stage = 2;
                }
                else if (_stage == 2 && player.Dead && !_perkClicked)
                {
                    var ui=_session.GetComponentInChildren<UprisingBattleUi>(true);
                    Require(!_session.Controls.CanSpectate,"Pending life enabled spectator");
                    _session.Controls.CycleSpectator(1);
                    Require(_session.View.Tank==player,"Pending life switched camera");
                    CheckPerkMouse(controller,ui);
                }
                else if (_stage == 2 && !player.Dead)
                {
                    Require(_perkClicked,"Perk pointer-click stage was skipped");
                    Require(controller.State.LifeSerial == _firstLife + 2 * (_round + 1), "Wrong respawn life serial");
                    Require(controller.State.RespawnsLeft == 1 - _round, "Wrong remaining lives");
                    Require(player.Health.Health == player.Health.MaxHealth, "Respawn did not restore health");
                    Require(!player.GetComponentInChildren<Original480Wreck>(true).Playing, "Original wreck persisted through respawn");
                    Require(_session.View.Tank == player, "Player camera was not restored");
                    Require(controller.State.Has("damageBoost2"), "Purchased passives were lost");
                    if(_round==0)
                    {
                        Kill(player);controller.State.AddElixir(1000);
                        Require(controller.State.Toggle(controller.State.Offer[0],Time.time,true),"Second-round initial purchase failed");
                        _round=1;_perkClicked=false;BeginPerkInput();QueueMouse(true);_stage=2;
                    }
                    else
                    {
                        Kill(player);Require(!controller.State.Respawning,"Third death incorrectly allows another respawn");
                        Require(_session.Controls.CanSpectate,"Final death did not enable spectator");
                        _session.Controls.CycleSpectator(1);Require(_session.View.Tank!=player,"Final spectator did not resume");
                        _stageAt=Time.time;_stage=4;
                    }
                }
                else if (_stage == 4 && Time.time - _stageAt >= 1)
                {
                    Require(player.TracksSpeed == 0f, "Dead tracks kept scrolling during Play Mode");
                    Require(player.GetComponentInChildren<Original480Wreck>(true).Effects[1].LiveParticles > 0, "Original flaming effect emitted nothing");
                    Require(_errors == 0, "Runtime errors during Uprising lifecycle: " + _errors);
                    Debug.Log("[Blitz] Uprising Play Mode passed: active, InputSystem mouse purchase/refund in both menus, held drag/wheel blocked, spectator gate, two timed respawns, camera, passives, final death; errors=0");
                    ExitPlay(0);
                }
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void QueueMouse(bool held, Vector2 delta=default, Vector2 scroll=default)
        {
            InputSystem.QueueStateEvent(_mouse,new MouseState{position=_pointer,delta=delta,scroll=scroll}.WithButton(MouseButton.Left,held));
        }
        private static void BeginPerkInput()
        {
            _pointerStage=0;_pointerAt=Time.time;
            _menuYaw=_session.View.Yaw;_menuPitch=_session.View.Pitch;_menuLength=_session.View.SpringLength;
        }
        private static void CheckPerkMouse(UprisingTank controller,UprisingBattleUi ui)
        {
            Require(Mathf.Abs(_session.View.Yaw-_menuYaw)<.00001f&&Mathf.Abs(_session.View.Pitch-_menuPitch)<.00001f,"Mouse drag rotated the camera behind perks");
            Require(Mathf.Abs(_session.View.SpringLength-_menuLength)<.00001f,"Mouse wheel zoomed the camera behind perks");
            Require(_session.View.Tank==_session.Player,"Mouse input switched observer behind perks");
            var elapsed=Time.time-_pointerAt;
            if(_pointerStage==0&&elapsed>.2f)
            {
                _pointer+=new Vector2(60,35);QueueMouse(true,new Vector2(60,35),new Vector2(0,120));
                _pointerStage=1;_pointerAt=Time.time;
            }
            else if(_pointerStage==1&&elapsed>.15f)
            {
                Require(_mouseProbe.Held,"Held mouse event did not reach game-loop InputSystem; probe pos="+_mouseProbe.Position);
                Require(!_session.Controls.Surface.InputEnabled&&_session.Controls.Surface.PointerCount==0,"Held world gesture survived modal ownership");
                QueueMouse(false);_pointerStage=2;_pointerAt=Time.time;
            }
            else if(_pointerStage==2&&ui.OfferAlpha>=.99f&&elapsed>.15f)
            {
                Require(!MouseLock.Captured&&Cursor.lockState==CursorLockMode.None,"Perk menu did not release mouse capture");
                var card=ui.transform.Find("respawn/content/ability_panel/card1").GetComponent<Button>();
                Require(card.interactable,"Perk card is not clickable");
                Require(controller.State.Pending.ContainsKey(controller.State.Offer[0]),"Held gesture accidentally toggled the perk");
                Canvas.ForceUpdateCanvases();
                _pointer=RectTransformUtility.WorldToScreenPoint(null,card.transform.TransformPoint(((RectTransform)card.transform).rect.center));
                var hits=new System.Collections.Generic.List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=_pointer},hits);
                Require(hits.Count>0&&hits[0].gameObject==card.gameObject,"Actual perk raycast was intercepted by "+(hits.Count>0?hits[0].gameObject.name:"nothing"));
                _menuBalance=controller.State.Elixir;_menuBalanceAt=Time.time;QueueMouse(false);_pointerStage=3;_pointerAt=Time.time;
            }
            else if(elapsed>.15f&&_pointerStage>=3)
            {
                if(_pointerStage==3)QueueMouse(true);
                else if(_pointerStage==4)QueueMouse(false);
                else if(_pointerStage==5)
                {
                    var id=controller.State.Offer[0];
                    Require(!controller.State.Pending.ContainsKey(id),"InputSystem mouse up did not refund perk");
                    CheckMenuBalance(controller,_menuBalance+controller.State.Cost(id),"refund");
                    QueueMouse(true);
                }
                else if(_pointerStage==6)QueueMouse(false);
                else if(_pointerStage==7)
                {
                    Require(controller.State.Pending.ContainsKey(controller.State.Offer[0]),"InputSystem mouse up did not purchase perk");
                    Require(controller.State.Pending[controller.State.Offer[0]]==controller.State.Cost(controller.State.Offer[0]),"Actual purchase receipt incorrect");
                    CheckMenuBalance(controller,_menuBalance,"purchase");
                    Debug.Log("[Blitz] Perk real InputSystem/EventSystem mouse route passed: round="+_round+" refund/purchase, held drag/wheel blocked; camera unchanged");
                    _perkClicked=true;return;
                }
                _pointerStage++;_pointerAt=Time.time;
            }
        }

        private static void CheckMenuBalance(UprisingTank controller,int expected,string action)
        {
            // Normal battle income continues at one Elixir/second during respawn.
            // Bound that income, without disabling the live gameplay update loop.
            var incomeLimit=Mathf.CeilToInt(Time.time-_menuBalanceAt)+1;
            var actual=controller.State.Elixir;
            Require(actual>=expected&&actual<=expected+incomeLimit,
                "Actual "+action+" balance incorrect: "+actual+", expected "+expected+" plus <="+incomeLimit+" passive income");
        }

        private static void Kill(BattleTank tank)
        {
            for (var attempt = 0; attempt < 2 && !tank.Dead; attempt++)
                tank.ReceiveDamage(new DamageInfo { Dmg = tank.Health.MaxHealth * 10, DmgBase = tank.Health.MaxHealth * 10, DmgType = DamageType.Ability });
            Require(tank.Dead, "Staged lethal damage did not kill tank");
            var wreck=tank.GetComponentInChildren<Original480Wreck>(true);
            Require(wreck != null && wreck.Playing && wreck.Effects.Count == 2, "Real death did not start original 4.8 wreck");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Fail(Exception exception)
        {
            Debug.LogException(exception);
            ExitPlay(1);
        }

        private static void ExitPlay(int result)
        {
            if(_mouseProbe!=null)_mouseProbe.Device=null;
            if(_mouse!=null){InputSystem.RemoveDevice(_mouse);_mouse=null;}
            if(_previousInputSettings!=null){InputSystem.settings=_previousInputSettings;_previousInputSettings=null;}
            if(_smokeInputSettings!=null){UnityEngine.Object.DestroyImmediate(_smokeInputSettings);_smokeInputSettings=null;}
            Application.runInBackground=_previousBackground;
            _session = null;
            SessionState.SetInt(ResultKey, result);
            EditorApplication.update -= Tick;
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            else Finish(result);
        }

        private static void Finish(int result)
        {
            SessionState.SetBool(ActiveKey, false);
            EditorApplication.playModeStateChanged -= Changed;
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= Message;
            _hooked = false;
            EditorApplication.Exit(result);
        }
    }
    /// <summary>Read the player-loop state buffer, not the separate Editor
    /// buffer visible from EditorApplication.update.</summary>
    [DefaultExecutionOrder(100)]
    internal sealed class UprisingMouseInputProbe : MonoBehaviour
    {
        public Mouse Device;
        public bool Held;
        public Vector2 Position;
        private void Update()
        {
            Held=Device!=null&&Device.leftButton.isPressed;
            Position=Device!=null?Device.position.ReadValue():Vector2.zero;
        }
    }

}
