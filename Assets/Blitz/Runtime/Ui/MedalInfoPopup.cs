using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
namespace BlitzLegacy.Runtime.Ui
{
    public sealed class MedalInfoPopup : MonoBehaviour
    {
        public string RewardId { get; private set; }
        public static int EscapeConsumedFrame { get; private set; } = -1;
        private static JsonNode _definitions;
        public static JsonNode Definition(string id) { if (_definitions == null) _definitions=JsonNode.Parse(BlitzResources.LoadText("battle_reward_definitions"))["definitions"]; return _definitions[id]; }
        public static string Region(string id) => id=="first_victory"||id=="first_blood"||id=="warrior" ? "flow_reward_"+id : "reward_"+id;
        public static MedalInfoPopup Open(Transform parent,string id)
        {
            var go=new GameObject("MedalInfoPopup");go.transform.SetParent(parent,false);
            var popup=go.AddComponent<MedalInfoPopup>();popup.RewardId=id;
            var canvas=BattleFlowUi.CreateCanvas(go.transform,false,false,180);
            var ui=new SourceProfileUi("reward",canvas);
            var definition=Definition(id);var lang=BattleFlowUi.LoadLanguage();
            var image=ui.Image("medal");image.sprite=BattleFlowUi.LoadAtlas().Get(Region(id));
            if(image.sprite != null)image.rectTransform.sizeDelta=image.sprite.rect.size;
            ui.Text("name").text="«"+lang.Get(definition["nameKey"].AsString())+"»";
            ui.Text("desc").text=lang.Get(definition["descKey"].AsString());
            var gold=definition["gold"].AsInt();ui.Text("price").text=AccountStatisticsScreen.Number(gold);
            ui.Nodes["gold"].gameObject.SetActive(gold!=0);ui.Nodes["price"].gameObject.SetActive(gold!=0);
            ui.Button("btn_menu").onClick.AddListener(()=>Destroy(go));
            return popup;
        }
        private void Update(){if(Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame){EscapeConsumedFrame=Time.frameCount;Destroy(gameObject);}}
    }
}
