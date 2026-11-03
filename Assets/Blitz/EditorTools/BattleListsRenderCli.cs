using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders the in-battle scoreboard (Tab) and the map intro team lists off
    /// screen and dumps every row node in Godot's top-left row coordinates, so
    /// both can be held against ui_player_scoreboard_team.tscn and
    /// ui_scoreboard_loading_team.tscn without a live battle.
    /// </summary>
    public static class BattleListsRenderCli
    {
        public const int Width = 1280;
        public const int Height = 720;

        private const string OutputDirectory = "Logs/lists-render";

        /// <summary>Icon sizes that cover the source spread: tall, wide, short.</summary>
        private static readonly string[] TankKeys =
        {
            "maus", "e100", "is7", "t110e5", "g167tigermaus", "t54e1", "t49",
        };

        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory(OutputDirectory);

                var cameraObject = new GameObject("render_camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.25f, 0.3f, 0.25f, 1f);
                camera.orthographic = true;
                camera.transform.position = new Vector3(0f, 0f, -100f);

                var root = new GameObject("roster");
                var roster = Roster(root.transform);
                var dump = new StringBuilder();

                var hud = BattleHud.Create(null, null, false, roster.Count / 2);
                var board = BattleScoreboard.Create(hud.Root);
                board.SetRoster(roster, 0);
                board.Toggle();
                board.Advance(BattleScoreboard.SlideSeconds);
                foreach (Transform child in hud.Root)
                {
                    child.gameObject.SetActive(child.name == "scoreboard");
                }
                ToCamera(hud.Root.GetComponentInParent<Canvas>(), camera);
                Capture(camera, "scoreboard.png");
                DumpRows(dump, "sb", board.AllyRoot.Find("team_box"));
                DumpRows(dump, "sb2", board.EnemyRoot.Find("team_box"));
                UnityEngine.Object.DestroyImmediate(hud.gameObject);

                var intro = BattleLoadingScreen.Create(null, roster, null, false, null);
                intro.Cancel();
                ToCamera(intro.GetComponentInChildren<Canvas>(true), camera);
                Capture(camera, "intro.png");
                var teams = intro.transform.GetComponentInChildren<Canvas>(true)
                    .transform.Find("authored_1040x550/teams");
                DumpRows(dump, "lt", teams.Find("team_ally/tanks"));
                DumpRows(dump, "lt2", teams.Find("team_enemies/tanks"));
                UnityEngine.Object.DestroyImmediate(intro.gameObject);
                UnityEngine.Object.DestroyImmediate(root);

                File.WriteAllText(Path.Combine(OutputDirectory, "rows.txt"), dump.ToString());
                Debug.Log("[Blitz] lists render ok: out=" + Path.GetFullPath(OutputDirectory));
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static List<BattleParticipantStats> Roster(Transform root)
        {
            var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var cards = new List<TankInfo>();
            foreach (var key in TankKeys)
            {
                foreach (var card in db.Tanks)
                {
                    if (card != null && BattleFlowUi.SmallTankIconRegion(card) == "flow_tank_" + key + "_small")
                    {
                        cards.Add(card);
                        break;
                    }
                }
            }

            var roster = new List<BattleParticipantStats>();
            var type = typeof(BattleParticipantStats);
            for (var team = 0; team < 2; team++)
            {
                for (var i = 0; i < cards.Count; i++)
                {
                    var tank = BattleTank.Spawn(
                        root, "t" + team + "_" + i, cards[i], root,
                        new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)), team);
                    var stats = new BattleParticipantStats();
                    type.GetProperty("Team").SetValue(stats, team);
                    type.GetProperty("Nickname").SetValue(stats, "Player_" + i);
                    type.GetProperty("Tank").SetValue(stats, tank);
                    type.GetProperty("IsPlayer").SetValue(stats, team == 0 && i == 2);
                    roster.Add(stats);
                }
            }
            return roster;
        }

        private static void ToCamera(Canvas canvas, Camera camera)
        {
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 10f;
        }

        /// <summary>Every descendant of each row, in the row's top-left space.</summary>
        private static void DumpRows(StringBuilder dump, string label, Transform box)
        {
            if (box == null)
            {
                dump.AppendLine(label + " missing");
                return;
            }
            Canvas.ForceUpdateCanvases();
            for (var i = 0; i < box.childCount; i++)
            {
                var row = (RectTransform)box.GetChild(i);
                var rowRect = row.rect;
                foreach (var node in row.GetComponentsInChildren<RectTransform>(false))
                {
                    var corners = new Vector3[4];
                    node.GetWorldCorners(corners);
                    var min = row.InverseTransformPoint(corners[1]);
                    var max = row.InverseTransformPoint(corners[3]);
                    var x0 = Mathf.Min(min.x, max.x) - rowRect.xMin;
                    var x1 = Mathf.Max(min.x, max.x) - rowRect.xMin;
                    var y0 = rowRect.yMax - Mathf.Max(min.y, max.y);
                    var y1 = rowRect.yMax - Mathf.Min(min.y, max.y);
                    dump.AppendLine(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0}/{1}/{2} x={3:0.##}..{4:0.##} y={5:0.##}..{6:0.##}",
                        label, i, NodePath(row, node), x0, x1, y0, y1));
                }
            }
        }

        private static string NodePath(Transform row, Transform node)
        {
            var path = node.name;
            while (node != row && node.parent != null && node.parent != row)
            {
                node = node.parent;
                path = node.name + "/" + path;
            }
            return node == row ? "." : path;
        }

        private static void Capture(Camera camera, string fileName)
        {
            var target = new RenderTexture(
                Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                Canvas.ForceUpdateCanvases();
                camera.Render();
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply(false, false);
                File.WriteAllBytes(Path.Combine(OutputDirectory, fileName), texture.EncodeToPNG());
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
