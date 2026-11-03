using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Covers the ported battle audio model: the Godot sound sets exported by
    /// <c>Tools/port_godot_sounds.py</c>, the band pickers of
    /// <c>engine_sounds.gd</c> / <c>track_sounds.gd</c>, the crew message queue
    /// and the tuning maths lifted out of <c>tank_sound_manager.gd</c>.
    /// </summary>
    public sealed class TankSoundTests
    {
        private const float Tolerance = 1e-4f;

        private static TankSoundTable Table()
        {
            var json = BlitzResources.LoadText(TankSoundTable.Resource);
            Assert.IsFalse(string.IsNullOrEmpty(json),
                "Resources/Data/tank_sounds.json did not load");
            return TankSoundTable.Parse(json);
        }

        private static SoundPool Pool(params string[] names)
        {
            var streams = "";
            for (var i = 0; i < names.Length; i++)
            {
                streams += (i == 0 ? "" : ",") + "\"" + names[i] + "\"";
            }

            return SoundPool.FromJson(JsonNode.Parse(
                "{\"volume\":1.0,\"streams\":[" + streams + "]}"));
        }

        [Test]
        public void ExportedTableCarriesEveryGodotSoundSet()
        {
            var table = Table();

            // Six Godot gun sets plus shot_atgm, which only the Blitz pack
            // carries (guns/tow).
            Assert.AreEqual(7, table.GunSetCount);
            Assert.AreEqual(16, table.EngineSetCount);
            Assert.AreEqual(4, table.TrackSetCount);
            Assert.AreEqual(4, table.ShellSetCount);

            Assert.IsTrue(table.Engine(EngineSound.UssrLt).Idle.HasStreams);
            Assert.IsTrue(table.Tracks(TrackSound.Lt).Bands[0].HasStreams);
            Assert.IsTrue(table.Gun(ShootSound.ShotSmall).Near.HasStreams);
            Assert.IsTrue(table.Gun(ShootSound.ShotSmall).Inside.HasStreams);
            Assert.IsTrue(table.Gun(ShootSound.ShotAtgm).Near.HasStreams);
            Assert.IsTrue(table.Gun(ShootSound.ShotAtgm).Inside.HasStreams);
            Assert.IsTrue(table.Gun(ShootSound.ShotAtgm).Dist.HasStreams);
            Assert.IsTrue(table.Explosion(TankSoundTable.TankExplosionSet)
                .Near.HasStreams);
            Assert.IsTrue(table.Shell(ShellType.Ap)
                .Pool(ShellSoundKind.Pen, ShootSound.ShotSmall).HasStreams);
        }

        [Test]
        public void MiscAndCrewPoolsCoverEveryCueTheManagerAsksFor()
        {
            var table = Table();
            var keys = new[]
            {
                "inside_pen", "inside_bounce", "tank_collision_light",
                "tank_collision_heavy", "tracks_destroy", "tracks_fix",
                "ui_sniper_in", "ui_sniper_out", "ui_autoaim_on",
                "ui_autoaim_off", "ui_shell_select", "ui_lamp", "ui_countdown",
                // The looped bed a guided rocket flies on, and the reports
                // the two warheads make when they land.
                TankSoundTable.ShellFlightSet,
                TankSoundTable.AtgmPenHeatSet,
                TankSoundTable.AtgmPenExplosiveSet,
                TankSoundTable.AtgmPenInsideSet,
            };

            for (var i = 0; i < keys.Length; i++)
            {
                Assert.IsTrue(table.Sound(keys[i]).HasStreams, keys[i]);
            }

            var messages = (CrewMessage[])System.Enum.GetValues(
                typeof(CrewMessage));
            for (var i = 0; i < messages.Length; i++)
            {
                Assert.IsTrue(table.Crew(messages[i]).HasStreams,
                    TankSoundTable.CrewKey(messages[i]));
            }
        }

        [Test]
        public void UnknownSetsFallBackTheWayTheAutoloadDoes()
        {
            Assert.AreEqual("shot_auto", TankSoundTable.GunKey((ShootSound)99));
            Assert.AreEqual("ussr_lt", TankSoundTable.EngineKey((EngineSound)99));
            Assert.AreEqual("lt", TankSoundTable.TrackKey((TrackSound)99));

            // shell_type_to_sound: HESH shares the HE reports.
            Assert.AreEqual("he", TankSoundTable.ShellKey(ShellType.Hesh));
            Assert.AreEqual("he", TankSoundTable.ShellKey(ShellType.He));
            Assert.AreEqual("ap", TankSoundTable.ShellKey(ShellType.Ap));

            // get_pen and friends fold the autocannon into the small pool.
            Assert.AreEqual("small", ShellSoundSet.SizeKey(ShootSound.ShotAuto));
            Assert.AreEqual("huge", ShellSoundSet.SizeKey(ShootSound.ShotHuge));
            Assert.AreEqual("huge", ShellSoundSet.SizeKey(ShootSound.ShotAtgm));
            Assert.AreEqual(
                "shot_atgm", TankSoundTable.GunKey(ShootSound.ShotAtgm));
            Assert.AreEqual("pen", ShellSoundSet.KindKey(ShellSoundKind.Pen));
            Assert.AreEqual("ricoshet",
                ShellSoundSet.KindKey(ShellSoundKind.Ricoshet));
        }

        [Test]
        public void EngineBandsFollowPickSndByParam()
        {
            var set = EngineSoundSet.FromJson(JsonNode.Parse(
                "{\"idle\":{\"streams\":[\"idle\"]},"
                + "\"low_rpm\":{\"streams\":[\"low\"]},"
                + "\"mid_rpm\":{\"streams\":[\"mid\"]},"
                + "\"high_rpm\":{\"streams\":[\"high\"]}}"));

            Assert.AreEqual("idle", set.PickByParam(0f).Streams[0]);
            Assert.AreEqual("idle", set.PickByParam(-1f).Streams[0]);
            Assert.AreEqual("low", set.PickByParam(0.2f).Streams[0]);
            Assert.AreEqual("mid", set.PickByParam(0.3f).Streams[0]);
            Assert.AreEqual("mid", set.PickByParam(0.7f).Streams[0]);

            // The source clamps to 0.8 before testing [0.95, 0.8], so the
            // high_rpm band is unreachable through the load term alone.
            Assert.AreEqual("mid", set.PickByParam(1f).Streams[0]);
        }

        [Test]
        public void TrackBandsFollowPickSndBySpeed()
        {
            var set = TrackSoundSet.FromJson(JsonNode.Parse(
                "{\"stop\":{\"streams\":[\"stop\"]},"
                + "\"speed_10\":{\"streams\":[\"b1\"]},"
                + "\"speed_20\":{\"streams\":[\"b2\"]},"
                + "\"speed_30\":{\"streams\":[\"b3\"]},"
                + "\"speed_40\":{\"streams\":[\"b4\"]},"
                + "\"speed_50\":{\"streams\":[\"b5\"]},"
                + "\"speed_60\":{\"streams\":[\"b6\"]},"
                + "\"speed_70\":{\"streams\":[\"b7\"]}}"));

            Assert.AreEqual("stop", set.PickBySpeed(0f).Streams[0]);
            Assert.AreEqual("stop", set.PickBySpeed(1.9f).Streams[0]);
            Assert.AreEqual("b1", set.PickBySpeed(5f).Streams[0]);
            Assert.AreEqual("b2", set.PickBySpeed(15f).Streams[0]);
            Assert.AreEqual("b3", set.PickBySpeed(27f).Streams[0]);
            Assert.AreEqual("b4", set.PickBySpeed(40f).Streams[0]);
            Assert.AreEqual("b5", set.PickBySpeed(50f).Streams[0]);
            Assert.AreEqual("b6", set.PickBySpeed(58f).Streams[0]);
            Assert.AreEqual("b7", set.PickBySpeed(90f).Streams[0]);
            Assert.AreEqual("b7", set.PickBySpeed(400f).Streams[0]);
        }

        [Test]
        public void EmptyPoolPicksNothingAndSinglePoolPicksItsOnlyStream()
        {
            Assert.IsNull(SoundPool.Empty.PickRandom(null));
            Assert.IsFalse(SoundPool.Empty.HasStreams);
            Assert.AreEqual("one", Pool("one").PickRandom(count => 0));
            Assert.AreEqual("two", Pool("one", "two").PickRandom(count => 1));

            // An out of range draw falls back to the first stream.
            Assert.AreEqual("one", Pool("one", "two").PickRandom(count => 7));
        }

        [Test]
        public void CrewQueueDropsDuplicatesAndSpeaksTheNewestLineFirst()
        {
            var queue = new CrewMessageQueue();
            queue.Schedule(CrewMessage.ArmorPierced);
            queue.Schedule(CrewMessage.ArmorPierced);
            queue.Schedule(CrewMessage.EnemyIgnited);

            Assert.AreEqual(2, queue.Count);

            CrewMessage message;
            Assert.IsTrue(queue.TryPop(0f, out message));
            Assert.AreEqual(CrewMessage.EnemyIgnited, message);
            Assert.IsTrue(queue.TryPop(0f, out message));
            Assert.AreEqual(CrewMessage.ArmorPierced, message);
            Assert.IsFalse(queue.TryPop(0f, out message));
        }

        [Test]
        public void TopPriorityCrewLinesWipeWhateverWasWaiting()
        {
            var queue = new CrewMessageQueue();
            queue.Schedule(CrewMessage.ArmorPierced);
            queue.Schedule(CrewMessage.Ricoshet);
            queue.Schedule(CrewMessage.ModuleTrackDestroyed);

            Assert.AreEqual(1, queue.Count);
            Assert.IsTrue(CrewMessageQueue.IsTopPriority(
                CrewMessage.TankDestroyed));
            Assert.IsFalse(CrewMessageQueue.IsTopPriority(
                CrewMessage.ArmorPierced));

            CrewMessage message;
            Assert.IsTrue(queue.TryPop(0f, out message));
            Assert.AreEqual(CrewMessage.ModuleTrackDestroyed, message);
        }

        [Test]
        public void AStartedLineBlocksTheQueueForItsLengthPlusTheTail()
        {
            var queue = new CrewMessageQueue();
            queue.Started(10f, 2f);

            Assert.AreEqual(12.25f, queue.NextMessageTime, Tolerance);

            queue.Schedule(CrewMessage.EnemyKilled);

            CrewMessage message;
            Assert.IsFalse(queue.TryPop(12f, out message));
            Assert.IsTrue(queue.TryPop(12.25f, out message));
            Assert.AreEqual(CrewMessage.EnemyKilled, message);

            queue.Schedule(CrewMessage.EnemyKilled);
            queue.Clear();
            Assert.AreEqual(0, queue.Count);
            Assert.AreEqual(0f, queue.NextMessageTime, Tolerance);
        }

        [Test]
        public void CollisionGainFollowsTheClampedImpulseCurve()
        {
            Assert.AreEqual(0f, TankSoundTuning.CollisionVolume(0f), Tolerance);
            Assert.AreEqual(
                (float)System.Math.Pow(0.25f, 1.2f),
                TankSoundTuning.CollisionVolume(10f), Tolerance);

            // clampf caps the gain at 0.4 before the exponent is applied.
            Assert.AreEqual(
                (float)System.Math.Pow(0.4f, 1.2f),
                TankSoundTuning.CollisionVolume(100000f), Tolerance);
        }

        [Test]
        public void EngineLoadAddsTheSteeringAxisToTheSpeedShare()
        {
            Assert.AreEqual(0.5f, TankSoundTuning.EngineLoad(0.5f, 0f),
                Tolerance);
            Assert.AreEqual(0.3f, TankSoundTuning.EngineLoad(0f, 1f), Tolerance);
            Assert.AreEqual(0.3f, TankSoundTuning.EngineLoad(0f, -1f),
                Tolerance);
            Assert.AreEqual(0.25f, TankSoundTuning.TurningLoadMax, Tolerance);
            Assert.AreEqual(1, TankSoundTuning.TracksOnGroundHits);
        }

        [Test]
        public void SpatialFilterCutoffStaysUnderTheDeviceCeiling()
        {
            Assert.AreEqual(19845f,
                TankSoundTuning.SpatialFilterCutoff(44100f), Tolerance);
            Assert.AreEqual(20500f,
                TankSoundTuning.SpatialFilterCutoff(48000f), Tolerance);
            Assert.AreEqual(0.85f, TankSoundTuning.MuffleSfxShare, Tolerance);
            Assert.AreEqual(0.3f, TankSoundTuning.ShellSoundInterval, Tolerance);
            Assert.AreEqual(300f, TankSoundTuning.GunSoundNearDistance,
                Tolerance);
            Assert.AreEqual(300f, TankSoundTuning.ExplosionSoundNearDistance,
                Tolerance);
        }

        [Test]
        public void FpsMeterUsesTheSourceLabelAndCadence()
        {
            Assert.AreEqual("FPS: 0", HudTuning.FpsText(0));
            Assert.AreEqual("FPS: 60", HudTuning.FpsText(60));
            Assert.AreEqual(0.25f, HudTuning.FpsUpdateInterval, Tolerance);
            Assert.AreEqual(18, HudTuning.FpsFontSize);
        }
    }
}
