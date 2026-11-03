using BlitzLegacy.Core.Battle;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class BattleChatTests
    {
        // ----- sanitizer (receiver side, RCE class of bugs) ---------------------

        [TestCase("<color=red>hi</color>")]
        [TestCase("<script>alert(1)</script>")]
        [TestCase("[url=http://x]y[/url]")]
        [TestCase("<size=500><b>BIG</b></size>")]
        [TestCase("{0}{1:N}")]
        [TestCase("<img src=x onerror=alert(1)>")]
        public void MarkupNeverSurvives(string raw)
        {
            var clean = ChatSanitizer.CleanText(raw);
            StringAssert.DoesNotContain("<", clean);
            StringAssert.DoesNotContain(">", clean);
            StringAssert.DoesNotContain("[", clean);
            StringAssert.DoesNotContain("]", clean);
            StringAssert.DoesNotContain("{", clean);
            StringAssert.DoesNotContain("}", clean);
            Assert.IsNotEmpty(clean);
        }

        [Test]
        public void ControlsBidiAndZeroWidthAreDropped()
        {
            var clean = ChatSanitizer.CleanText("a\u0000b\u202Ec\u200Bd\u2066e\uFEFFf\u0007");
            Assert.AreEqual("abcdef", clean);
        }

        [Test]
        public void WhitespaceCollapsesAndTrims()
        {
            Assert.AreEqual("go left", ChatSanitizer.CleanText("  go\r\n\t  left \n"));
            Assert.AreEqual(string.Empty, ChatSanitizer.CleanText(" \n\u200B "));
            Assert.AreEqual(string.Empty, ChatSanitizer.CleanText(null));
        }

        [Test]
        public void LengthIsCappedWithoutSplittingSurrogates()
        {
            var raw = new string('x', ChatSanitizer.MaxTextLength - 1) + "\uD83D\uDE00";
            var clean = ChatSanitizer.CleanText(raw);
            Assert.AreEqual(ChatSanitizer.MaxTextLength - 1, clean.Length);
            Assert.AreEqual(ChatSanitizer.MaxTextLength, ChatSanitizer.CleanText(new string('y', 5000)).Length);
            Assert.AreEqual("ab", ChatSanitizer.CleanText("a\uD83Db"), "unpaired surrogate");
            Assert.AreEqual("\uD83D\uDE00", ChatSanitizer.CleanText("\uD83D\uDE00"));
        }

        [Test]
        public void CyrillicAndPunctuationAreKept()
        {
            Assert.AreEqual("Помогите, база! 50%?", ChatSanitizer.CleanText("Помогите, база! 50%?"));
        }

        // ----- spam guard -------------------------------------------------------

        [Test]
        public void SixthMessageWithinThirtySecondsMutesForTen()
        {
            var guard = new ChatSpamGuard();
            for (var i = 0; i < 5; i++) { Assert.IsTrue(guard.Accept(i)); }
            Assert.IsFalse(guard.Accept(5));
            Assert.IsTrue(guard.IsMuted(14.9));
            Assert.IsFalse(guard.Accept(14.9));
            Assert.IsTrue(guard.Accept(15.0));
        }

        [Test]
        public void SlowSenderIsNeverMuted()
        {
            var guard = new ChatSpamGuard();
            for (var i = 0; i < 50; i++) { Assert.IsTrue(guard.Accept(i * 6.0), "msg " + i); }
            Assert.IsFalse(guard.Accept(double.NaN));
        }

        // ----- colours ----------------------------------------------------------

        [Test]
        public void ColoursFollowReaderTeamAndBlueStatus()
        {
            Assert.AreEqual(ChatTint.Green, ChatColors.For(0, 0, false));
            Assert.AreEqual(ChatTint.Blue, ChatColors.For(0, 0, true));
            Assert.AreEqual(ChatTint.Red, ChatColors.For(0, 1, false));
            Assert.AreEqual(ChatTint.Red, ChatColors.For(0, 1, true), "enemies never see blue");
        }

        [Test]
        public void TeamLinesReachOnlyTheTeam()
        {
            Assert.IsTrue(ChatColors.Receives(ChatChannel.Team, 1, 1));
            Assert.IsFalse(ChatColors.Receives(ChatChannel.Team, 1, 0));
            Assert.IsTrue(ChatColors.Receives(ChatChannel.All, 1, 0));
        }

        // ----- feed -------------------------------------------------------------

        [Test]
        public void AtMostThreeLinesSystemOnTop()
        {
            var feed = new ChatFeed();
            feed.AddChat(ChatChannel.All, ChatTint.Green, "a", "1", 0);
            feed.AddChat(ChatChannel.All, ChatTint.Green, "b", "2", 0.1);
            feed.AddChat(ChatChannel.All, ChatTint.Green, "c", "3", 0.2);
            feed.AddSystem("Player {0} kicked", "A", ChatTint.Blue, 0.3);
            var visible = feed.Visible(0.4);
            Assert.AreEqual(3, visible.Count);
            Assert.IsTrue(visible[0].System);
            Assert.AreEqual("2", visible[1].Text);
            Assert.AreEqual("3", visible[2].Text);
        }

        [Test]
        public void ChatLivesFiveSecondsSystemFifteen()
        {
            var feed = new ChatFeed();
            feed.AddSystem("{0} left", "A", ChatTint.Red, 0);
            feed.AddChat(ChatChannel.Team, ChatTint.Green, "b", "hi", 0);
            Assert.AreEqual(2, feed.Visible(4.9).Count);
            Assert.AreEqual(1, feed.Visible(5.0).Count);
            Assert.AreEqual(1, feed.Visible(14.9).Count);
            Assert.AreEqual(0, feed.Visible(15.0).Count);
            Assert.AreEqual(0, feed.StoredCount, "expired lines are destroyed");
        }

        [Test]
        public void ExpiringLineFadesQuickly()
        {
            var line = new ChatLine { Born = 0, Lifetime = ChatFeed.ChatLifetime };
            Assert.AreEqual(1f, ChatFeed.Alpha(line, 4.6));
            Assert.AreEqual(0.5f, ChatFeed.Alpha(line, 4.85), 1e-4);
            Assert.AreEqual(0f, ChatFeed.Alpha(line, 5.0));
        }

        [Test]
        public void SystemTemplateIsSplitNotFormatted()
        {
            var feed = new ChatFeed();
            var line = feed.AddSystem("Игрок {0} исключён", "<b>{1}</b>", ChatTint.Blue, 0);
            Assert.AreEqual("Игрок ", line.Text);
            Assert.AreEqual(" исключён", line.Suffix);
            StringAssert.DoesNotContain("<", line.Nick);
            StringAssert.DoesNotContain("{", line.Nick);
            Assert.AreEqual(ChatTint.Yellow, line.Tint);
            Assert.AreEqual(ChatTint.Blue, line.NickTint);
        }

        [Test]
        public void EmptyAfterSanitizingIsNotShown()
        {
            var feed = new ChatFeed();
            Assert.IsNull(feed.AddChat(ChatChannel.All, ChatTint.Red, "x", "\u200B\u202E ", 0));
            Assert.AreEqual(0, feed.Visible(0).Count);
        }
    }
}
