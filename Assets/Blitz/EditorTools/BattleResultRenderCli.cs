using System;
using System.IO;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders the ported battle result screen off-screen so its layout can be
    /// held against the Godot reference screenshots without flashing an APK.
    /// The frame size matches the stretched menu viewport the screen is
    /// authored against (1040x550 / 0.75).
    /// </summary>
    public static class BattleResultRenderCli
    {
        public const int Width = 1387;
        public const int Height = 733;

        private const string OutputDirectory = "Logs/result-render";

        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory(OutputDirectory);

                var cameraObject = new GameObject("render_camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.07f, 0.09f, 0.1f, 1f);
                camera.orthographic = true;
                camera.transform.position = new Vector3(0f, 0f, -100f);

                var frames = 0;
                frames += RenderOutcome(camera, BattleOutcome.Victory, "victory");
                frames += RenderOutcome(camera, BattleOutcome.Defeat, "defeat");
                frames += RenderOutcome(camera, BattleOutcome.Draw, "draw");

                RenderHeader(camera);
                Debug.Log("[Blitz] result render ok: frames=" + frames +
                    " " + Width + "x" + Height +
                    " out=" + Path.GetFullPath(OutputDirectory));
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void RenderHeader(Camera camera)
        {
            var root = new GameObject("header_roster");
            var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var allies = new List<BattleTank>();
            var enemies = new List<BattleTank>();
            var hud = BattleHud.Create(null, null, true, 7);
            try
            {
                var ids = new[] { "ms-1", "t-34", "is-7", "su-100", "t-34", "is-7", "su-100" };
                for (var i = 0; i < ids.Length; i++)
                {
                    allies.Add(BattleTank.Spawn(root.transform, "ally" + i, db.Get(ids[i]), root.transform,
                        new Bounds(Vector3.zero, new Vector3(1000,100,1000)), 0));
                    enemies.Add(BattleTank.Spawn(root.transform, "enemy" + i, db.Get(ids[i]), root.transform,
                        new Bounds(Vector3.zero, new Vector3(1000,100,1000)), 1));
                }
                allies[1].Health.ReceiveDamageBasic(100000);
                allies[2].Health.ReceiveDamageBasic(100000);
                enemies[0].Health.ReceiveDamageBasic(100000);
                hud.SetTeamRoster(allies, enemies);
                hud.SetTeamScores(1, 2);
                hud.SetBattleTime(182);
                foreach (Transform child in hud.Root)
                    child.gameObject.SetActive(child.name == "total_tanks");
                var canvas = hud.Root.GetComponentInParent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 10;
                Capture(camera, "battle_header.png");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hud.gameObject);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static int RenderOutcome(
            Camera camera, BattleOutcome outcome, string name)
        {
            var screen = BattleResultScreen.Create(null, Sample(outcome), null);
            try
            {
                var canvas = screen.GetComponentInChildren<Canvas>(true);
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 10f;

                screen.ShowTab(0);
                Capture(camera, name + "_main.png");
                screen.ShowTab(1);
                Capture(camera, name + "_team.png");
                return 2;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(screen.gameObject);
            }
        }

        /// <summary>A full roster with rewards, so nothing on the screen is empty.</summary>
        private static BattleResultData Sample(BattleOutcome outcome)
        {
            var result = new BattleResultData
            {
                Id = "render",
                MapId = "rudniki",
                TankId = "is-7",
                Outcome = outcome,
                FinishReason = outcome == BattleOutcome.Draw
                    ? BattleFinishReason.Timeout
                    : BattleFinishReason.Elimination,
                PremiumAccount = false,
                DurationSeconds = 214f,
                CreditsEarned = 52600,
                Credits = 52600,
                Gold = 1000,
                Xp = 2600,
                FreeXp = 26,
            };

            if (outcome == BattleOutcome.Victory)
            {
                result.Rewards.Add(new BattleRewardData
                {
                    Id = "first_victory", Gold = 250, Rarity = 3,
                });
                result.Rewards.Add(new BattleRewardData
                {
                    Id = "first_blood", Gold = 250, Rarity = 2,
                });
                result.Rewards.Add(new BattleRewardData
                {
                    Id = "warrior", Gold = 150, Rarity = 4,
                });
            }

            var tankName = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get("is-7").DisplayNameShort;
            var names = new[]
            {
                "Amway921", "Jove", "LeBwa", "Yusha", "Nechaev", "Vspishka", "EviL",
                "Korwin", "Marzhec", "Rikitikitavi", "Strannik", "Dezertod",
                "Gnomus", "Tortik",
            };
            for (var index = 0; index < names.Length; index++)
            {
                var participant = new BattleParticipantResult
                {
                    Name = names[index],
                    TankId = "is-7",
                    TankName = tankName,
                    Team = index % 2,
                    IsPlayer = index == 0,
                    Survived = index % 3 != 0,
                    Damage = 1840 - index * 121,
                    DamageTaken = 300 + index * 40,
                    Kills = index % 4,
                    Spots = index % 3,
                    Shots = 12,
                    Hits = 9,
                    Xp = 980 - index * 47,
                    XpRaw = 980 - index * 47,
                };
                result.Participants.Add(participant);
            }
            return result;
        }

        private static void Capture(Camera camera, string fileName)
        {
            var target = new RenderTexture(
                Width, Height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                Canvas.ForceUpdateCanvases();
                // The first frames of a canvas the renderer has never seen are
                // laid out but not yet batched, so they are discarded.
                camera.Render();
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply(false, false);
                File.WriteAllBytes(
                    Path.Combine(OutputDirectory, fileName), texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
