using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;
namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Runtime destruction path from original 4.8 FX and 4.8/11.20 crash assets.
    /// Unlike the obsolete Godot inspection factory, scales are authored metres.</summary>
    public sealed class Original480Wreck : MonoBehaviour
    {
        private BattleTank _tank;
        private JsonNode _definition;
        private string _crashResource = "Legacy480/Textures/crash";
        private Texture2D _crash;
        private readonly List<DavaFx> _effects = new List<DavaFx>();
        private readonly Dictionary<MeshRenderer,Material[]> _live = new Dictionary<MeshRenderer,Material[]>();
        private readonly List<Material> _burnt = new List<Material>();
        public bool Playing { get; private set; }
        /// <summary>
        /// War of Ducks: play the WarDuck's own 4.8 death scenes from the
        /// event's particle folder instead of the legacy wreck manifest.
        /// </summary>
        public bool UseWorldOfDucksFx { get; set; }
        public string ExplosionSource { get; set; }
        public string FlamingSource { get; set; }
        /// <summary>
        /// War of Ducks: world height of the sea. The WarDuck has no HP_Fire
        /// point, so its scenes hung at 55 % of the hull and the fountain, the
        /// whirl and the bubbles rose out of the duck's head; the water writes
        /// depth, so both scenes are laid on the surface under the duck instead.
        /// </summary>
        public float? WaterSurface { get; set; }
        public IReadOnlyList<DavaFx> Effects => _effects;
        public void Initialize(BattleTank tank)
        {
            _tank=tank;
            var manifest=JsonResources.Load("Data/legacy480_wreck_manifest");
            _definition=manifest["vehicles"][tank.Card.Name];
            var newer = JsonResources.Load("Data/legacy1120_crash_manifest");
            if (newer != null)
            {
                var record = newer["vehicles"][tank.Card.Name];
                if (record.Kind == JsonKind.Object)
                {
                    _definition = record;
                    _crashResource = record["crashTexture"].AsString();
                }
            }

            // Load the crash sheet with the tank, not in the frame it dies.
            _crash = Resources.Load<Texture2D>(_crashResource);
        }
        public void Play()
        {
            using var slowScope = BlitzPerfMonitor.Slow("wreck480.play");
            if(Playing)return;
            Playing=true;
            // 4.8 XML points undamaged/destroyed/exploded to the same part model.
            // Broken track geometry is swapped by BattleTank; apply the shipped
            // common crash sheet to the original parts, never procedural rubble.
            var crash=_crash!=null?_crash:Resources.Load<Texture2D>(_crashResource);
            foreach(var renderer in _tank.Model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var live=renderer.sharedMaterials;_live[renderer]=live;
                var dead=(Material[])live.Clone();
                for(var i=0;i<dead.Length;i++)
                {
                    if(dead[i]==null || !dead[i].HasProperty("_BaseMap"))continue;
                    var material=new Material(dead[i]);_burnt.Add(material);
                    material.SetTexture("_BaseMap",crash);
                    if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",Color.white);
                    if(material.HasProperty("_Smoothness"))material.SetFloat("_Smoothness",0);
                    // A burnt hull shows the crash sheet alone, camouflage or not.
                    if(material.HasProperty("_CamoColor"))material.SetColor("_CamoColor",Color.clear);
                    dead[i]=material;
                }
                renderer.sharedMaterials=dead;
            }
            var explosion=_tank.AmmoBayExploded?"explosion":"destruction";
            var source=_definition[explosion].AsString(_tank.AmmoBayExploded?"explosion_tank_1":"explosion_rush_1");
            if(UseWorldOfDucksFx && !string.IsNullOrEmpty(ExplosionSource))source=ExplosionSource;
            var explosionFx=UseWorldOfDucksFx?DavaFx.CreateWorldOfDucks(transform,source,30):DavaFx.Create480(transform,source,30);
            // HP_Fire marker vertices are authored attachment positions in the
            // original tank SC2, not a guessed size multiplier.
            var point=_definition["firePoints"]["HP_Fire_01"];
            var anchor=point.Kind==JsonKind.Array?new Vector3(point[0].AsFloat(),point[1].AsFloat(),point[2].AsFloat()):
                new Vector3(0,_tank.Hull.Height*.55f,0);
            explosionFx.transform.localPosition=new Vector3(0,anchor.y,0);
            _effects.Add(explosionFx);
            var flaming=_definition["flaming"].AsString("Fire_and_Smoke");
            if(UseWorldOfDucksFx && !string.IsNullOrEmpty(FlamingSource))flaming=FlamingSource;
            var flamingFx=UseWorldOfDucksFx?DavaFx.CreateWorldOfDucks(transform,flaming,1000,true):DavaFx.Create480(transform,flaming,1000,true);
            flamingFx.transform.localPosition=anchor;
            _effects.Add(flamingFx);
            if(UseWorldOfDucksFx && WaterSurface.HasValue)
                foreach(var fx in new[]{explosionFx,flamingFx})
                {
                    var p=fx.transform.position;fx.transform.position=new Vector3(p.x,WaterSurface.Value,p.z);
                    fx.LevelEmitters(0);
                }
        }
        public void Stop()
        {
            foreach(var item in _live)if(item.Key!=null)item.Key.sharedMaterials=item.Value;
            _live.Clear();
            foreach(var fx in _effects)if(fx!=null)Release(fx.gameObject);
            _effects.Clear();
            foreach(var material in _burnt)if(material!=null)Release(material);
            _burnt.Clear();Playing=false;
        }
        private static void Release(Object obj){if(Application.isPlaying)Destroy(obj);else DestroyImmediate(obj);}
        private void OnDestroy(){Stop();}
    }
}
