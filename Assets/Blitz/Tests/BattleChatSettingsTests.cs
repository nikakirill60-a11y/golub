using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class BattleChatSettingsTests
    {
        [Test]
        public void ModeFiltersChannels()
        {
            Assert.IsTrue(ChatPreferences.Accepts(SettingsData.ChatOn, ChatChannel.Team));
            Assert.IsTrue(ChatPreferences.Accepts(SettingsData.ChatOn, ChatChannel.All));
            Assert.IsTrue(ChatPreferences.Accepts(SettingsData.ChatTeamOnly, ChatChannel.Team));
            Assert.IsFalse(ChatPreferences.Accepts(SettingsData.ChatTeamOnly, ChatChannel.All));
            Assert.IsFalse(ChatPreferences.Accepts(SettingsData.ChatOff, ChatChannel.Team));
            Assert.IsFalse(ChatPreferences.Accepts(SettingsData.ChatOff, ChatChannel.All));
            Assert.IsTrue(ChatPreferences.CanType(SettingsData.ChatTeamOnly));
            Assert.IsFalse(ChatPreferences.CanType(SettingsData.ChatOff));
        }

        [Test]
        public void SettingDefaultsOnClampsAndSaves()
        {
            var settings = new SettingsData();
            Assert.AreEqual(SettingsData.ChatOn, settings.BattleChat);
            settings.SetInt("battle_chat", 99);
            Assert.AreEqual(SettingsData.ChatOff, settings.BattleChat);
            settings.SetInt("battle_chat", SettingsData.ChatTeamOnly);
            var restored = SettingsData.FromJson(JsonNode.Parse(settings.ToJson().ToJson()));
            Assert.AreEqual(SettingsData.ChatTeamOnly, restored.BattleChat);
        }

        [TestCase(ChatPreferences.SoundTeam)]
        [TestCase(ChatPreferences.SoundAll)]
        [TestCase(ChatPreferences.SoundNotice)]
        public void ChatSoundsShipFromBlitz480(string clip)
        {
            var audio = BlitzResources.LoadAudio(clip);
            Assert.IsNotNull(audio, clip);
            Assert.Greater(audio.length, 0.1f);
            Assert.Less(audio.length, 1.5f);
        }

        [Test]
        public void ChannelsRingDifferently()
        {
            Assert.AreNotEqual(ChatPreferences.SoundFor(ChatChannel.Team), ChatPreferences.SoundFor(ChatChannel.All));
        }
    }
}
