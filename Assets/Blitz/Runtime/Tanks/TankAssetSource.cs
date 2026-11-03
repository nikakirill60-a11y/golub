using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Mods;
using UnityEngine;

namespace BlitzLegacy.Runtime.Tanks
{
    /// <summary>
    /// Where a vehicle's meshes, textures and tables come from.
    ///
    /// Everything that shipped with the build lives under Resources, written
    /// there by the port tools. A mod arrives after the build and can only
    /// live on disk, so every load goes through here: Resources first, then
    /// the installed mods. Call sites stay unaware of which one answered.
    /// </summary>
    public static class TankAssetSource
    {
        private static readonly Dictionary<string, Texture2D> Textures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, TextAsset> Assets =
            new Dictionary<string, TextAsset>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drops the cached mod textures and meshes.</summary>
        public static void Forget()
        {
            Textures.Clear();
            Assets.Clear();
            ModTankModel.Forget();
        }

        /// <summary>
        /// True when the vehicle has a model the player can drive. The
        /// build's own models are looked up in <see cref="TankModelIndex"/>
        /// instead of being loaded; a mod's model is asked for as before.
        /// </summary>
        public static bool HasModel(TankInfo card)
        {
            if (card == null)
            {
                return false;
            }

            var assets = TankModelAssets.For(card);
            bool shipped;
            if (!TankModelIndex.TryContains(assets.Id, out shipped))
            {
                return Model(assets) != null;
            }

            return shipped || ModTankModel.Visible(assets.TankName) != null;
        }

        public static GameObject Model(TankModelAssets assets)
        {
            var prefab = Resources.Load<GameObject>(assets.ModelResource);
            return prefab != null ? prefab : ModTankModel.Visible(assets.TankName);
        }

        public static GameObject ArmorModel(TankModelAssets assets)
        {
            var prefab = Resources.Load<GameObject>(assets.ArmorModelResource);
            return prefab != null ? prefab : ModTankModel.Armor(assets.TankName);
        }

        public static GameObject PhysicsModel(TankModelAssets assets)
        {
            var prefab = Resources.Load<GameObject>(assets.PhysicsResource);
            return prefab != null ? prefab : ModTankModel.Physics(assets.TankName);
        }

        /// <summary>The node rig, wrapped so call sites keep one shape.</summary>
        public static TextAsset NodesAsset(TankModelAssets assets)
        {
            var asset = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
            return asset != null ? asset : Wrap(assets.NodeDataResource, NodesJson(assets));
        }

        /// <summary>The armour table, wrapped so call sites keep one shape.</summary>
        public static TextAsset ArmorAsset(TankModelAssets assets)
        {
            var asset = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
            return asset != null ? asset : Wrap(assets.ArmorDataResource, ArmorJson(assets));
        }

        private static TextAsset Wrap(string name, string json)
        {
            if (string.IsNullOrEmpty(json)) { return null; }

            TextAsset cached;
            if (Assets.TryGetValue(name, out cached) && cached != null) { return cached; }

            cached = new TextAsset(json) { name = name };
            Assets[name] = cached;
            return cached;
        }

        public static string NodesJson(TankModelAssets assets)
        {
            var asset = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
            return asset != null ? asset.text : ModTankModel.NodesJson(assets.TankName);
        }

        public static string ArmorJson(TankModelAssets assets)
        {
            var asset = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
            return asset != null ? asset.text : ModTankModel.ArmorJson(assets.TankName);
        }

        public static Texture2D BodyTexture(TankModelAssets assets)
        {
            var texture = Resources.Load<Texture2D>(assets.BodyTextureResource);
            return texture != null ? texture : ModTexture(assets.TankName, false);
        }

        public static Texture2D TrackTexture(TankModelAssets assets)
        {
            var texture = Resources.Load<Texture2D>(assets.TrackTextureResource);
            return texture != null ? texture : ModTexture(assets.TankName, true);
        }

        private static Texture2D ModTexture(string tankName, bool tracks)
        {
            var key = (tracks ? "t:" : "b:") + tankName;
            Texture2D cached;
            if (Textures.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            var tank = ModRuntime.TankAssets(tankName);
            var path = tank == null
                ? null
                : tracks ? tank.TrackTexturePath : tank.BodyTexturePath;
            Texture2D texture;
            // An image that takes the game down while it decodes switches its mod
            // off for the next start.
            using (ModRuntime.Guard.Loading(tank != null ? ModRuntime.TankModId(tankName) : null))
            {
                texture = ModRuntime.LoadTexture(path);
                if (texture == null && tracks && tank != null)
                {
                    // Not every mod ships a separate track sheet; Godot falls back
                    // to the body sheet rather than drawing untextured tracks.
                    texture = ModRuntime.LoadTexture(tank.BodyTexturePath);
                }
            }

            Textures[key] = texture;
            return texture;
        }
    }
}

