using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Resolved native Godot Control geometry; only active scene nodes, no reference mockups.</summary>
    public sealed class SourceProfileUi
    {
        public readonly Dictionary<string, RectTransform> Nodes = new Dictionary<string, RectTransform>();
        private readonly AtlasRegionTable _atlas = MenuStyle.ApplyForMenus(BattleFlowUi.LoadAtlas());
        private readonly BlitzLegacy.Core.Localization.LocalizationTable _lang = BattleFlowUi.LoadLanguage();
        public RectTransform Root { get; private set; }
        private static JsonNode _scenes;
        public static JsonNode Scene(string id) { if (_scenes == null) _scenes=JsonNode.Parse(BlitzResources.LoadText("profile_source_manifest"))["scenes"]; return _scenes[id]; }
        public SourceProfileUi(string scene, Transform parent, bool prototypes = false)
        {
            Root = Build(Scene(scene), parent, new Vector2(1387,733), prototypes);
            if (scene != "history") GodotRect.FullRect(Root);
            Root.gameObject.SetActive(true);
        }
        private RectTransform Build(JsonNode n, Transform parent, Vector2 parentSize, bool prototypes)
        {
            var name = n["name"].AsString(); var type = n["type"].AsString();
            RectTransform rt;
            if (n["path"].AsString() == "blur" && type == "ColorRect")
                rt = UiFactory.Node(name,parent);
            else if (type == "Button" && n["variation"].AsString() == "ButtonSmallGeneric")
                rt = (RectTransform)BattleFlowUi.Button(name,parent,_atlas,"button_small_generic",BlitzResources.LoadFont(BlitzResources.BoldFontName),15,"",0,0,1,1).transform;
            else rt = UiFactory.Node(name,parent);
            Nodes[n["path"].AsString()] = rt;
            var pos = V(n["position"]); var size = V(n["size"]); var anchors = n["anchors"];
            // Use native minimum-size/grow resolution, not authored offsets alone.
            // Box/Grid children are already resolved by Godot's actual container pass.
            if (n["layoutMode"].AsInt() == 2) BattleFlowUi.Place(rt,pos.x,pos.y,size.x,size.y);
            else
            {
                rt.anchorMin = new Vector2(anchors[0].AsFloat(),1-anchors[3].AsFloat());
                rt.anchorMax = new Vector2(anchors[2].AsFloat(),1-anchors[1].AsFloat());
                rt.pivot = new Vector2(0,1);
                rt.offsetMin = new Vector2(pos.x-anchors[0].AsFloat()*parentSize.x,-pos.y-size.y+anchors[3].AsFloat()*parentSize.y);
                rt.offsetMax = new Vector2(pos.x+size.x-anchors[2].AsFloat()*parentSize.x,-pos.y+anchors[1].AsFloat()*parentSize.y);
            }
            var pivot=V(n["pivot"]);
            if (pivot.sqrMagnitude > 0 && size.x != 0 && size.y != 0)
            {
                var local=rt.localPosition; var original=rt.pivot;
                rt.pivot = new Vector2(pivot.x/size.x,1-pivot.y/size.y);
                rt.localPosition = local + new Vector3((rt.pivot.x-original.x)*size.x,(rt.pivot.y-original.y)*size.y,0);
            }
            rt.localScale = new Vector3(n["scale"][0].AsFloat(1),n["scale"][1].AsFloat(1),1);
            Graphic graphic = null;
            if (type == "Label")
            {
                var text=rt.gameObject.AddComponent<Text>();
                text.font=BlitzResources.LoadFont(n["font"].AsString().Contains("Bold")?BlitzResources.BoldFontName:BlitzResources.RegularFontName);
                text.fontSize=n["fontSize"].AsInt(20);
                text.text=_lang.Get(n["text"].AsString());
                text.color=C(n["color"]); text.raycastTarget=false;
                text.horizontalOverflow=(n["wrap"].AsBool() || n["wrap"].AsInt()>0)?HorizontalWrapMode.Wrap:HorizontalWrapMode.Overflow;
                text.verticalOverflow=VerticalWrapMode.Overflow;
                text.alignment=(TextAnchor)(Math.Min(2,n["vertical"].AsInt())*3+Math.Min(2,n["horizontal"].AsInt()));
                if(n.Has("shadow")) {var shadow=rt.gameObject.AddComponent<Shadow>();shadow.effectColor=C(n["shadow"]);shadow.effectDistance=new Vector2(n["shadowOffset"][0].AsFloat(),-n["shadowOffset"][1].AsFloat());}
                graphic=text;
            }
            else if (type == "TextureRect")
            {
                var sprite=_atlas.Get(n["texture"]["region"].AsString());
                var image=UiFactory.Picture("art",rt,sprite); image.raycastTarget=false;
                if(n["stretch"].AsInt()==3 && sprite != null)
                { image.rectTransform.anchorMin=image.rectTransform.anchorMax=new Vector2(.5f,.5f);image.rectTransform.pivot=new Vector2(.5f,.5f);image.rectTransform.anchoredPosition=Vector2.zero;image.rectTransform.sizeDelta=sprite.rect.size; }
                else GodotRect.FullRect(image.rectTransform);
                graphic=image;
            }
            else if(type == "ColorRect" && n["path"].AsString() != "blur")
            { var image=rt.gameObject.AddComponent<Image>();image.color=C(n["color"]);image.raycastTarget=n["mouseFilter"].AsInt()==0;graphic=image; }
            else if(type == "Button")
            {
                var image=rt.GetComponent<Image>()??rt.gameObject.AddComponent<Image>();
                if(n["variation"].AsString()!="ButtonSmallGeneric")image.color=n.Has("buttonColor")?C(n["buttonColor"]):Color.clear;
                var button=rt.GetComponent<Button>()??rt.gameObject.AddComponent<Button>();button.targetGraphic=image;button.interactable=!n["disabled"].AsBool();
                if(n.Has("icon"))
                {
                    var sprite=_atlas.Get(n["icon"]["region"].AsString());
                    if(sprite != null) {var icon=UiFactory.Picture("source_icon",rt,sprite);icon.raycastTarget=false;var w=Mathf.Min(sprite.rect.width,n["iconMaxWidth"].AsFloat(50));icon.rectTransform.anchorMin=icon.rectTransform.anchorMax=new Vector2(.5f,.5f);icon.rectTransform.pivot=new Vector2(.5f,.5f);icon.rectTransform.sizeDelta=new Vector2(w,sprite.rect.height*w/sprite.rect.width);}
                }
                graphic=image;
            }
            if(graphic != null)graphic.color*=C(n["modulate"]);
            foreach(var child in n["children"].Items)
            {
                if(!prototypes && child["path"].AsString().StartsWith("tabs/summary/medals/"))continue;
                Build(child,rt,size,prototypes);
            }
            rt.gameObject.SetActive(n["visible"].AsBool(true));
            return rt;
        }
        public Text Text(string path) => Nodes[path].GetComponent<Text>();
        public Button Button(string path) => Nodes[path].GetComponent<Button>();
        public Image Image(string path) => Nodes[path].GetComponentInChildren<Image>(true);
        private static Vector2 V(JsonNode n) => new Vector2(n[0].AsFloat(),n[1].AsFloat());
        private static Color C(JsonNode n) => new Color(n[0].AsFloat(1),n[1].AsFloat(1),n[2].AsFloat(1),n[3].AsFloat(1));
    }
}
