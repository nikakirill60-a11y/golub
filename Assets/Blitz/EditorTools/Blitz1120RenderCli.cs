using System;
using System.IO;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Hangar;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Source-module visual sweep, not a substitute for device QA.</summary>
    public static class Blitz1120RenderCli
    {
        public static void Render()
        { RenderCards("Data/blitz_1120_cards", "Logs/blitz1120-render"); }

        public static void RenderGermany()
        { RenderCards("Data/blitz_germany_1120_cards", "Logs/germany1120-render"); }

        private static void RenderCards(string resource, string folder)
        {
            try
            {
                var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                var newCards = TanksDatabase.Parse(Resources.Load<TextAsset>(resource).text);
                Directory.CreateDirectory(folder);
                var count = 0;
                foreach (var selected in newCards.Tanks)
                {
                    var filterCard = Environment.GetEnvironmentVariable("BLITZ_1120_RENDER_CARD");
                    if (!string.IsNullOrEmpty(filterCard) && selected.Name != filterCard) { continue; }
                    var card = db.Get(selected.Name);
                    for (var turret = 0; turret < card.Turrets.Count; turret++)
                    {
                        foreach (var slot in card.Turrets[turret].Guns)
                        {
                            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                            var save = new TankSaveData { Name = card.Name, TurretId = turret,
                                GunId = card.Guns.FindIndex(g => g.Name == slot.Name),
                                EngineId = card.Engines.Count - 1, TrackId = card.Tracks.Count - 1 };
                            var world = HangarWorld.Create(null, new SettingsData(), card, save);
                            DynamicGI.UpdateEnvironment();
                            var model = world.TankModel;
                            var bounds = new Bounds(model.transform.position, Vector3.zero);
                            var first = true;
                            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>())
                            {
                                if (!renderer.gameObject.activeInHierarchy) { continue; }
                                if (first) { bounds = renderer.bounds; first = false; }
                                else { bounds.Encapsulate(renderer.bounds); }
                            }
                            if (first) { throw new InvalidOperationException(card.Name + " has no visible geometry"); }
                            var camera = world.HangarCamera;
                            if (Environment.GetEnvironmentVariable("BLITZ_1120_RENDER_ISOLATE") == "1")
                            {
                                foreach (var renderer in world.GetComponentsInChildren<MeshRenderer>())
                                { if (!renderer.transform.IsChildOf(model.transform)) { renderer.enabled = false; } }
                                camera.clearFlags = CameraClearFlags.SolidColor;
                                camera.backgroundColor = new Color(.12f, .12f, .12f);
                            }
                            var angles = new[] { 35f, 135f, 225f, 315f };
                            for (var view = 0; view < angles.Length; view++)
                            {
                                var direction = Quaternion.Euler(18f, angles[view], 0f) * Vector3.back;
                                camera.transform.position = bounds.center + direction * Mathf.Max(7f, bounds.size.magnitude * 1.05f);
                                camera.transform.LookAt(bounds.center);
                                camera.fieldOfView = 38f;
                                Capture(camera, Path.Combine(folder, TankModelAssets.ResourceId(card.Name) +
                                    "_" + card.Turrets[turret].Mesh + "_" + slot.Mesh + "_g" + save.GunId + "_" + view + ".png"));
                            }
                            count++;
                            UnityEngine.Object.DestroyImmediate(world.gameObject);
                            Resources.UnloadUnusedAssets();
                        }
                    }
                }
                Debug.Log("[Blitz] 11.20 render configurations=" + count);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        private static void Capture(Camera camera, string path)
        {
            var previous = camera.targetTexture;
            var active = RenderTexture.active;
            var target = new RenderTexture(900, 600, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(900, 600, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                for (var i = 0; i < 3; i++) { camera.Render(); }
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 900, 600), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previous;
                RenderTexture.active = active;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
