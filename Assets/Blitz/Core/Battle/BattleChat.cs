using System;
using System.Collections.Generic;
using System.Text;

using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>Battle chat channel; the byte goes over the wire as is.</summary>
    public enum ChatChannel : byte
    {
        Team = 0,
        All = 1,
    }

    /// <summary>Colour of a chat line or of a nick inside a system notice.</summary>
    public enum ChatTint : byte
    {
        Green = 0,
        Red = 1,
        Blue = 2,
        Yellow = 3,
    }

    /// <summary>System notices raised by the host; each is a localization key with one nick.</summary>
    public enum ChatNotice : byte
    {
        TeamKillKick = 1,
        TurnedBlue = 2,
    }

    /// <summary>
    /// SECURITY: receiver-side sanitizer of every chat string (text and nick)
    /// before it reaches a renderer. The 4.x client was hit by an RCE through
    /// the chat because markup was only filtered by the sender. Here the
    /// receiving client cleans the string itself, whatever the host or a
    /// patched peer sent, and the HUD renders it with rich text disabled.
    /// Markup characters are replaced with inert full-width look-alikes, so
    /// no HTML / uGUI rich text / BBCode / format placeholder survives.
    /// </summary>
    public static class ChatSanitizer
    {
        /// <summary>Longest message in characters (UTF-16 code units, surrogate safe).</summary>
        public const int MaxTextLength = 80;
        public const int MaxNickLength = 24;
        /// <summary>Hard cap of raw input looked at, to bound work on hostile payloads.</summary>
        public const int MaxRawLength = 512;

        public static string CleanText(string raw) { return Clean(raw, MaxTextLength); }
        public static string CleanNick(string raw) { return Clean(raw, MaxNickLength); }

        public static string Clean(string raw, int max)
        {
            if (string.IsNullOrEmpty(raw) || max <= 0) { return string.Empty; }
            var length = Math.Min(raw.Length, MaxRawLength);
            var sb = new StringBuilder(Math.Min(length, max));
            var pendingSpace = false;
            for (var i = 0; i < length; i++)
            {
                var c = raw[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < length && char.IsLowSurrogate(raw[i + 1]))
                    {
                        if (!Append(sb, ref pendingSpace, max, c, raw[i + 1])) { break; }
                        i++;
                    }
                    continue; // unpaired: dropped
                }
                if (char.IsLowSurrogate(c)) { continue; }
                if (IsWhitespace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (IsInvisible(c)) { continue; }
                if (!Append(sb, ref pendingSpace, max, Neutralize(c), '\0')) { break; }
            }
            return sb.ToString();
        }

        private static bool Append(StringBuilder sb, ref bool pendingSpace, int max, char a, char b)
        {
            var need = (b != '\0' ? 2 : 1) + (pendingSpace ? 1 : 0);
            if (sb.Length + need > max) { return false; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(a);
            if (b != '\0') { sb.Append(b); }
            return true;
        }

        private static bool IsWhitespace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f'
                || c == '\u00A0' || c == '\u2028' || c == '\u2029' || c == '\u3000'
                || (c >= '\u2000' && c <= '\u200A') || c == '\u202F' || c == '\u205F';
        }

        /// <summary>Controls, zero-width and bidi overrides (spoofing of nicks and text direction).</summary>
        private static bool IsInvisible(char c)
        {
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) { return true; }
            if (c == '\u00AD' || c == '\u034F' || c == '\u061C' || c == '\u180E') { return true; }
            if (c >= '\u200B' && c <= '\u200F') { return true; }
            if (c >= '\u202A' && c <= '\u202E') { return true; }
            if (c >= '\u2060' && c <= '\u206F') { return true; }
            if (c == '\uFEFF' || (c >= '\uFFF9' && c <= '\uFFFB')) { return true; }
            if (c >= '\uFE00' && c <= '\uFE0F') { return true; }
            return false;
        }

        private static char Neutralize(char c)
        {
            switch (c)
            {
                case '<': return '\u2039';
                case '>': return '\u203A';
                case '[': return '\uFF3B';
                case ']': return '\uFF3D';
                case '{': return '\uFF5B';
                case '}': return '\uFF5D';
                case '\\': return '\uFF3C';
                default: return c;
            }
        }
    }

    /// <summary>
    /// More than <see cref="Burst"/> messages within <see cref="Window"/>
    /// seconds mutes the sender for <see cref="MuteSeconds"/>. A muted line
    /// still leaves the sender's own input, but nobody else receives it.
    /// Runs on the sender (local echo only) and on the host (authoritative).
    /// </summary>
    public sealed class ChatSpamGuard
    {
        public const int Burst = 5;
        public const double Window = 30.0;
        public const double MuteSeconds = 10.0;

        private readonly Queue<double> _sent = new Queue<double>();
        private double _mutedUntil = double.NegativeInfinity;

        public bool IsMuted(double now) { return now < _mutedUntil; }
        public double MutedUntil { get { return _mutedUntil; } }

        /// <summary>True if the message may be delivered to other players.</summary>
        public bool Accept(double now)
        {
            if (double.IsNaN(now)) { return false; }
            if (IsMuted(now)) { return false; }
            while (_sent.Count > 0 && now - _sent.Peek() >= Window) { _sent.Dequeue(); }
            if (_sent.Count >= Burst)
            {
                _mutedUntil = now + MuteSeconds;
                _sent.Clear();
                return false;
            }
            _sent.Enqueue(now);
            return true;
        }

        public void Reset() { _sent.Clear(); _mutedUntil = double.NegativeInfinity; }
    }

    /// <summary>Colour rules of nicks and lines, from the reader's point of view.</summary>
    public static class ChatColors
    {
        /// <summary>
        /// Own team: green, or blue for a team-killer. Other team: always red,
        /// the blue status is only shown to the offender's allies.
        /// </summary>
        public static ChatTint For(int senderTeam, int readerTeam, bool senderBlue)
        {
            if (senderTeam != readerTeam) { return ChatTint.Red; }
            return senderBlue ? ChatTint.Blue : ChatTint.Green;
        }

        /// <summary>Team lines only reach the sender's team.</summary>
        public static bool Receives(ChatChannel channel, int senderTeam, int readerTeam)
        {
            return channel == ChatChannel.All || senderTeam == readerTeam;
        }
    }

    /// <summary>One line of the battle chat feed.</summary>
    public sealed class ChatLine
    {
        public bool System;
        public ChatChannel Channel;
        public ChatTint Tint;
        /// <summary>Tint of the mentioned nick inside a system notice.</summary>
        public ChatTint NickTint;
        public string Nick = string.Empty;
        /// <summary>Chat: the message. System: the text before the nick.</summary>
        public string Text = string.Empty;
        /// <summary>System only: the text after the nick.</summary>
        public string Suffix = string.Empty;
        public double Born;
        public double Lifetime;

        public double Expires { get { return Born + Lifetime; } }
    }

    /// <summary>
    /// What the HUD shows: at most <see cref="MaxVisible"/> lines, system
    /// notices first (on top, 15 s), chat below them (5 s). When a line runs
    /// out it fades quickly and disappears.
    /// </summary>
    public sealed class ChatFeed
    {
        public const int MaxVisible = 3;
        public const double ChatLifetime = 5.0;
        public const double SystemLifetime = 15.0;
        public const double FadeSeconds = 0.3;
        /// <summary>Lines kept in memory at most (older ones are destroyed).</summary>
        public const int MaxStored = 16;

        private readonly List<ChatLine> _lines = new List<ChatLine>();
        private readonly List<ChatLine> _visible = new List<ChatLine>(MaxVisible);

        public int StoredCount { get { return _lines.Count; } }

        public ChatLine AddChat(ChatChannel channel, ChatTint tint, string nick, string text, double now)
        {
            var clean = ChatSanitizer.CleanText(text);
            if (clean.Length == 0) { return null; }
            return Add(new ChatLine
            {
                Channel = channel, Tint = tint, NickTint = tint,
                Nick = ChatSanitizer.CleanNick(nick), Text = clean,
                Born = now, Lifetime = ChatLifetime,
            });
        }

        /// <summary>
        /// <paramref name="template"/> is a trusted localized string with one
        /// <c>{0}</c> for the nick; it is split, never passed to string.Format
        /// together with the untrusted nick.
        /// </summary>
        public ChatLine AddSystem(string template, string nick, ChatTint nickTint, double now)
        {
            template = template ?? string.Empty;
            var at = template.IndexOf("{0}", StringComparison.Ordinal);
            var before = at >= 0 ? template.Substring(0, at) : template;
            var after = at >= 0 ? template.Substring(at + 3) : string.Empty;
            return Add(new ChatLine
            {
                System = true, Channel = ChatChannel.All, Tint = ChatTint.Yellow, NickTint = nickTint,
                Nick = at >= 0 ? ChatSanitizer.CleanNick(nick) : string.Empty,
                Text = before, Suffix = after,
                Born = now, Lifetime = SystemLifetime,
            });
        }

        private ChatLine Add(ChatLine line)
        {
            _lines.Add(line);
            while (_lines.Count > MaxStored) { _lines.RemoveAt(0); }
            return line;
        }

        /// <summary>Drops expired lines; returns the visible ones top to bottom.</summary>
        public IList<ChatLine> Visible(double now)
        {
            _lines.RemoveAll(l => now >= l.Expires);
            _visible.Clear();
            // Newest system notices win the top rows; shown oldest first.
            var systems = 0;
            for (var i = _lines.Count - 1; i >= 0 && systems < MaxVisible; i--)
            {
                if (_lines[i].System) { _visible.Insert(0, _lines[i]); systems++; }
            }
            var room = MaxVisible - systems;
            var chat = new List<ChatLine>(room);
            for (var i = _lines.Count - 1; i >= 0 && chat.Count < room; i--)
            {
                if (!_lines[i].System) { chat.Insert(0, _lines[i]); }
            }
            _visible.AddRange(chat);
            return _visible;
        }

        /// <summary>1 while fresh, then a quick linear fade over the last <see cref="FadeSeconds"/>.</summary>
        public static float Alpha(ChatLine line, double now)
        {
            if (line == null) { return 0f; }
            var left = line.Expires - now;
            if (left <= 0) { return 0f; }
            if (left >= FadeSeconds) { return 1f; }
            return (float)(left / FadeSeconds);
        }

        public void Clear() { _lines.Clear(); _visible.Clear(); }
    }

    /// <summary>The "BATTLE CHAT" settings row, applied on the receiving side.</summary>
    public static class ChatPreferences
    {
        /// <summary>Whether a line on <paramref name="channel"/> is shown (and sent) under <paramref name="mode"/>.</summary>
        public static bool Accepts(int mode, ChatChannel channel)
        {
            switch (mode)
            {
                case SettingsData.ChatOff: return false;
                case SettingsData.ChatTeamOnly: return channel == ChatChannel.Team;
                default: return true;
            }
        }

        /// <summary>The input dialog can be opened at all.</summary>
        public static bool CanType(int mode)
        {
            return mode != SettingsData.ChatOff;
        }

        /// <summary>Blitz 4.8.0 Sfx/GUI_chat.fsb: chat_allies for the team, chat_all for everybody.</summary>
        public const string SoundTeam = "blitz/GUI_chat/chat_allies";
        public const string SoundAll = "blitz/GUI_chat/chat_all";
        /// <summary>The squad ping, unused by chat lines here, marks system notices.</summary>
        public const string SoundNotice = "blitz/GUI_chat/chat_squad";

        /// <summary>A burst of lines rings once, not once per line.</summary>
        public const double SoundCooldown = 0.15;

        public static string SoundFor(ChatChannel channel)
        {
            return channel == ChatChannel.Team ? SoundTeam : SoundAll;
        }
    }
}
