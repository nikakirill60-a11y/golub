using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    public sealed class BattleModeScreen : MonoBehaviour
    {
        /// <summary>Height of the top band with the back arrow and the title.</summary>
        public const float HeaderHeight = 80f;

        private Action<BattleMode> _picked;
        private Action<bool> _onlineChanged;
        private bool _onlineSelected;
        private readonly System.Collections.Generic.List<Sprite> _sprites = new System.Collections.Generic.List<Sprite>();
        /// <param name="onlineChanged">When set, a globe toggles online matchmaking
        /// independently of the selected battle mode.</param>
        /// <param name="kit">The hangar's widget kit for the back arrow; null
        /// builds one from the shared atlas (tests, render tools).</param>
        public static BattleModeScreen Create(Transform parent, BattleMode selected, Action<BattleMode> picked,
            bool onlineSelected = false, Action<bool> onlineChanged = null, HangarUiKit kit = null)
        {
            var root=UiFactory.Node("BattleModeScreen",parent); GodotRect.FullRect(root);
            var c=root.gameObject.AddComponent<BattleModeScreen>(); c._picked=picked;c._onlineChanged=onlineChanged;c._onlineSelected=onlineSelected;
            var canvas=root.gameObject.AddComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=80;
            var scaler=root.gameObject.AddComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1196,540);scaler.matchWidthOrHeight=.5f;
            root.gameObject.AddComponent<GraphicRaycaster>();
            var lang=LocalizationTable.Parse(BlitzResources.LoadText("lang"));lang.Language=BlitzBootstrap.Current?.Settings.Language ?? "en";
            var bold=Resources.Load<Font>("Fonts/WarHeliosCondCBold"); var regular=Resources.Load<Font>("Uprising/menu_default");
            ColorBlock(root,"background",new Color(.027f,.043f,.059f,1),0,0,1,1,0,0,0,0);root.Find("background").GetComponent<Image>().raycastTarget=true;
            ColorBlock(root,"background_glow",new Color(.08f,.19f,.27f,.34f),0,0,1,0,0,0,0,196);
            ColorBlock(root,"top_bar",new Color(.015f,.024f,.035f,.84f),0,0,1,0,0,0,0,80);
            var header=UiFactory.Label("header",root,bold,26,new Color(.96f,.97f,1),TextAnchor.MiddleCenter,lang.Get("battle_mode:menu/title"));
            GodotRect.Apply(header.rectTransform,0,0,1,0,172,0,-172,80);UiFactory.AddShadow(header,new Color(0,0,0,.75f),new Vector2(1,2));
            BuildBack(root,kit ?? FallbackKit(lang),MenuStyle.InMenus,c);
            if(onlineChanged!=null)
            {
                var globe=UiFactory.TextButton("online_globe",root,null,bold,16,string.Empty);GodotRect.Apply((RectTransform)globe.transform,1,0,1,0,-248,16,-24,64);
                var globeIcon=UiFactory.Picture("icon",globe.transform,c.LoadSprite("online_globe"));globeIcon.raycastTarget=false;GodotRect.TopLeft(globeIcon.rectTransform,10,6,46,42);
                var globeText=UiFactory.Label("state",globe.transform,bold,15,Color.white,TextAnchor.MiddleCenter,string.Empty);GodotRect.TopLeft(globeText.rectTransform,48,0,216,48);
                Action refreshOnline=()=>{var on=c._onlineSelected;globe.GetComponent<Image>().color=on?new Color(.16f,.48f,.72f,.95f):new Color(.12f,.16f,.2f,.95f);globeText.text=lang.Get(on?"online:toggle/on":"online:toggle/off");};
                globe.onClick.AddListener(()=>{c._onlineSelected=!c._onlineSelected;c._onlineChanged(c._onlineSelected);refreshOnline();});refreshOnline();
            }
            ColorBlock(root,"top_divider",new Color(.23f,.52f,.72f,.8f),0,0,1,0,0,79,0,81);
            var modes=BattleModeCatalog.MenuModes;
            var cardCount=modes.Length;
            // Use the same card footprint for every mode; crop artwork inside the card.
            var viewport=UiFactory.Node("mode_viewport",root);GodotRect.Apply(viewport,0,0,1,1,24,100,-24,-20);viewport.gameObject.AddComponent<RectMask2D>();
            var viewportHit=viewport.gameObject.AddComponent<Image>();viewportHit.color=Color.clear;
            var content=UiFactory.Node("mode_cards",viewport);content.anchorMin=content.anchorMax=new Vector2(0,.5f);content.pivot=new Vector2(0,.5f);content.anchoredPosition=Vector2.zero;content.sizeDelta=new Vector2(cardCount*210f+(cardCount-1)*14f,340);
            var scroll=viewport.gameObject.AddComponent<ScrollRect>();scroll.viewport=viewport;scroll.content=content;scroll.horizontal=true;scroll.vertical=false;scroll.movementType=ScrollRect.MovementType.Clamped;
            var cursor=0f;
            for(var i=0;i<cardCount;i++)
            {
                var mode=modes[i]; var id=BattleModeCatalog.Id(mode);
                var chosen=selected==mode;
                const float cardWidth=210f;const float cardHeight=340f;
                var card=UiFactory.TextButton("card_"+id,content,null,null,0,null);
                var left=cursor;cursor+=cardWidth+14;
                GodotRect.TopLeft((RectTransform)card.transform,left,(340-cardHeight)*.5f,left+cardWidth,(340+cardHeight)*.5f);
                card.GetComponent<Image>().color=Color.clear;
                var texture=(mode==BattleMode.BigBoss?Resources.Load<Texture2D>("BigBoss/Textures/battle-type_bossmode"):null) ?? (mode==BattleMode.WorldOfDucks?Resources.Load<Texture2D>("Textures/WorldOfDucks/mode_card"):null) ?? Resources.Load<Texture2D>("Uprising/Modes/"+id) ?? Resources.Load<Texture2D>("Uprising/Modes/normal");
                if(texture==null) continue;
                var sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);c._sprites.Add(sprite);
                var art=UiFactory.Picture("mode_art",card.transform,sprite);GodotRect.FullRect(art.rectTransform);art.preserveAspect=false;
                card.gameObject.AddComponent<RectMask2D>();var aspect=art.gameObject.AddComponent<AspectRatioFitter>();aspect.aspectMode=AspectRatioFitter.AspectMode.EnvelopeParent;aspect.aspectRatio=(float)texture.width/texture.height;
                ColorBlock((RectTransform)card.transform,"art_tint",new Color(.02f,.04f,.06f,.08f),0,0,1,1,0,0,0,0);
                ColorBlock((RectTransform)card.transform,"bottom_shade",new Color(.008f,.014f,.022f,.9f),0,1,1,1,0,-140,0,0);
                var title=UiFactory.Label("mode_name",card.transform,bold,20,Color.white,TextAnchor.MiddleLeft,lang.Get(BattleModeCatalog.NameKey(mode)));
                GodotRect.TopLeft(title.rectTransform,12,208,cardWidth-12,242);var outline=title.gameObject.AddComponent<Outline>();outline.effectColor=new Color(0,0,0,.9f);outline.effectDistance=new Vector2(3,-3);
                var desc=UiFactory.Label("mode_desc",card.transform,regular,12,Color.white,TextAnchor.UpperLeft,lang.Get(BattleModeCatalog.DescriptionKey(mode)));
                desc.horizontalOverflow=HorizontalWrapMode.Wrap;desc.color=new Color(.88f,.91f,.95f);GodotRect.TopLeft(desc.rectTransform,12,242,cardWidth-12,328);
                if(!BattleModeCatalog.AllowsOnline(mode))
                {
                    // Offline-only modes (the Sandbox) say so on the card: the
                    // online toggle never applies to them.
                    ColorBlock((RectTransform)card.transform,"offline_badge",new Color(.008f,.014f,.022f,.82f),0,0,0,0,10,10,128,36);
                    var badge=UiFactory.Label("offline_only",card.transform,bold,13,new Color(.96f,.61f,.15f),TextAnchor.MiddleCenter,lang.Get("training:offline_only"));
                    GodotRect.TopLeft(badge.rectTransform,10,10,128,36);
                }
                var focusRoot=UiFactory.Node("focus_outline",card.transform);GodotRect.FullRect(focusRoot);Border(focusRoot,new Color(.54f,.78f,1,.95f),2);
                focusRoot.gameObject.SetActive(false);
                // Four explicit strips: Outline on a transparent quad does not draw a border.
                if(chosen) Border((RectTransform)card.transform,new Color(.96f,.61f,.15f),4);
                var focus=card.gameObject.AddComponent<ModeCardFocus>();focus.Initialize(focusRoot.gameObject,chosen);
                card.onClick.AddListener(()=>{c._picked?.Invoke(mode);c.Close();});
                if(chosen && EventSystem.current!=null) EventSystem.current.SetSelectedGameObject(card.gameObject);
            }
            Canvas.ForceUpdateCanvases();scroll.horizontalNormalizedPosition=Mathf.Clamp01((float)Array.IndexOf(modes,selected)/Mathf.Max(1,cardCount-1));
            if(MenuStyle.InMenus) Modernize(root);
            return c;
        }

        /// <summary>
        /// Modern style: the picker keeps its mode art and layout and takes
        /// the Modern palette - glass bands and the amber accent line.
        /// </summary>
        private static void Modernize(RectTransform root)
        {
            Tint(root, "background", new Color(0.02f, 0.025f, 0.035f, 1f));
            Tint(root, "background_glow", new Color(1f, 0.69f, 0.125f, 0.06f));
            Tint(root, "top_bar", ModernTheme.GlassStrong);
            Tint(root, "top_divider", ModernTheme.Accent);
        }

        /// <summary>
        /// The back arrow of the shop and the container results instead of a
        /// CLOSE text button: the hangar burger's button in the active style,
        /// at its left inset and centred in the header band.
        /// </summary>
        private static Button BuildBack(RectTransform root, HangarUiKit kit, bool modern, BattleModeScreen screen)
        {
            var back = kit.BackButton(root, modern, 50f);
            var x = modern ? HangarUiKit.ModernTopButtonX : HangarUiKit.ClassicBackButtonX;
            var width = modern ? HangarUiKit.ModernTopButtonWidth : HangarUiKit.ClassicBackButtonWidth;
            var height = modern ? HangarUiKit.ModernTopButtonHeight : HangarUiKit.ClassicBackButtonHeight;
            HangarUiKit.Place((RectTransform)back.transform, x, (HeaderHeight - height) * 0.5f, width, height);
            kit.Bind(back, screen.Close, AudioService.SoundEnter);
            return back;
        }

        private static HangarUiKit FallbackKit(LocalizationTable lang)
        {
            var atlas = MenuStyle.ApplyForMenus(
                AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData)));
            var bootstrap = BlitzBootstrap.Current;
            return new HangarUiKit(atlas, lang,
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                bootstrap == null ? null : bootstrap.Audio);
        }

        /// <summary>Back arrow, Escape and a picked mode all leave the picker.</summary>
        public void Close()
        {
            if (Application.isPlaying) { Destroy(gameObject); }
            else { DestroyImmediate(gameObject); }
        }

        private static void Tint(RectTransform root, string name, Color color)
        {
            var node = root.Find(name);
            var image = node == null ? null : node.GetComponent<Image>();
            if (image != null) { image.color = color; }
        }
        private Sprite LoadSprite(string id){var texture=Resources.Load<Texture2D>("Uprising/Icons/"+id);var sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);_sprites.Add(sprite);return sprite;}
        public sealed class ModeCardFocus : MonoBehaviour,IPointerEnterHandler,IPointerExitHandler,ISelectHandler,IDeselectHandler
        {
            private Graphic[] _graphics;private Color[] _colors;private GameObject _outline;private bool _chosen,_hover,_focus;
            public void Initialize(GameObject outline,bool chosen){_outline=outline;_chosen=chosen;_graphics=GetComponentsInChildren<Graphic>(true);_colors=new Color[_graphics.Length];for(var i=0;i<_graphics.Length;i++)_colors[i]=_graphics[i].color;Refresh();}
            private void Refresh(){if(_outline==null)return;var focused=_focus||_hover;_outline.SetActive(focused);var tint=_chosen||focused?Color.white:new Color(.82f,.84f,.88f);for(var i=0;i<_graphics.Length;i++)_graphics[i].color=_colors[i]*tint;}
            public void OnPointerEnter(PointerEventData e){_hover=true;Refresh();}public void OnPointerExit(PointerEventData e){_hover=false;Refresh();}
            public void OnSelect(BaseEventData e){_focus=true;Refresh();}public void OnDeselect(BaseEventData e){_focus=false;Refresh();}
        }
        public static void Border(RectTransform root,Color color,float width)
        {
            ColorBlock(root,"borderTop",color,0,0,1,0,0,0,0,width);
            ColorBlock(root,"borderBottom",color,0,1,1,1,0,-width,0,0);
            ColorBlock(root,"borderLeft",color,0,0,0,1,0,0,width,0);
            ColorBlock(root,"borderRight",color,1,0,1,1,-width,0,0,0);
        }
        private static void ColorBlock(RectTransform p,string n,Color c,float al,float at,float ar,float ab,float l,float t,float r,float b)
        {var image=UiFactory.Picture(n,p,null);image.color=c;GodotRect.Apply(image.rectTransform,al,at,ar,ab,l,t,r,b);}
        private void Update(){if(Keyboard.current!=null && Keyboard.current.escapeKey.wasPressedThisFrame)Close();}
        private void OnDestroy(){foreach(var s in _sprites)if(s!=null)Destroy(s);}
    }
}
