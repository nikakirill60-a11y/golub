using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Active ui_playerstats: summary, reward grid, ten-result history and native navigation.</summary>
    public sealed class AccountStatisticsScreen : MonoBehaviour
    {
        private AccountData _account;
        private Action _close;
        private Action<BattleResultData> _openResult;
        private SourceProfileUi _ui;
        private RectTransform _history;
        private ScrollRect _scroll;
        private Scrollbar _scrollbar;
        public int ActiveTab { get; private set; }
        public int Page { get; private set; }=1;
        public int PageCount => AccountBattleStatistics.PageCount(_account);
        public int VisibleResultCount { get; private set; }
        public SourceProfileUi SourceUi => _ui;
        public FrozenMenuBackdrop Backdrop { get; private set; }
        public static AccountStatisticsScreen Create(Transform parent,AccountData account,Action close,Action<BattleResultData> openResult)
        {
            var go=new GameObject("AccountStatisticsScreen");go.transform.SetParent(parent,false);
            var screen=go.AddComponent<AccountStatisticsScreen>();screen._account=account;screen._close=close;screen._openResult=openResult;screen.Build();return screen;
        }
        public static string Number(double n) => ((long)n).ToString("N0",CultureInfo.InvariantCulture).Replace(","," ");
        private static string Percent(double n,int digits) => Math.Round(n,digits,MidpointRounding.AwayFromZero).ToString("0.0########",CultureInfo.InvariantCulture)+"%";
        private void Build()
        {
            var canvas=BattleFlowUi.CreateCanvas(transform,false,false,135);
            _ui=new SourceProfileUi("profile",canvas);
            var blur=_ui.Nodes["blur"];
            Backdrop=FrozenMenuBackdrop.Attach(blur.gameObject.AddComponent<RawImage>(),canvas.GetComponent<Canvas>());
            _ui.Button("btn_menu").onClick.AddListener(()=>_close?.Invoke());
            var tabs=new[]{"btn_summary","btn_battles","btn_details","btn_vehicles"};
            for(var i=0;i<tabs.Length;i++){var index=i;_ui.Button(tabs[i]).onClick.AddListener(()=>ShowTab(index));}
            var s=AccountBattleStatistics.Calculate(_account);
            Set("rating_pennant/rating",Number(s.Rating));
            Set("GridContainer/grid_elem/battles",Number(s.Battles));
            Set("GridContainer/grid_elem2/victories",Percent(s.WinRate,1));
            Set("GridContainer/grid_elem3/dmg_avg",Number(s.AverageDamage));
            Set("GridContainer/grid_elem4/hit_ratio",Percent(Math.Round(s.AverageHitRatio,3,MidpointRounding.AwayFromZero)*100,4));
            Set("GridContainer2/grid_elem/avg_xp",Number(s.AverageXp));
            Set("GridContainer2/grid_elem2/max_xp",Number(s.MaxXp));
            Set("GridContainer2/grid_elem3/max_kills",Number(s.MaxKills));
            // Godot never updates its authored 12/49. Do not ship fabricated account data.
            Set("GridContainer2/grid_elem4/m_badges","—");
            BuildMedals();
            BuildTeamKillStatus();
            var container=_ui.Nodes["tabs/battles/ScrollContainer"];
            _scroll=container.gameObject.AddComponent<ScrollRect>();_scroll.horizontal=false;_scroll.movementType=ScrollRect.MovementType.Clamped;
            container.gameObject.AddComponent<RectMask2D>();
            var hit=container.gameObject.AddComponent<Image>();hit.color=Color.clear;hit.raycastTarget=true;
            _history=_ui.Nodes["tabs/battles/ScrollContainer/battles_box"];
            _history.anchorMin=new Vector2(0,1);_history.anchorMax=new Vector2(1,1);_history.pivot=new Vector2(0,1);_history.anchoredPosition=Vector2.zero;
            _scroll.content=_history;_scroll.viewport=container;
            _scrollbar=CreateSourceScrollbar(container);
            _scroll.verticalScrollbar=_scrollbar;

            _ui.Button("tabs/battles/bresults_controls/HBoxContainer/btn_last").onClick.AddListener(()=>ShowPage(Page-1));
            _ui.Button("tabs/battles/bresults_controls/HBoxContainer/btn_next").onClick.AddListener(()=>ShowPage(Page+1));
            ShowPage(1);ShowTab(0);
        }
        private void Set(string path,string text)=>_ui.Text("tabs/summary/"+path).text=text;
        /// <summary>Summary footer: blue team-killer status with its expiry, team damage and the strict week.</summary>
        public Text TeamKillStatus { get; private set; }
        private void BuildTeamKillStatus()
        {
            var record=_account.TeamKill;var now=TeamKillPolicy.Now();
            var text=TeamKillText(record,now,BattleFlowUi.LoadLanguage());
            TeamKillStatus=UiFactory.Label("teamkill_status",_ui.Nodes["tabs/summary"],BlitzResources.LoadFont(BlitzResources.RegularFontName),22,
                record.IsBlue(now)?BlitzLegacy.Runtime.Battle.BattleTankPlate.TeamKillerColour:Color.white,TextAnchor.LowerCenter,text);
            UiFactory.AddShadow(TeamKillStatus,new Color(0f,0f,0f,.75f));
            var rt=TeamKillStatus.rectTransform;rt.anchorMin=new Vector2(0,0);rt.anchorMax=new Vector2(1,0);rt.pivot=new Vector2(.5f,0);
            rt.anchoredPosition=new Vector2(0,10);rt.sizeDelta=new Vector2(-40,84);TeamKillStatus.raycastTarget=false;
        }
        public static string Date(double unixSeconds)
        {
            var date=DateTimeOffset.FromUnixTimeSeconds((long)Math.Max(0,Math.Min(unixSeconds,253402300799))).ToLocalTime();
            return date.ToString("dd.MM.yyyy HH:mm",CultureInfo.InvariantCulture);
        }
        public static string TeamKillText(TeamKillRecord record,double now,LocalizationTable lang)
        {
            var status=record.Permanent?lang.Get("profile:teamkill/blue_forever")
                :record.IsBlue(now)?lang.Get("profile:teamkill/blue_until").Replace("{0}",Date(record.BlueUntil))
                :lang.Get("profile:teamkill/clean");
            var lines=status+"\n"+lang.Get("profile:teamkill/damage").Replace("{0}",Number(record.Total))
                .Replace("{1}",Number(record.Pending)).Replace("{2}",Number(TeamKillPolicy.BlueThreshold));
            if(record.Watched(now))lines+="\n"+lang.Get("profile:teamkill/watched_until").Replace("{0}",Date(record.WatchUntil))
                .Replace("{1}",TeamKillPolicy.KickBattleDamage.ToString(CultureInfo.InvariantCulture));
            return lines;
        }
        public void ShowTab(int tab)
        {
            ActiveTab=Mathf.Clamp(tab,0,3);var names=new[]{"summary","battles","stats","tanks"};var buttons=new[]{"btn_summary","btn_battles","btn_details","btn_vehicles"};
            _ui.Nodes["tabs"].gameObject.SetActive(true);
            for(var i=0;i<4;i++)
            {
                _ui.Nodes["tabs/"+names[i]].gameObject.SetActive(i==ActiveTab);
                _ui.Button(buttons[i]).GetComponent<Image>().color=i==ActiveTab?new Color(.65206665f,.9211909f,.96818036f,.10980392f):Color.clear;
            }
        }
        public void ShowPage(int page)
        {
            Page=Mathf.Clamp(page,1,PageCount);
            for(var i=_history.childCount-1;i>=0;i--){var child=_history.GetChild(i);child.gameObject.SetActive(false);if(Application.isPlaying)Destroy(child.gameObject);else DestroyImmediate(child.gameObject);}
            var prefix="tabs/battles/bresults_controls/HBoxContainer/";
            _ui.Text(prefix+"folders").text=Page+"/"+PageCount;
            _ui.Button(prefix+"btn_last").interactable=Page>1;_ui.Button(prefix+"btn_next").interactable=Page<PageCount;
            var db=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));var lang=BattleFlowUi.LoadLanguage();
            var rows=AccountBattleStatistics.RecentPage(_account,Page);VisibleResultCount=rows.Count;
            for(var i=0;i<rows.Count;i++)
            {
                var result=rows[i];var row=new SourceProfileUi("history",_history);
                row.Root.anchorMin=new Vector2(0,1);row.Root.anchorMax=new Vector2(1,1);row.Root.pivot=new Vector2(0,1);row.Root.anchoredPosition=new Vector2(0,-i*144.85f);row.Root.sizeDelta=new Vector2(0,140.85f);
                row.Text("received/credits").text=Number(result.CreditsEarned);row.Text("received/xp").text=Number(result.Xp);row.Text("received/gold").text=Number(result.Gold);
                var outcome=result.Outcome==BattleOutcome.Victory?"victory":result.Outcome==BattleOutcome.Defeat?"defeat":"draw";
                row.Text("status").text=lang.Get(outcome=="victory"?"battle_notif:victory":outcome=="defeat"?"battle_notif:defeat":"battle_notif:draw");
                if(outcome!="victory")row.Image("status_icon").sprite=BattleFlowUi.LoadAtlas().Get("profile_status_"+outcome);
                var map="Unknown map";
                try { var definition=BlitzLegacy.Runtime.Battle.BattleWorld.LoadDefinition(result.MapId);map=lang.Get(definition.NameKey); } catch(Exception){}
                var date=DateTimeOffset.FromUnixTimeSeconds((long)result.FinishedAt).UtcDateTime;
                row.Text("battle_info").text=map+" "+date.Day+"/"+date.Month+"/"+date.Year+" "+date.Hour+":"+date.Minute+":"+date.Second;
                var tank=db.Get(result.TankId);row.Text("tank_name").text=tank==null?(result.Player?.TankName??result.TankId):lang.GetTankName(tank);
                row.Button("btn").onClick.AddListener(()=>_openResult?.Invoke(result));
            }
            _history.sizeDelta=new Vector2(0,Mathf.Max(0,rows.Count*144.85f-4));_scroll.verticalNormalizedPosition=1;
            Canvas.ForceUpdateCanvases();
            var needsScroll=_history.rect.height>_scroll.viewport.rect.height;
            _scrollbar.gameObject.SetActive(needsScroll);
            _history.sizeDelta=new Vector2(needsScroll?-12:0,_history.sizeDelta.y);
        }
        private static Sprite _rounded;
        private static Sprite Rounded()
        {
            if(_rounded!=null)return _rounded;
            var texture=new Texture2D(34,34,TextureFormat.RGBA32,false);texture.hideFlags=HideFlags.HideAndDontSave;texture.wrapMode=TextureWrapMode.Clamp;
            for(var y=0;y<34;y++)for(var x=0;x<34;x++)
            {
                var nearest=new Vector2(Mathf.Clamp(x+.5f,16,18),Mathf.Clamp(y+.5f,16,18));
                var distance=Vector2.Distance(new Vector2(x+.5f,y+.5f),nearest);
                texture.SetPixel(x,y,new Color(1,1,1,Mathf.Clamp01(16.5f-distance)));
            }
            texture.Apply();_rounded=Sprite.Create(texture,new Rect(0,0,34,34),new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect,new Vector4(16,16,16,16));_rounded.hideFlags=HideFlags.HideAndDontSave;return _rounded;
        }
        private Scrollbar CreateSourceScrollbar(RectTransform container)
        {
            var rt=UiFactory.Node("source_vertical_scrollbar",container);rt.anchorMin=new Vector2(1,0);rt.anchorMax=new Vector2(1,1);rt.pivot=new Vector2(1,.5f);rt.sizeDelta=new Vector2(8,0);rt.anchoredPosition=Vector2.zero;
            var background=rt.gameObject.AddComponent<Image>();background.sprite=Rounded();background.type=Image.Type.Sliced;background.color=new Color(.1f,.1f,.1f,.6f);background.raycastTarget=true;
            var bar=rt.gameObject.AddComponent<Scrollbar>();bar.direction=Scrollbar.Direction.BottomToTop;
            var handle=UiFactory.Picture("grabber",rt,Rounded());handle.type=Image.Type.Sliced;handle.raycastTarget=true;GodotRect.FullRect(handle.rectTransform);handle.color=Color.white;
            bar.handleRect=handle.rectTransform;bar.targetGraphic=handle;
            var colors=bar.colors;colors.normalColor=new Color(1,1,1,.4f);colors.highlightedColor=new Color(1,1,1,.75f);colors.pressedColor=new Color(.75f,.75f,.75f,.75f);colors.selectedColor=colors.normalColor;colors.fadeDuration=0;bar.colors=colors;
            return bar;
        }
        private void BuildMedals()
        {
            var atlas=BattleFlowUi.LoadAtlas();var box=_ui.Nodes["tabs/summary/medals"];
            var ids=new List<string>();foreach(var pair in _account.Rewards)if(pair.Value>0 && MedalInfoPopup.Definition(pair.Key)["rare"].AsBool())ids.Add(pair.Key);
            foreach(var pair in _account.Rewards)if(pair.Value>0 && !MedalInfoPopup.Definition(pair.Key)["rare"].AsBool())ids.Add(pair.Key);
            var widths=new float[9];var heights=new float[(ids.Count+8)/9];
            for(var i=0;i<ids.Count;i++){var sprite=atlas.Get(MedalInfoPopup.Region(ids[i]));if(sprite==null)continue;widths[i%9]=Mathf.Max(widths[i%9],sprite.rect.width+10);heights[i/9]=Mathf.Max(heights[i/9],sprite.rect.height);}
            float total=0;foreach(var width in widths)if(width>0)total+=width+4;total=Mathf.Max(0,total-4);
            box.anchorMin=box.anchorMax=new Vector2(.5f,1);box.pivot=new Vector2(.5f,1);box.anchoredPosition=new Vector2(0,-408);box.sizeDelta=new Vector2(total,0);
            float x=0,y=0;
            for(var i=0;i<ids.Count;i++)
            {
                if(i%9==0){x=0;if(i>0)y+=heights[i/9-1]+4;}
                var id=ids[i];var cell=UiFactory.Node("medal_"+id,box);BattleFlowUi.Place(cell,x,y,widths[i%9],heights[i/9]);
                var sprite=atlas.Get(MedalInfoPopup.Region(id));UiFactory.KeepCentered(cell,sprite);
                var button=cell.gameObject.AddComponent<Button>();var plate=cell.gameObject.AddComponent<Image>();plate.color=Color.clear;plate.raycastTarget=true;button.targetGraphic=plate;button.onClick.AddListener(()=>MedalInfoPopup.Open(transform,id));
                var badge=BattleFlowUi.Art("series",cell,atlas,"profile_medal_number",96,110,100,47);badge.rectTransform.localScale=Vector3.one*.605f;
                BattleFlowUi.Label("series_counter",badge.transform,BlitzResources.LoadFont(BlitzResources.BoldFontName),38,Color.white,TextAnchor.UpperCenter,_account.Rewards[id].ToString(),11.900803f,0,75.702487f,39);
                x+=widths[i%9]+4;
            }
        }
        private void Update(){if(MedalInfoPopup.EscapeConsumedFrame!=Time.frameCount && GetComponentInChildren<MedalInfoPopup>()==null && Keyboard.current!=null && Keyboard.current.escapeKey.wasPressedThisFrame)_close?.Invoke();}
    }
}
