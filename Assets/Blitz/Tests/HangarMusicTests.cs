using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>The play order, the playlist and the saved state of the hangar music player.</summary>
    public sealed class HangarMusicTests
    {
        // ---------------------------------------------------------------- queue

        [Test]
        public void InOrderTheListRunsRoundAndRound()
        {
            var queue = new HangarMusicQueue(3, 1, new SeededRandomSource(1));
            Assert.AreEqual(1, queue.Current);
            Assert.AreEqual(2, queue.Next());
            Assert.AreEqual(0, queue.Next());
            Assert.AreEqual(2, queue.Previous(0d));
            Assert.AreEqual(1, queue.Previous(HangarMusicQueue.RestartSeconds));
        }

        [Test]
        public void PreviousPastTheFirstSecondsStaysToStartTheTrackOver()
        {
            var queue = new HangarMusicQueue(4, 2, new SeededRandomSource(1));
            Assert.AreEqual(2, queue.Previous(HangarMusicQueue.RestartSeconds + 0.5d));
            Assert.AreEqual(1, queue.Previous(0.5d));
        }

        [Test]
        public void RepeatOneHoldsOnlyATrackThatPlayedOut()
        {
            var queue = new HangarMusicQueue(5, 3, new SeededRandomSource(1)) { RepeatOne = true };
            Assert.AreEqual(3, queue.Finished());
            Assert.AreEqual(3, queue.Finished());
            Assert.AreEqual(4, queue.Next());
            Assert.AreEqual(3, queue.Previous(0d));
            queue.RepeatOne = false;
            Assert.AreEqual(4, queue.Finished());
        }

        [Test]
        public void ShuffleStartsWithThePlayingTrackAndPlaysEveryTrackOncePerRound()
        {
            const int count = 7;
            var queue = new HangarMusicQueue(count, 4, new SeededRandomSource(20261031));
            queue.SetShuffle(true);
            Assert.IsTrue(queue.Shuffle);
            Assert.AreEqual(4, queue.Current);

            var played = new List<int> { queue.Current };
            for (var i = 1; i < count * 40; i++)
            {
                played.Add(queue.Next());
            }

            for (var round = 0; round < 40; round++)
            {
                var tracks = played.Skip(round * count).Take(count).OrderBy(t => t).ToArray();
                CollectionAssert.AreEqual(Enumerable.Range(0, count).ToArray(), tracks, "round " + round);
            }

            for (var i = 1; i < played.Count; i++)
            {
                Assert.AreNotEqual(played[i - 1], played[i], "a track repeats at " + i);
            }
        }

        [Test]
        public void ShuffledPreviousWalksBackThroughWhatPlayedAndStopsAtTheStart()
        {
            var queue = new HangarMusicQueue(5, 0, new SeededRandomSource(7));
            queue.SetShuffle(true);
            var played = new List<int> { queue.Current };
            for (var i = 0; i < 7; i++)
            {
                played.Add(queue.Next());
            }

            for (var i = played.Count - 2; i >= 0; i--)
            {
                Assert.AreEqual(played[i], queue.Previous(0d));
            }

            Assert.AreEqual(played[0], queue.Previous(0d), "before the first track it stays");
            for (var i = 1; i < played.Count; i++)
            {
                Assert.AreEqual(played[i], queue.Next(), "next after previous replays the same order");
            }
        }

        [Test]
        public void ShuffleOffGoesOnInListOrderFromThePlayingTrack()
        {
            var queue = new HangarMusicQueue(6, 0, new SeededRandomSource(3));
            queue.SetShuffle(true);
            queue.Next();
            queue.Next();
            var current = queue.Current;
            queue.SetShuffle(false);
            Assert.AreEqual(current, queue.Current);
            Assert.AreEqual((current + 1) % 6, queue.Next());
        }

        [Test]
        public void SelectPlaysTheTrackAndDealsTheShuffleAgainFromIt()
        {
            var queue = new HangarMusicQueue(6, 0, new SeededRandomSource(5));
            queue.SetShuffle(true);
            Assert.AreEqual(3, queue.Select(3));
            var round = new HashSet<int> { queue.Current };
            for (var i = 1; i < 6; i++)
            {
                Assert.IsTrue(round.Add(queue.Next()));
            }

            Assert.AreEqual(5, queue.Select(99), "an index past the end is the last track");
        }

        [Test]
        public void EmptyAndSingleTrackQueuesStayPut()
        {
            var empty = new HangarMusicQueue(0, 3, new SeededRandomSource(1));
            Assert.AreEqual(-1, empty.Current);
            Assert.AreEqual(-1, empty.Next());
            Assert.AreEqual(-1, empty.Previous(0d));
            empty.SetShuffle(true);
            Assert.AreEqual(-1, empty.Finished());

            var single = new HangarMusicQueue(1, 5, new SeededRandomSource(1));
            Assert.AreEqual(0, single.Current);
            single.SetShuffle(true);
            Assert.AreEqual(0, single.Next());
            Assert.AreEqual(0, single.Next());
            Assert.AreEqual(0, single.Previous(0d));
        }

        // ------------------------------------------------------------- playlist

        [Test]
        public void ParseKeepsTheGoodRowsInOrder()
        {
            var playlist = HangarMusicPlaylist.Parse(@"{
                ""folder"": ""HangarMusic"",
                ""tracks"": [
                    { ""id"": ""a"", ""artist"": ""A"", ""title"": ""Alpha"", ""file"": ""a.ogg"", ""seconds"": 60.5, ""loudness"": -9.5 },
                    { ""id"": """", ""file"": ""x.ogg"", ""seconds"": 10 },
                    { ""id"": ""b"", ""file"": """", ""seconds"": 10 },
                    { ""id"": ""c"", ""file"": ""c.ogg"", ""seconds"": 0 },
                    { ""id"": ""d"", ""title"": ""Delta"", ""file"": ""d.ogg"", ""seconds"": 90, ""default"": true },
                    { ""id"": ""a"", ""title"": ""Again"", ""file"": ""a2.ogg"", ""seconds"": 30 },
                    { ""id"": ""e"", ""file"": ""e.ogg"", ""seconds"": 5 }
                ]
            }");

            Assert.AreEqual("HangarMusic", playlist.Folder);
            CollectionAssert.AreEqual(new[] { "a", "d", "e" }, playlist.Tracks.Select(t => t.Id).ToArray());
            Assert.AreEqual(1, playlist.DefaultIndex);
            Assert.AreEqual("Alpha", playlist[0].Title);
            Assert.AreEqual("A", playlist[0].Artist);
            Assert.AreEqual(60.5d, playlist[0].Seconds, 1e-9);
            Assert.AreEqual(-9.5f, playlist[0].Loudness, 1e-6f);
            Assert.AreEqual("e", playlist[2].Title, "a track without a title shows its id");
            Assert.AreEqual(string.Empty, playlist[1].Artist);
            Assert.AreEqual(HangarMusicPlaylist.TargetLoudness, playlist[2].Loudness, "unmeasured tracks play as they are");

            Assert.AreEqual(2, playlist.IndexOf("e"));
            Assert.AreEqual(-1, playlist.IndexOf("c"));
            Assert.AreEqual(2, playlist.StartIndex("e"));
            Assert.AreEqual(1, playlist.StartIndex("gone"), "a dropped track starts on the default");
            Assert.AreEqual(1, playlist.StartIndex(string.Empty));
        }

        [Test]
        public void BrokenPlaylistsAreEmpty()
        {
            Assert.AreEqual(0, HangarMusicPlaylist.Parse(null).Count);
            Assert.AreEqual(0, HangarMusicPlaylist.Parse("{").Count);
            Assert.AreEqual(0, HangarMusicPlaylist.Parse("[]").Count);
            Assert.AreEqual(0, HangarMusicPlaylist.Parse("{\"tracks\": 4}").Count);
            Assert.AreEqual(0, HangarMusicPlaylist.Empty().DefaultIndex);
        }

        [Test]
        public void GainBringsEveryTrackToTheTargetLoudness()
        {
            Assert.AreEqual(1f, HangarMusicPlaylist.GainFor(HangarMusicPlaylist.TargetLoudness), 1e-6f);
            Assert.AreEqual(Mathf.Pow(10f, -10f / 20f), HangarMusicPlaylist.GainFor(-8f), 1e-5f);
            Assert.AreEqual(Mathf.Pow(10f, 5f / 20f), HangarMusicPlaylist.GainFor(-23f), 1e-5f);
            Assert.AreEqual(
                Mathf.Pow(10f, HangarMusicPlaylist.MaxBoostDb / 20f), HangarMusicPlaylist.GainFor(-60f), 1e-5f,
                "a nearly silent file is not blown up");
            Assert.AreEqual(1f, HangarMusicPlaylist.GainFor(float.NaN));
            Assert.AreEqual(1f, HangarMusicPlaylist.GainFor(float.NegativeInfinity));
        }

        [Test]
        public void TimesReadAsMinutesAndHours()
        {
            Assert.AreEqual("0:00", HangarMusicPlaylist.FormatTime(0d));
            Assert.AreEqual("0:00", HangarMusicPlaylist.FormatTime(-4d));
            Assert.AreEqual("0:00", HangarMusicPlaylist.FormatTime(double.NaN));
            Assert.AreEqual("0:59", HangarMusicPlaylist.FormatTime(59.99d));
            Assert.AreEqual("3:09", HangarMusicPlaylist.FormatTime(189.4d));
            Assert.AreEqual("59:59", HangarMusicPlaylist.FormatTime(3599d));
            Assert.AreEqual("1:00:00", HangarMusicPlaylist.FormatTime(3600d));
            Assert.AreEqual("1:02:05", HangarMusicPlaylist.FormatTime(3725.7d));
        }

        [Test]
        public void TheShippedPlaylistPlaysTheMikuHangarTracksFromTheirFiles()
        {
            var text = BlitzResources.LoadText(HangarMusicPlaylist.Resource);
            Assert.IsNotNull(text, "Resources/Data/hangar_music.json is missing");
            var rows = JsonNode.Parse(text)["tracks"];
            var playlist = HangarMusicPlaylist.Parse(text);

            Assert.AreEqual(95, playlist.Count, "the Miku hangar ships 95 tracks");
            Assert.AreEqual(rows.Count, playlist.Count, "every exported row is a playable track");
            Assert.AreEqual(1, playlist.Tracks.Count(t => t.IsDefault), "exactly one default track");
            Assert.AreEqual("main_theme", playlist[playlist.DefaultIndex].Id);

            var folder = Path.Combine(Application.streamingAssetsPath, playlist.Folder);
            var files = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < playlist.Count; i++)
            {
                var track = playlist[i];
                var row = rows[i];
                Assert.AreEqual(track.Id, row["id"].AsString());
                Assert.IsFalse(string.IsNullOrEmpty(track.Title), track.Id + " has no title");
                Assert.IsTrue(files.Add(track.File), track.File + " is used twice");
                Assert.That(track.Loudness, Is.InRange(-70f, 0f), track.Id + " loudness");
                Assert.That(track.Seconds, Is.GreaterThan(1d), track.Id + " length");

                var path = Path.Combine(folder, track.File);
                Assert.IsTrue(File.Exists(path), path + " is missing");
                Assert.AreEqual(row["bytes"].AsDouble(), new FileInfo(path).Length, track.File + " size");
                using (var stream = File.OpenRead(path))
                {
                    var magic = new byte[4];
                    Assert.AreEqual(4, stream.Read(magic, 0, 4));
                    Assert.AreEqual("OggS", System.Text.Encoding.ASCII.GetString(magic), track.File + " is not Ogg");
                }
            }

            var shipped = Directory.GetFiles(folder, "*.ogg").Select(Path.GetFileName).ToArray();
            CollectionAssert.AreEquivalent(files.ToArray(), shipped, "no stray files in the build");
        }

        // ------------------------------------------------------------- settings

        [Test]
        public void ThePlayerStartsOnTheDefaultTrackAtTheSourceVolume()
        {
            var music = new SettingsData().HangarMusic;
            Assert.AreEqual(string.Empty, music.Track);
            Assert.AreEqual(HangarMusicSettings.DefaultVolume, music.Volume);
            Assert.IsFalse(music.Shuffle);
            Assert.IsFalse(music.RepeatOne);
            Assert.AreEqual(HangarMusicView.Player, music.View);
            Assert.IsFalse(music.Collapsed);
            Assert.IsFalse(music.Paused);
            Assert.IsFalse(music.Placed);
            Assert.IsFalse(music.EqOn);
            for (var band = 0; band < HangarMusicEqualizer.BandCount; band++)
            {
                Assert.AreEqual(0, music.EqGain(band));
            }

            var old = SettingsData.FromJson(JsonNode.Parse("{\"volume_music\": 0.5}"));
            Assert.AreEqual(HangarMusicSettings.DefaultVolume, old.HangarMusic.Volume, "saves from before the player");
            Assert.AreEqual(string.Empty, old.HangarMusic.Track);
        }

        [Test]
        public void ThePlayerStateRoundTripsThroughTheSettingsSave()
        {
            var settings = new SettingsData();
            settings.HangarMusic.Track = "shinkai";
            settings.HangarMusic.Volume = 0.35f;
            settings.HangarMusic.Shuffle = true;
            settings.HangarMusic.RepeatOne = true;
            settings.HangarMusic.View = HangarMusicView.Equalizer;
            settings.HangarMusic.Collapsed = true;
            settings.HangarMusic.Paused = true;
            settings.HangarMusic.EqOn = true;
            settings.HangarMusic.SetEqGain(0, 12);
            settings.HangarMusic.SetEqGain(4, -3);
            settings.HangarMusic.SetEqGain(9, -12);

            var restored = SettingsData.FromJson(JsonNode.Parse(settings.ToJson().ToJson())).HangarMusic;
            Assert.AreEqual("shinkai", restored.Track);
            Assert.AreEqual(0.35f, restored.Volume, 1e-6f);
            Assert.IsTrue(restored.Shuffle);
            Assert.IsTrue(restored.RepeatOne);
            Assert.AreEqual(HangarMusicView.Equalizer, restored.View);
            Assert.IsTrue(restored.Collapsed);
            Assert.IsTrue(restored.Paused);
            Assert.IsTrue(restored.EqOn);
            var gains = new int[HangarMusicEqualizer.BandCount];
            for (var band = 0; band < gains.Length; band++)
            {
                gains[band] = restored.EqGain(band);
            }

            CollectionAssert.AreEqual(new[] { 12, 0, 0, 0, -3, 0, 0, 0, 0, -12 }, gains);
        }

        [Test]
        public void EveryViewRoundTripsAndAnUnknownOneOpensThePlayer()
        {
            foreach (HangarMusicView view in Enum.GetValues(typeof(HangarMusicView)))
            {
                var music = new HangarMusicSettings { View = view };
                Assert.AreEqual(view, HangarMusicSettings.FromJson(JsonNode.Parse(music.ToJson().ToJson())).View);
            }

            Assert.AreEqual(HangarMusicView.Player,
                HangarMusicSettings.FromJson(JsonNode.Parse("{\"view\": \"lyrics\"}")).View);
            Assert.AreEqual(HangarMusicView.Player, HangarMusicSettings.FromJson(JsonNode.Parse("{\"view\": 1}")).View);
        }

        [Test]
        public void SavedEqualizerGainsAreHeldToTwelveDecibelsAndShortListsPadWithZero()
        {
            var music = HangarMusicSettings.FromJson(JsonNode.Parse(
                "{\"eq\": true, \"eq_gains\": [40, -40, 2.6, \"loud\", -1e300]}"));
            Assert.IsTrue(music.EqOn);
            Assert.AreEqual(12, music.EqGain(0));
            Assert.AreEqual(-12, music.EqGain(1));
            Assert.AreEqual(3, music.EqGain(2), "whole decibels");
            Assert.AreEqual(0, music.EqGain(3), "not a number");
            Assert.AreEqual(-12, music.EqGain(4));
            for (var band = 5; band < HangarMusicEqualizer.BandCount; band++)
            {
                Assert.AreEqual(0, music.EqGain(band));
            }

            var broken = HangarMusicSettings.FromJson(JsonNode.Parse("{\"eq\": 1, \"eq_gains\": {\"31\": 5}}"));
            Assert.AreEqual(0, broken.EqGain(0));
        }

        [Test]
        public void TheDraggedPlaceIsSavedOnlyOnceThePanelWasDragged()
        {
            var music = new HangarMusicSettings();
            music.Right = 50f;
            music.Top = 60f;
            var json = music.ToJson();
            Assert.IsFalse(json.Has("right") || json.Has("top"), "where the hangar puts it");
            Assert.IsFalse(HangarMusicSettings.FromJson(JsonNode.Parse(json.ToJson())).Placed);

            music.Placed = true;
            music.Right = 120.25f;
            music.Top = 48f;
            var restored = HangarMusicSettings.FromJson(JsonNode.Parse(music.ToJson().ToJson()));
            Assert.IsTrue(restored.Placed);
            Assert.AreEqual(120.25f, restored.Right, 1e-4f);
            Assert.AreEqual(48f, restored.Top, 1e-4f);

            // The panel holds a place off the screen back itself, so only nonsense is dropped.
            var outside = HangarMusicSettings.FromJson(JsonNode.Parse("{\"right\": -30, \"top\": 9000}"));
            Assert.IsTrue(outside.Placed);
            Assert.AreEqual(-30f, outside.Right);
            Assert.AreEqual(9000f, outside.Top);
            Assert.IsFalse(HangarMusicSettings.FromJson(JsonNode.Parse("{\"right\": 40}")).Placed, "one edge");
            Assert.IsFalse(HangarMusicSettings.FromJson(JsonNode.Parse("{\"right\": 40, \"top\": \"low\"}")).Placed);
            Assert.IsFalse(HangarMusicSettings.FromJson(JsonNode.Parse("{\"right\": 1e300, \"top\": 10}")).Placed);
        }

        [Test]
        public void TheVolumeStaysBetweenSilenceAndFull()
        {
            var music = new HangarMusicSettings();
            music.Volume = 1.7f;
            Assert.AreEqual(1f, music.Volume);
            music.Volume = -0.2f;
            Assert.AreEqual(0f, music.Volume);
            music.Volume = float.NaN;
            Assert.AreEqual(HangarMusicSettings.DefaultVolume, music.Volume);

            var loaded = HangarMusicSettings.FromJson(JsonNode.Parse("{\"volume\": 9, \"track\": 4}"));
            Assert.AreEqual(1f, loaded.Volume);
            Assert.AreEqual(HangarMusicSettings.DefaultVolume, HangarMusicSettings.FromJson(null).Volume);
        }
    }
}
