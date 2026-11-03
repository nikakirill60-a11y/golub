using System.Collections.Generic;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Platform;
using BlitzLegacy.Core.Battle;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    public sealed class UprisingBattleUi : MonoBehaviour
    {
        public const int SortingOrder = BattleHud.SortingOrder + 3;
        private UprisingTank _owner;
        private RectTransform _offer,_content;
        private readonly Image[] _lifeIcons=new Image[3];
        private bool _oldCaptured;private bool _offering;
        /// <summary>The perk offer is the content of the shared respawn presentation.</summary>
        public static readonly Vector2 OfferSize = new Vector2(620, 444);
        private BattleRespawnFrame _frame;
        public float OfferAlpha { get { return _frame != null ? _frame.Alpha : 0; } }
        private Text _header, _balance, _lives, _seconds;
        private readonly Button[] _cards=new Button[3];
        private readonly Text[] _titles=new Text[3],_descriptions=new Text[3],_costs=new Text[3],_rarities=new Text[3];
        private readonly Image[] _icons=new Image[3],_selected=new Image[3];
        private readonly Dictionary<string,Sprite> _sprites=new Dictionary<string,Sprite>();
        private LocalizationTable _lang;
        private Font _font;
        public static UprisingBattleUi Create(Transform parent,UprisingTank owner)
        {
            var root=UiFactory.Node("UprisingBattleUi",parent);GodotRect.FullRect(root);
            var c=root.gameObject.AddComponent<UprisingBattleUi>();c._owner=owner;
            c._lang=LocalizationTable.Parse(BlitzResources.LoadText("lang"));c._lang.Language=BlitzBootstrap.Current?.Settings.Language??"en";
            c._font=Resources.Load<Font>("Uprising/arialmb");
            // HUD is intentionally non-interactive and below the controls canvas.
            // Give the modal its own raycaster above the fullscreen drag surface.
            c._frame=BattleRespawnFrame.Create(root,"respawn",SortingOrder);
            c._content=c._frame.Content;GodotRect.Centered(c._content,-OfferSize.x/2,-OfferSize.y/2,OfferSize.x/2,OfferSize.y/2);
            c._offer=UiFactory.Node("ability_panel",c._content);GodotRect.FullRect(c._offer);
            c._header=c.Label("header",c._offer,19,c._lang.Get("uprising:abilities/pick"));GodotRect.Centered(c._header.rectTransform,-292,-84,292,-56);
            for(var i=0;i<3;i++)
            {
                var slot=i;
                var card=UiFactory.TextButton("card"+(i+1),c._offer,c.Sprite("card"),null,0,null);
                card.GetComponent<Image>().type=Image.Type.Sliced;c._cards[i]=card;GodotRect.Centered((RectTransform)card.transform,-292+i*200,-48,-108+i*200,206);
                card.onClick.AddListener(()=>c.Pick(slot));
                c._selected[i]=UiFactory.Picture("selected",card.transform,c.Sprite("selected"));GodotRect.FullRect(c._selected[i].rectTransform);c._selected[i].type=Image.Type.Sliced;
                c._rarities[i]=c.Label("rarity",card.transform,13,"");GodotRect.TopLeft(c._rarities[i].rectTransform,8,6,176,26);
                c._icons[i]=UiFactory.Picture("icon",card.transform,null);c._icons[i].preserveAspect=true;GodotRect.TopLeft(c._icons[i].rectTransform,53,28,131,106);
                c._titles[i]=c.Label("title",card.transform,17,"");GodotRect.TopLeft(c._titles[i].rectTransform,8,108,176,154);Fit(c._titles[i],17,12);
                c._descriptions[i]=c.Label("desc",card.transform,12,"");c._descriptions[i].color=new Color(.78f,.78f,.78f);GodotRect.TopLeft(c._descriptions[i].rectTransform,8,158,176,222);Fit(c._descriptions[i],12,9);
                c._costs[i]=c.Label("cost",card.transform,19,"");GodotRect.TopLeft(c._costs[i].rectTransform,8,228,148,250);
                var elixir=UiFactory.Picture("cost_icon",card.transform,c.Sprite("elixir"));GodotRect.TopLeft(elixir.rectTransform,152,228,174,250);
            }
            var balanceIcon=UiFactory.Picture("elixir_icon",root,c.Sprite("elixir"));GodotRect.Apply(balanceIcon.rectTransform,0,.5f,0,.5f,16,-13,42,13);
            c._balance=c.Label("elixir",root,22,"");c._balance.alignment=TextAnchor.MiddleLeft;GodotRect.Apply(c._balance.rectTransform,0,.5f,0,.5f,48,-14,180,14);
            for(var i=0;i<3;i++){c._lifeIcons[i]=UiFactory.Picture("life"+i,root,c.Sprite("lives"));GodotRect.TopLeft(c._lifeIcons[i].rectTransform,16+i*30,14,42+i*30,40);}
            c._lives=UiFactory.Label("caption",c._content,BlitzResources.LoadFont(BlitzResources.BoldFontName),24,new Color(.7094448f,.9999996f,.41735148f),TextAnchor.MiddleCenter,c._lang.Get("uprising:respawn/await"));GodotRect.Centered(c._lives.rectTransform,-220,-196,220,-168);
            c._seconds=UiFactory.Label("time",c._content,BlitzResources.LoadFont(BlitzResources.BoldFontName),60,new Color(.8219557f,1,.6715396f),TextAnchor.MiddleCenter,"");GodotRect.Centered(c._seconds.rectTransform,-110,-166,110,-94);
            c.Refresh();return c;
        }
        private static void Fit(Text t,int max,int min) {t.horizontalOverflow=HorizontalWrapMode.Wrap;t.verticalOverflow=VerticalWrapMode.Truncate;t.resizeTextForBestFit=true;t.resizeTextMinSize=min;t.resizeTextMaxSize=max;}
        private Text Label(string name,Transform parent,int size,string text){return UiFactory.Label(name,parent,_font,size,Color.white,TextAnchor.MiddleCenter,text);}
        private Sprite Sprite(string id)
        {
            Sprite sprite;if(_sprites.TryGetValue(id,out sprite))return sprite;
            var texture=Resources.Load<Texture2D>("Uprising/Icons/"+id);if(texture==null)return null;
            if(id!="card" && id!="selected") sprite=UnityEngine.Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);
            else sprite=UnityEngine.Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect,id=="selected"?new Vector4(12,12,12,12):Vector4.zero);
            _sprites[id]=sprite;return sprite;
        }
        private void Pick(int i){if(OfferAlpha<.99f)return;if(i<_owner.State.Offer.Count && _owner.State.Offer[i]!="")_owner.Toggle(_owner.State.Offer[i]);Refresh();}
        private void Refresh()
        {
            if(_owner==null)return;var s=_owner.State;var ongoing=_owner.Session.Started;
            if(_owner.Tank.Dead && s.RespawnsLeft==0){gameObject.SetActive(false);return;}
            var offering=s.Respawning && ongoing;
            if(offering && !_offering)_oldCaptured=MouseLock.Captured;
            if(offering)MouseLock.Set(false);
            else if(!offering && _offering && ongoing)
            {
                var anotherMenu=_owner.Session.SettingsUi!=null || (_owner.Session.PauseMenu!=null && _owner.Session.PauseMenu.Visible);
                MouseLock.Set(_oldCaptured && !anotherMenu);
            }
            _offering=offering;_frame.Present(offering,Time.time-(s.RespawnAt-UprisingState.RespawnDelay),OfferSize,true);
            _offer.gameObject.SetActive(offering);
            _lives.gameObject.SetActive(s.Respawning && ongoing);
            _seconds.text=Mathf.Max(1,Mathf.CeilToInt((float)(s.RespawnAt-Time.time))).ToString();
            var remaining=s.RespawnsLeft+(_owner.Tank.Dead?0:1);for(var i=0;i<3;i++)_lifeIcons[i].color=new Color(1,1,1,i<remaining?1:.22f);
            _balance.text=s.Elixir.ToString();
            for(var i=0;i<3;i++)
            {
                var id=i<s.Offer.Count?s.Offer[i]:"";_cards[i].gameObject.SetActive(id!="");if(id=="")continue;
                var selected=s.Pending.ContainsKey(id);
                _selected[i].gameObject.SetActive(selected);_cards[i].interactable=_frame.Group.interactable && ongoing && Time.time<s.RespawnAt && (selected || s.CanBuy(id));
                var group=_cards[i].GetComponent<CanvasGroup>();if(group==null)group=_cards[i].gameObject.AddComponent<CanvasGroup>();group.alpha=_cards[i].interactable?1:.55f;
                var nameKey="uprising:ability/"+id+"/name"; var descKey="uprising:ability/"+id+"/desc"; var rarityKey="uprising:rarity/"+s.Catalog.Rarity(id);
                _titles[i].text=_lang.Get(nameKey);_descriptions[i].text=_lang.Get(descKey);
                _rarities[i].text=_lang.Get(rarityKey);_costs[i].text=s.Cost(id).ToString();_icons[i].sprite=Sprite(id);
            }
        }
        private void Update()
        {
            Refresh();var k=Keyboard.current;if(k==null)return;
            // The active is the HUD's mode slot: BattleControls presses it, once, for Digit7.
            if(_offering){if(OfferAlpha<.99f)return;if(k.digit1Key.wasPressedThisFrame)Pick(0);if(k.digit2Key.wasPressedThisFrame)Pick(1);if(k.digit3Key.wasPressedThisFrame)Pick(2);}
        }
        private static void Release(UnityEngine.Object value){if(value==null)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        private void OnDestroy(){foreach(var s in _sprites.Values)Release(s);}
    }
}
