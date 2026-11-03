using System;
using System.Globalization;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The panel of the hangar music player in the hangar's main view, after
    /// the Panzer War Miku player: a see-through navy panel with a thin teal
    /// frame and corner brackets, in three views. The player shows the track
    /// and its artist, the progress bar, the transport in Spotify's order
    /// (shuffle, previous, play/pause, next, repeat one) and the volume; the
    /// playlist lists the tracks, and a press on one plays it; the equalizer
    /// has its switch, a reset and its ten bands. Under the playlist and the
    /// equalizer a bar keeps the track, its progress and a short transport;
    /// the two, taller, may rise over the hangar's controls, so their glass
    /// is solid and those controls do not show through their rows.
    ///
    /// The header holds the caption, the buttons of the two views, which open
    /// a view and on a second press go back to the player, and the fold,
    /// which puts the view away and keeps the title and a play/pause button.
    /// A view button unfolds a folded panel into its view.
    ///
    /// A drag on the header moves the panel anywhere inside the screen's safe
    /// area; the player saves where it was let go, and until the first drag the
    /// panel stands where the hangar puts it. The place is kept by the top
    /// edge, so folding, unfolding and changing the view leave the header
    /// where it is, and the panel only rises while its bottom would leave the
    /// area, or, where the hangar puts it, come down on the controls under it.
    ///
    /// The panel only shows the player and sends it commands; whether it is up
    /// at all is the hangar's call. Its glass takes the touches, so a press on
    /// it does not turn the hangar camera behind.
    /// </summary>
    public sealed class HangarMusicPanel : MonoBehaviour
    {
        public const float Width = 340f;
        public const float HeaderHeight = 36f;
        public const float ExpandedHeight = 160f;

        /// <summary>The height of the playlist and the equalizer: the header, the view and the bar under it.</summary>
        public const float TallHeight = HeaderHeight + ViewHeight + BarHeight;

        /// <summary>The height of a row of the playlist.</summary>
        public const float RowHeight = 40f;

        /// <summary>Miku's teal, #39C5BB.</summary>
        public static readonly Color Accent = new Color(0x39 / 255f, 0xC5 / 255f, 0xBB / 255f, 1f);

        private static readonly Color PanelColor = new Color(0.035f, 0.05f, 0.13f, 0.62f);

        /// <summary>
        /// The glass of the playlist and the equalizer: solid, so what they rise
        /// over does not show through; the UI blends in linear space, where even
        /// a nearly solid glass lets light text behind it through.
        /// </summary>
        private static readonly Color TallPanelColor = new Color(0.035f, 0.05f, 0.13f, 1f);
        private static readonly Color TextColor = new Color(0.93f, 0.97f, 1f, 1f);
        private static readonly Color DimColor = new Color(0.72f, 0.82f, 0.9f, 0.9f);
        private static readonly Color TrackColor = new Color(1f, 1f, 1f, 0.18f);
        private static readonly Color HandleColor = new Color(0.85f, 1f, 0.98f, 1f);
        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color LineColor = new Color(1f, 1f, 1f, 0.07f);

        /// <summary>The header's caption of each view, in the order of <see cref="HangarMusicView"/>.</summary>
        private static readonly string[] ViewCaptions =
        {
            "ui:hangar_music/title", "ui:hangar_music/playlist", "ui:hangar_music/equalizer",
        };

        private const float Inset = 14f;
        private const float TextWidth = Width - (2f * Inset);
        private const float ModeIconSize = 22f;
        private const float DotSize = 4f;

        // The header: the move mark and the caption, then from the right the
        // fold, the equalizer, the playlist with the counter, and the
        // play/pause that takes the counter's place on the folded panel.
        private const float HeaderButton = 32f;
        private const float HeaderButtonY = (HeaderHeight - HeaderButton) / 2f;
        private const float HeaderStep = 34f;
        private const float CaptionX = 38f;
        private const float CollapseX = Width - 38f;
        private const float EqualizerX = CollapseX - HeaderStep;
        private const float PlaylistWidth = 88f;
        private const float PlaylistX = EqualizerX - 2f - PlaylistWidth;
        private const float FoldedPlaylistX = EqualizerX - HeaderStep;
        private const float MiniPlayX = FoldedPlaylistX - HeaderStep;
        private const float CounterWidth = 58f;
        private const float CaptionWidth = PlaylistX - 2f - CaptionX;
        private const float FoldedCaptionWidth = MiniPlayX - 4f - CaptionX;

        // The player's transport row, spaced evenly around the play button.
        private const float ControlsY = 112f;
        private const float ButtonSize = 36f;
        private const float PlaySize = 40f;
        private const float ShuffleX = 8f;
        private const float PreviousX = 46f;
        private const float PlayX = 84f;
        private const float NextX = 126f;
        private const float RepeatX = 164f;
        private const float VolumeIconX = 208f;
        private const float VolumeX = 230f;

        // The playlist and the equalizer stand between the header and the bar.
        private const float ViewHeight = 200f;
        private const float BarHeight = 56f;

        // The playlist: rows under a clipped viewport, and a thin scrollbar.
        private const float ListX = 6f;
        private const float ListWidth = Width - 20f;
        private const float ScrollbarX = ListX + ListWidth + 4f;
        private const float ScrollbarWidth = 3f;
        private const float NumberWidth = 34f;
        private const float RowTextX = 40f;
        private const float RowTimeWidth = 46f;
        private const float RowTextWidth = ListWidth - RowTextX - RowTimeWidth - 6f;

        // The equalizer: the switch and the reset over ten columns of the
        // gain, the band's fader and its frequency.
        private const float SwitchWidth = 96f;
        private const float SwitchHeight = 28f;
        private const float SwitchY = 8f;
        private const float BandsY = 40f;
        private const float BandWidth = TextWidth / HangarMusicEqualizer.BandCount;
        private const float FaderY = 16f;
        private const float FaderHeight = 126f;
        private const float BandLabelHeight = 16f;
        private const int GainSteps = HangarMusicEqualizer.MaxGain - HangarMusicEqualizer.MinGain;

        // The bar: the progress along its top, the track, then previous, play/pause and next.
        private const float BarTop = HeaderHeight + ViewHeight;
        private const float BarButtonY = 12f;
        private const float BarNextX = Width - 42f;
        private const float BarPlayX = BarNextX - 36f;
        private const float BarPreviousX = BarPlayX - 36f;
        private const float BarTextWidth = BarPreviousX - 6f - Inset;

        private HangarMusicPlayer _player;
        private Func<bool> _shown;
        private LocalizationTable _lang;
        private AudioService _audio;
        private HangarMusicSprites _sprites;
        private Font _regular;
        private Font _bold;

        private RectTransform _visual;
        private Image _glass;
        private HangarMusicDragHandle _handle;
        private GameObject _separator;
        private Text _caption;
        private RectTransform _playlistButton;
        private Text _counter;
        private Image _listIcon;
        private Image _equalizerIcon;
        private GameObject _equalizerDot;
        private Image _chevron;
        private GameObject _miniPlay;
        private Image _miniPlayIcon;

        // The player.
        private GameObject _body;
        private Text _title;
        private Text _subtitle;
        private Text _elapsed;
        private Text _duration;
        private HangarMusicSlider _progress;
        private Image _playIcon;
        private GameObject _shuffleOff;
        private GameObject _shuffleOn;
        private GameObject _repeatOff;
        private GameObject _repeatOn;
        private HangarMusicSlider _volume;

        // The playlist.
        private GameObject _listView;
        private ScrollRect _scroll;
        private RectTransform _content;
        private ListRow[] _rows;

        // The equalizer.
        private GameObject _equalizerView;
        private Image _switchFrame;
        private Image _switchIcon;
        private Text _switchLabel;
        private CanvasGroup _bands;
        private HangarMusicSlider[] _faders;
        private Text[] _gains;

        // The bar under the playlist and the equalizer.
        private GameObject _bar;
        private RectTransform _barFill;
        private Text _barTitle;
        private Text _barSubtitle;
        private Image _barPlayIcon;

        // Where the hangar puts the panel until it is dragged, and the drag.
        private float _homeRight;
        private float _homeTop;
        private float _homeFloor;
        private float _height = ExpandedHeight;
        private Vector2 _dragFrom;
        private float _dragFromRight;
        private float _dragFromTop;
        private float _dragRight;
        private float _dragTop;

        // The layout the panel stands in: folded, or the view it shows.
        private bool _laid;
        private bool _laidFolded;
        private HangarMusicView _laidView;

        // What the labels show, so they are rebuilt only when it changes.
        private int _shownIndex = -2;
        private HangarMusicPlayer.PlayerStatus _shownStatus = HangarMusicPlayer.PlayerStatus.Off;
        private long _shownElapsed = -1;
        private long _shownDuration = -1;
        private int _shownSwitch = -1;
        private readonly int[] _shownGains = new int[HangarMusicEqualizer.BandCount];

        /// <summary>Whether the hangar has the panel up; see <see cref="Visible"/>.</summary>
        public bool Shown { get; private set; }

        /// <summary>Whether the panel is on the screen: up, and its view open.</summary>
        public bool Visible
        {
            get { return Shown && _visual != null && _visual.gameObject.activeInHierarchy; }
        }

        /// <summary>The text of the track line: the title, the folded header's or the bar's.</summary>
        public string TitleText
        {
            get
            {
                if (_laidFolded)
                {
                    return _caption.text;
                }

                return _laidView == HangarMusicView.Player ? _title.text : _barTitle.text;
            }
        }

        /// <summary>The line under the title: the artist, or what keeps the track from playing.</summary>
        public string SubtitleText
        {
            get { return _laidFolded || _laidView == HangarMusicView.Player ? _subtitle.text : _barSubtitle.text; }
        }

        public float Height
        {
            get { return _visual.rect.height; }
        }

        /// <summary>
        /// The panel on the screen. The component's own rect covers the whole
        /// parent: the room the panel is dragged in, which takes no touches.
        /// </summary>
        public RectTransform Frame
        {
            get { return _visual; }
        }

        /// <summary>How far the panel's right edge stands in from the parent's, in canvas units.</summary>
        public float Right { get; private set; }

        /// <summary>How far the panel's top edge stands down from the parent's, in canvas units.</summary>
        public float Top { get; private set; }

        /// <summary>Whether a finger or the mouse is moving the panel by its header.</summary>
        public bool Dragging
        {
            get { return _handle.Dragging; }
        }

        /// <summary>How far the playlist is scrolled: from the top of its first row to the viewport's, in canvas units.</summary>
        public float ListOffset
        {
            get { return _content.anchoredPosition.y; }
        }

        /// <summary>
        /// Builds the panel over the whole of <paramref name="parent"/>, which
        /// has to cover the screen, as the hangar's main view does; see
        /// <see cref="SetHome"/> for where it stands.
        /// </summary>
        public static HangarMusicPanel Create(
            RectTransform parent, HangarMusicPlayer player, Func<bool> shown,
            LocalizationTable lang, Font regular, Font bold, AudioService audio)
        {
            if (player == null || shown == null || lang == null)
            {
                throw new ArgumentNullException(player == null ? "player" : shown == null ? "shown" : "lang");
            }

            var host = UiFactory.Node("HangarMusicPanel", parent);
            var panel = host.gameObject.AddComponent<HangarMusicPanel>();
            panel._player = player;
            panel._shown = shown;
            panel._lang = lang;
            panel._audio = audio;
            panel._regular = regular;
            panel._bold = bold;
            panel._sprites = HangarMusicSprites.Shared;
            panel.Build(host);
            panel.Refresh();
            return panel;
        }

        /// <summary>
        /// Where the hangar puts the panel until it is dragged: its right and
        /// top edges <paramref name="right"/> and <paramref name="top"/> in
        /// from the parent's, in canvas units, and its bottom edge a margin
        /// over <paramref name="floor"/>, the height above the parent's bottom
        /// where the hangar's controls under it end. A view too tall for that
        /// room rises.
        /// </summary>
        public void SetHome(float right, float top, float floor)
        {
            _homeRight = right;
            _homeTop = top;
            _homeFloor = floor;
            if (Shown)
            {
                Place();
            }
        }

        /// <summary>The button of the playlist's row for track <paramref name="index"/>, or null while the row is out of sight.</summary>
        public Button Row(int index)
        {
            var row = FindRow(index);
            return row == null ? null : row.Button;
        }

        /// <summary>Whether the playlist's row for track <paramref name="index"/> is in sight and lit as the track on.</summary>
        public bool RowLit(int index)
        {
            var row = FindRow(index);
            return row != null && row.Light.activeSelf;
        }

        private void Update()
        {
            Refresh();
        }

        /// <summary>Brings the panel up to date with the player; it runs every frame on its own.</summary>
        public void Refresh()
        {
            var shown = _shown() && _player.Count > 0;
            Shown = shown;
            if (_visual.gameObject.activeSelf != shown)
            {
                _visual.gameObject.SetActive(shown);
            }

            if (!shown)
            {
                return;
            }

            var folded = _player.Collapsed;
            var view = _player.View;
            if (!_laid || folded != _laidFolded || view != _laidView)
            {
                Lay(folded, view);
            }

            var previous = _shownIndex;
            var index = _player.Index;
            var track = _player.Track;
            var trackChanged = index != previous;
            if (trackChanged)
            {
                _shownIndex = index;
                if (folded)
                {
                    Ellipsize(_caption, track.Title, FoldedCaptionWidth);
                }

                _counter.text = string.Format(
                    CultureInfo.InvariantCulture, _lang.Get("ui:hangar_music/counter"), index + 1, _player.Count);
                Ellipsize(_title, track.Title, TextWidth);
                Ellipsize(_barTitle, track.Title, BarTextWidth);
            }

            var status = _player.Status;
            if (trackChanged || status != _shownStatus)
            {
                _shownStatus = status;
                ShowStatus(_subtitle, TextWidth, status, track);
                ShowStatus(_barSubtitle, BarTextWidth, status, track);
            }

            var icon = status == HangarMusicPlayer.PlayerStatus.Paused ? _sprites.Play : _sprites.Pause;
            _playIcon.sprite = icon;
            _miniPlayIcon.sprite = icon;
            _barPlayIcon.sprite = icon;
            var equalizerOn = _player.EqualizerOn;
            if (_equalizerDot.activeSelf != equalizerOn)
            {
                _equalizerDot.SetActive(equalizerOn);
            }

            if (!folded)
            {
                switch (view)
                {
                    case HangarMusicView.Playlist:
                        ShowList(previous, trackChanged);
                        ShowBar();
                        break;
                    case HangarMusicView.Equalizer:
                        ShowEqualizer(equalizerOn);
                        ShowBar();
                        break;
                    default:
                        ShowPlayer();
                        break;
                }
            }

            Place();
        }

        /// <summary>Lays the panel out folded or in <paramref name="view"/>: its height, its parts and the header's buttons.</summary>
        private void Lay(bool folded, HangarMusicView view)
        {
            _laid = true;
            _laidFolded = folded;
            _laidView = view;
            var player = !folded && view == HangarMusicView.Player;
            var playlist = !folded && view == HangarMusicView.Playlist;
            var equalizer = !folded && view == HangarMusicView.Equalizer;
            _height = folded ? HeaderHeight : player ? ExpandedHeight : TallHeight;
            _visual.sizeDelta = new Vector2(Width, _height);
            _glass.color = playlist || equalizer ? TallPanelColor : PanelColor;
            _separator.SetActive(!folded);
            _body.SetActive(player);
            _listView.SetActive(playlist);
            _equalizerView.SetActive(equalizer);
            _bar.SetActive(playlist || equalizer);

            Box(_caption, CaptionX, 0f, folded ? FoldedCaptionWidth : CaptionWidth, HeaderHeight);
            _caption.color = folded ? TextColor : Accent;
            if (!folded)
            {
                Ellipsize(_caption, _lang.Get(ViewCaptions[(int)view]), CaptionWidth);
            }

            _miniPlay.SetActive(folded);
            _counter.gameObject.SetActive(!folded);
            Box(_playlistButton, folded ? FoldedPlaylistX : PlaylistX, HeaderButtonY,
                folded ? HeaderButton : PlaylistWidth, HeaderButton);
            _counter.color = playlist ? Accent : DimColor;
            _listIcon.color = playlist ? Accent : DimColor;
            _equalizerIcon.color = equalizer ? Accent : DimColor;
            _chevron.sprite = folded ? _sprites.ChevronDown : _sprites.ChevronUp;

            if (playlist)
            {
                // The playlist opens on the track that is on.
                _content.sizeDelta = new Vector2(0f, _player.Count * RowHeight);
                _scroll.StopMovement();
                ScrollTo(HangarMusicList.Centered(_player.Index, _player.Count, RowHeight, ViewHeight));
            }

            // The texts of the layout are filled in by the refresh that follows.
            _shownIndex = -2;
        }

        /// <summary>
        /// Stands the panel where the drag has it, where it was let go, or at
        /// the hangar's spot, held inside the safe area at its current height,
        /// and at the hangar's spot over its floor too. The saved place stays
        /// as it is, so a panel a small screen pushed in goes back out on a
        /// larger one, and a folded one back down. It runs with every refresh,
        /// which follows a turned or resized screen too.
        /// </summary>
        private void Place()
        {
            float right;
            float top;
            var home = false;
            if (_handle.Dragging)
            {
                right = _dragRight;
                top = _dragTop;
            }
            else if (_player.Placed)
            {
                right = _player.PlacedRight;
                top = _player.PlacedTop;
            }
            else
            {
                right = _homeRight;
                top = _homeTop;
                home = true;
            }

            var room = ((RectTransform)transform).rect;
            var safe = Screen.safeArea;
            float insetLeft;
            float insetTop;
            float insetRight;
            float insetBottom;
            HangarMusicPlacement.Insets(
                Screen.width, Screen.height, safe.x, safe.y, safe.width, safe.height, room.width, room.height,
                out insetLeft, out insetTop, out insetRight, out insetBottom);
            if (home)
            {
                insetBottom = Math.Max(insetBottom, _homeFloor);
            }

            HangarMusicPlacement.Clamp(
                room.width, room.height, insetLeft, insetTop, insetRight, insetBottom, Width, _height,
                ref right, ref top);
            Right = right;
            Top = top;
            var position = new Vector2(-right, -top);
            if (_visual.anchoredPosition != position)
            {
                _visual.anchoredPosition = position;
            }
        }

        private void OnDragBegan(Vector2 pointer)
        {
            _dragFrom = pointer;
            _dragFromRight = Right;
            _dragFromTop = Top;
            _dragRight = Right;
            _dragTop = Top;
        }

        private void OnDragMoved(Vector2 pointer)
        {
            // The parent's space has y up, and the edges are measured in from the top right.
            _dragRight = _dragFromRight - (pointer.x - _dragFrom.x);
            _dragTop = _dragFromTop - (pointer.y - _dragFrom.y);
            Place();
        }

        private void OnDragEnded()
        {
            _player.Place(Right, Top);
        }

        /// <summary>A view button: it unfolds a folded panel into its view, and otherwise opens the view or goes back to the player.</summary>
        private void Open(HangarMusicView view)
        {
            if (_player.Collapsed)
            {
                _player.SetView(view);
                _player.SetCollapsed(false);
                return;
            }

            _player.SetView(_player.View == view ? HangarMusicView.Player : view);
        }

        private void ShowStatus(Text label, float width, HangarMusicPlayer.PlayerStatus status, HangarMusicTrack track)
        {
            switch (status)
            {
                case HangarMusicPlayer.PlayerStatus.Paused:
                    label.color = DimColor;
                    label.text = _lang.Get("ui:hangar_music/paused");
                    break;
                case HangarMusicPlayer.PlayerStatus.Loading:
                    label.color = DimColor;
                    label.text = _lang.Get("ui:hangar_music/loading");
                    break;
                case HangarMusicPlayer.PlayerStatus.Unavailable:
                    label.color = Accent;
                    label.text = _lang.Get("ui:hangar_music/unavailable");
                    break;
                default:
                    label.color = Alpha(Accent, 0.9f);
                    Ellipsize(label, track.Artist, width);
                    break;
            }
        }

        private void ShowPlayer()
        {
            _shuffleOn.SetActive(_player.Shuffle);
            _shuffleOff.SetActive(!_player.Shuffle);
            _repeatOn.SetActive(_player.RepeatOne);
            _repeatOff.SetActive(!_player.RepeatOne);
            _volume.SetValue(_player.Volume);
            ShowTimes();
        }

        private void ShowTimes()
        {
            var duration = _player.Duration;
            var elapsed = _progress.Dragging ? _progress.Value * duration : _player.Elapsed;
            if (!_progress.Dragging)
            {
                _progress.SetValue(duration > 0d ? (float)(elapsed / duration) : 0f);
            }

            var whole = (long)Math.Floor(Math.Max(0d, elapsed));
            if (whole != _shownElapsed)
            {
                _shownElapsed = whole;
                _elapsed.text = HangarMusicPlaylist.FormatTime(elapsed);
            }

            var length = (long)Math.Floor(Math.Max(0d, duration));
            if (length != _shownDuration)
            {
                _shownDuration = length;
                _duration.text = HangarMusicPlaylist.FormatTime(duration);
            }
        }

        /// <summary>
        /// Lights the new track's row. The list follows the music while the
        /// row of the track before was in sight, and stays where a scroll left
        /// it otherwise.
        /// </summary>
        private void ShowList(int previous, bool trackChanged)
        {
            if (!trackChanged)
            {
                return;
            }

            var index = _player.Index;
            var offset = ListOffset;
            if (previous >= 0 && HangarMusicList.Shows(previous, RowHeight, ViewHeight, offset) &&
                !HangarMusicList.Shows(index, RowHeight, ViewHeight, offset))
            {
                _scroll.StopMovement();
                ScrollTo(HangarMusicList.Centered(index, _player.Count, RowHeight, ViewHeight));
                return;
            }

            ShowRows();
        }

        private void ScrollTo(float offset)
        {
            _content.anchoredPosition = new Vector2(0f, HangarMusicList.Clamp(offset, _player.Count, RowHeight, ViewHeight));
            ShowRows();
        }

        /// <summary>Hands the ring's rows to the tracks in sight and lights the one on.</summary>
        private void ShowRows()
        {
            int first;
            int last;
            HangarMusicList.Window(_player.Count, RowHeight, ViewHeight, ListOffset, out first, out last);
            var current = _player.Index;
            var ring = _rows.Length;
            for (var slot = 0; slot < ring; slot++)
            {
                // Track i always takes slot i % ring, so the rows in sight never share one.
                var row = _rows[slot];
                var index = first + ((((slot - first) % ring) + ring) % ring);
                var used = index <= last;
                if (row.Rect.gameObject.activeSelf != used)
                {
                    row.Rect.gameObject.SetActive(used);
                }

                if (!used)
                {
                    row.Index = -1;
                    continue;
                }

                if (row.Index != index)
                {
                    Fill(row, index);
                }

                var lit = index == current;
                row.Light.SetActive(lit);
                row.Number.color = lit ? Accent : DimColor;
                row.Title.color = lit ? Accent : TextColor;
            }
        }

        private void Fill(ListRow row, int index)
        {
            var track = _player.Playlist[index];
            row.Index = index;
            Box(row.Rect, 0f, index * RowHeight, ListWidth, RowHeight);
            row.Number.text = (index + 1).ToString(CultureInfo.InvariantCulture);
            Ellipsize(row.Title, track.Title, RowTextWidth);
            Ellipsize(row.Artist, track.Artist, RowTextWidth);
            row.Time.text = HangarMusicPlaylist.FormatTime(track.Seconds);
        }

        private ListRow FindRow(int index)
        {
            if (!_listView.activeInHierarchy)
            {
                return null;
            }

            foreach (var row in _rows)
            {
                if (row.Index == index && row.Rect.gameObject.activeSelf)
                {
                    return row;
                }
            }

            return null;
        }

        private void ShowEqualizer(bool on)
        {
            var flag = on ? 1 : 0;
            if (flag != _shownSwitch)
            {
                _shownSwitch = flag;
                var color = on ? Accent : DimColor;
                _switchIcon.color = color;
                _switchLabel.color = color;
                _switchLabel.text = _lang.Get(on ? "ui:hangar_music/eq_on" : "ui:hangar_music/eq_off");
                _switchFrame.color = Alpha(color, on ? 0.6f : 0.35f);
                _bands.alpha = on ? 1f : 0.5f;
            }

            for (var band = 0; band < _faders.Length; band++)
            {
                var gain = _player.EqualizerGain(band);
                if (!_faders[band].Dragging)
                {
                    _faders[band].SetValue((gain - HangarMusicEqualizer.MinGain) / (float)GainSteps);
                }

                if (gain != _shownGains[band])
                {
                    _shownGains[band] = gain;
                    _gains[band].text = HangarMusicEqualizer.GainLabel(gain);
                    _gains[band].color = gain == 0 ? DimColor : TextColor;
                }
            }
        }

        /// <summary>The progress along the top of the bar under the playlist and the equalizer.</summary>
        private void ShowBar()
        {
            var duration = _player.Duration;
            var progress = duration > 0d ? Mathf.Clamp01((float)(_player.Elapsed / duration)) : 0f;
            if (!Mathf.Approximately(_barFill.anchorMax.x, progress))
            {
                _barFill.anchorMax = new Vector2(progress, 1f);
            }
        }

        private static int FaderGain(float value)
        {
            return HangarMusicEqualizer.MinGain + Mathf.RoundToInt(value * GainSteps);
        }

        // -------------------------------------------------------------- build

        private void Build(RectTransform host)
        {
            // The host is the room the panel moves in; the panel hangs from its top right corner.
            GodotRect.FullRect(host);
            var visual = UiFactory.Node("Panel", host);
            visual.anchorMin = Vector2.one;
            visual.anchorMax = Vector2.one;
            visual.pivot = Vector2.one;
            visual.sizeDelta = new Vector2(Width, ExpandedHeight);
            visual.anchoredPosition = Vector2.zero;
            var glass = visual.gameObject.AddComponent<Image>();
            glass.color = PanelColor;
            glass.raycastTarget = true;
            _glass = glass;
            Stretch(UiFactory.Picture("Frame", visual, _sprites.Frame), Alpha(Accent, 0.55f));
            Stretch(UiFactory.Picture("Corners", visual, _sprites.Corners), Accent);
            _visual = visual;

            BuildHeader(host, visual);
            BuildPlayer(visual);
            BuildPlaylist(visual);
            BuildEqualizer(visual);
            BuildBar(visual);

            // Over the views, so the playlist scrolls under it.
            var separator = UiFactory.Picture("Separator", visual, null);
            separator.color = Alpha(Accent, 0.3f);
            Box(separator, Inset, HeaderHeight, TextWidth, 1f);
            _separator = separator.gameObject;
        }

        /// <summary>
        /// The header: the move mark, the caption, the playlist's button with
        /// the counter, the equalizer's, and the fold. It is the handle: a drag
        /// that starts on it, on its buttons too, moves the panel.
        /// </summary>
        private void BuildHeader(RectTransform host, RectTransform visual)
        {
            var header = UiFactory.Picture("Header", visual, null);
            header.color = Color.clear;
            header.raycastTarget = true;
            Box(header, 0f, 0f, Width, HeaderHeight);
            _handle = header.gameObject.AddComponent<HangarMusicDragHandle>();
            _handle.Area = host;
            _handle.Began += OnDragBegan;
            _handle.Moved += OnDragMoved;
            _handle.Ended += OnDragEnded;
            var bar = header.transform;
            Box(UiFactory.Picture("Move", bar, _sprites.Move), 12f, 8f, 20f, 20f).color = Accent;
            _caption = Label("Caption", bar, _bold, 15, Accent, TextAnchor.MiddleLeft,
                CaptionX, 0f, CaptionWidth, HeaderHeight);
            var miniPlay = MakeButton("MiniPlayButton", bar, MiniPlayX, HeaderButtonY, HeaderButton, HeaderButton,
                () => _player.TogglePause());
            _miniPlayIcon = Icon("Icon", miniPlay.transform, _sprites.Pause, TextColor, 18f);
            _miniPlay = miniPlay.gameObject;

            var playlist = MakeButton("PlaylistButton", bar, PlaylistX, HeaderButtonY, PlaylistWidth, HeaderButton,
                () => Open(HangarMusicView.Playlist));
            _playlistButton = (RectTransform)playlist.transform;
            _counter = Label("Counter", playlist.transform, _regular, 13, DimColor, TextAnchor.MiddleRight,
                0f, 0f, CounterWidth, HeaderButton);

            // The list mark keeps to the button's right end as it narrows on the folded panel.
            _listIcon = Icon("Icon", playlist.transform, _sprites.List, DimColor, 20f);
            var listIcon = _listIcon.rectTransform;
            listIcon.anchorMin = new Vector2(1f, 0.5f);
            listIcon.anchorMax = new Vector2(1f, 0.5f);
            listIcon.anchoredPosition = new Vector2(-HeaderButton / 2f, 0f);

            var equalizer = MakeButton("EqualizerButton", bar, EqualizerX, HeaderButtonY, HeaderButton, HeaderButton,
                () => Open(HangarMusicView.Equalizer));
            _equalizerIcon = Icon("Icon", equalizer.transform, _sprites.Equalizer, DimColor, 20f);
            _equalizerDot = Dot(equalizer.transform, 10f + (DotSize / 2f) + 1f);
            _equalizerDot.SetActive(false);

            var collapse = MakeButton("CollapseButton", bar, CollapseX, HeaderButtonY, HeaderButton, HeaderButton,
                () => _player.SetCollapsed(!_player.Collapsed));
            _chevron = Icon("Icon", collapse.transform, _sprites.ChevronUp, DimColor, 20f);
        }

        /// <summary>The player: the track, the progress bar, the transport and the volume.</summary>
        private void BuildPlayer(RectTransform visual)
        {
            var body = UiFactory.Node("Body", visual);
            GodotRect.FullRect(body);
            _body = body.gameObject;
            _title = Label("Title", body, _bold, 18, TextColor, TextAnchor.MiddleLeft, Inset, 42f, TextWidth, 24f);
            _subtitle = Label("Subtitle", body, _regular, 14, Alpha(Accent, 0.9f), TextAnchor.MiddleLeft,
                Inset, 66f, TextWidth, 18f);

            // Times past an hour run over the ends of the bar rather than vanish.
            _elapsed = Label("Elapsed", body, _regular, 13, DimColor, TextAnchor.MiddleLeft, Inset, 88f, 42f, 20f);
            _duration = Label("Duration", body, _regular, 13, DimColor, TextAnchor.MiddleRight,
                Width - Inset - 42f, 88f, 42f, 20f);
            _progress = HangarMusicSlider.Create("Progress", body, TrackColor, Accent, TextColor);
            Box(_progress.transform, 60f, 88f, Width - 120f, 20f);
            _progress.Committed += value => _player.Seek(value * _player.Duration);

            // Transport in Spotify's order: shuffle, previous, play/pause, next, repeat one.
            var buttonY = ControlsY + ((PlaySize - ButtonSize) / 2f);
            var shuffle = MakeButton("ShuffleButton", body, ShuffleX, buttonY, ButtonSize, ButtonSize,
                () => _player.SetShuffle(!_player.Shuffle));
            _shuffleOff = Icon("Off", shuffle.transform, _sprites.Shuffle, DimColor, ModeIconSize).gameObject;
            _shuffleOn = ModeOn(shuffle.transform, _sprites.Shuffle);
            var previous = MakeButton("PreviousButton", body, PreviousX, buttonY, ButtonSize, ButtonSize, () => _player.Previous());
            Icon("Icon", previous.transform, _sprites.Previous, TextColor, 20f);
            var play = MakeButton("PlayButton", body, PlayX, ControlsY, PlaySize, PlaySize, () => _player.TogglePause());
            _playIcon = Icon("Icon", play.transform, _sprites.Pause, TextColor, 28f);
            var next = MakeButton("NextButton", body, NextX, buttonY, ButtonSize, ButtonSize, () => _player.Next());
            Icon("Icon", next.transform, _sprites.Next, TextColor, 20f);
            var repeat = MakeButton("RepeatButton", body, RepeatX, buttonY, ButtonSize, ButtonSize,
                () => _player.SetRepeatOne(!_player.RepeatOne));
            _repeatOff = Icon("Off", repeat.transform, _sprites.Repeat, DimColor, ModeIconSize).gameObject;
            _repeatOn = ModeOn(repeat.transform, _sprites.RepeatOne);

            var rowMiddle = ControlsY + (PlaySize / 2f);
            Box(UiFactory.Picture("VolumeIcon", body, _sprites.Volume), VolumeIconX, rowMiddle - 9f, 18f, 18f).color = DimColor;
            _volume = HangarMusicSlider.Create("Volume", body, TrackColor, Alpha(Accent, 0.85f), HandleColor);
            Box(_volume.transform, VolumeX, rowMiddle - 10f, Width - Inset - VolumeX, 20f);
            _volume.Changed += value => _player.SetVolume(value);
            _volume.Committed += value =>
            {
                _player.SetVolume(value);
                _player.CommitVolume();
            };
        }

        /// <summary>
        /// The playlist: every track by its number, title, artist and length,
        /// the one on lit. Only a ring of rows is built, handed on to the
        /// tracks in sight as the list scrolls.
        /// </summary>
        private void BuildPlaylist(RectTransform visual)
        {
            var list = UiFactory.Node("Playlist", visual);
            Box(list, 0f, HeaderHeight, Width, ViewHeight);
            _listView = list.gameObject;

            var viewport = UiFactory.Node("Viewport", list);
            Box(viewport, ListX, 0f, ListWidth, ViewHeight);
            viewport.gameObject.AddComponent<RectMask2D>();
            var catcher = viewport.gameObject.AddComponent<Image>();
            catcher.color = Color.clear;
            catcher.raycastTarget = true;

            var content = UiFactory.Node("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = GodotRect.TopLeftPivot;
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            _content = content;

            _rows = new ListRow[HangarMusicList.RingSize(RowHeight, ViewHeight)];
            for (var slot = 0; slot < _rows.Length; slot++)
            {
                _rows[slot] = MakeRow(content);
            }

            _scroll = list.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = viewport;
            _scroll.content = content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 40f;
            _scroll.verticalScrollbar = MakeScrollbar(list);
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            _scroll.onValueChanged.AddListener(position => ShowRows());
            _listView.SetActive(false);
        }

        private ListRow MakeRow(RectTransform content)
        {
            var row = new ListRow();
            row.Button = MakeButton("Row", content, 0f, 0f, ListWidth, RowHeight, () => _player.Play(row.Index));
            row.Rect = (RectTransform)row.Button.transform;
            var light = UiFactory.Picture("Light", row.Rect, null);
            light.color = Alpha(Accent, 0.12f);
            GodotRect.FullRect(light.rectTransform);
            Box(UiFactory.Picture("Mark", light.transform, null), 0f, 6f, 3f, RowHeight - 12f).color = Accent;
            row.Light = light.gameObject;
            row.Number = Label("Number", row.Rect, _regular, 13, DimColor, TextAnchor.MiddleCenter,
                4f, 0f, NumberWidth - 4f, RowHeight);
            row.Title = Label("Title", row.Rect, _bold, 14, TextColor, TextAnchor.MiddleLeft,
                RowTextX, 3f, RowTextWidth, 18f);
            row.Artist = Label("Artist", row.Rect, _regular, 12, DimColor, TextAnchor.MiddleLeft,
                RowTextX, 21f, RowTextWidth, 16f);
            row.Time = Label("Time", row.Rect, _regular, 12, DimColor, TextAnchor.MiddleRight,
                RowTextX + RowTextWidth, 0f, RowTimeWidth, RowHeight);
            Box(UiFactory.Picture("Line", row.Rect, null), RowTextX, RowHeight - 1f, ListWidth - RowTextX - 6f, 1f).color =
                LineColor;
            row.Rect.gameObject.SetActive(false);
            return row;
        }

        /// <summary>A thin bar along the playlist's right edge, there only while the list is longer than its view.</summary>
        private static Scrollbar MakeScrollbar(Transform parent)
        {
            var track = UiFactory.Picture("Scrollbar", parent, null);
            track.color = LineColor;
            track.raycastTarget = true;
            Box(track, ScrollbarX, 4f, ScrollbarWidth, ViewHeight - 8f);
            var handle = UiFactory.Picture("Handle", track.transform, null);
            handle.color = Color.white;
            handle.raycastTarget = true;
            GodotRect.FullRect(handle.rectTransform);
            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = handle.rectTransform;
            bar.targetGraphic = handle;
            bar.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = Alpha(Accent, 0.7f);
            colors.highlightedColor = Accent;
            colors.pressedColor = HandleColor;
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = Alpha(Accent, 0.3f);
            colors.fadeDuration = 0.08f;
            bar.colors = colors;
            return bar;
        }

        /// <summary>
        /// The equalizer: its switch and its reset over the ten bands, each a
        /// fader with its gain over it and its frequency under it. The bands
        /// dim while it is off, and moving one switches it on.
        /// </summary>
        private void BuildEqualizer(RectTransform visual)
        {
            var view = UiFactory.Node("Equalizer", visual);
            Box(view, 0f, HeaderHeight, Width, ViewHeight);
            _equalizerView = view.gameObject;

            var power = MakeButton("PowerButton", view, Inset, SwitchY, SwitchWidth, SwitchHeight,
                () => _player.SetEqualizer(!_player.EqualizerOn));
            _switchFrame = ButtonFrame(power.transform);
            _switchIcon = Box(UiFactory.Picture("Icon", power.transform, _sprites.Power), 10f, 6f, 16f, 16f);
            _switchLabel = Label("Label", power.transform, _bold, 13, Accent, TextAnchor.MiddleLeft,
                32f, 0f, SwitchWidth - 36f, SwitchHeight);
            var reset = MakeButton("ResetButton", view, Width - Inset - SwitchWidth, SwitchY, SwitchWidth, SwitchHeight,
                () => _player.ResetEqualizer());
            ButtonFrame(reset.transform).color = Alpha(TextColor, 0.3f);
            Label("Label", reset.transform, _regular, 13, TextColor, TextAnchor.MiddleCenter,
                0f, 0f, SwitchWidth, SwitchHeight).text = _lang.Get("ui:hangar_music/eq_reset");

            var bands = UiFactory.Node("Bands", view);
            Box(bands, 0f, BandsY, Width, ViewHeight - BandsY);
            _bands = bands.gameObject.AddComponent<CanvasGroup>();

            // The ends of the faders' travel, +12 and -12 dB, and 0 dB between them.
            var top = FaderY + (HangarMusicSlider.HandleSize / 2f);
            var travel = FaderHeight - HangarMusicSlider.HandleSize;
            Box(UiFactory.Picture("Top", bands, null), Inset, top, TextWidth, 1f).color = LineColor;
            Box(UiFactory.Picture("Zero", bands, null), Inset, top + (travel / 2f), TextWidth, 1f).color = Alpha(Accent, 0.3f);
            Box(UiFactory.Picture("Bottom", bands, null), Inset, top + travel, TextWidth, 1f).color = LineColor;

            var kilohertz = _lang.Get("ui:hangar_music/khz");
            _faders = new HangarMusicSlider[HangarMusicEqualizer.BandCount];
            _gains = new Text[HangarMusicEqualizer.BandCount];
            for (var band = 0; band < HangarMusicEqualizer.BandCount; band++)
            {
                var x = Inset + (band * BandWidth);
                _gains[band] = Label("Gain" + band, bands, _regular, 11, DimColor, TextAnchor.MiddleCenter,
                    x, 0f, BandWidth, BandLabelHeight);
                var fader = HangarMusicSlider.Create(
                    "Band" + band, bands, TrackColor, Alpha(Accent, 0.85f), HandleColor, true, GainSteps, true);
                Box(fader.transform, x, FaderY, BandWidth, FaderHeight);
                var index = band;
                fader.Changed += value => _player.SetEqualizerGain(index, FaderGain(value));
                fader.Committed += value =>
                {
                    _player.SetEqualizerGain(index, FaderGain(value));
                    _player.CommitEqualizer();
                };
                _faders[band] = fader;
                _shownGains[band] = int.MinValue;
                Label("Frequency" + band, bands, _regular, 11, DimColor, TextAnchor.MiddleCenter,
                    x, FaderY + FaderHeight + 1f, BandWidth, BandLabelHeight).text =
                    HangarMusicEqualizer.FrequencyLabel(band, kilohertz);
            }

            _equalizerView.SetActive(false);
        }

        /// <summary>
        /// The bar under the playlist and the equalizer: the progress along its
        /// top, the track and its artist, and previous, play/pause and next.
        /// </summary>
        private void BuildBar(RectTransform visual)
        {
            var bar = UiFactory.Node("NowPlaying", visual);
            Box(bar, 0f, BarTop, Width, BarHeight);
            _bar = bar.gameObject;
            var line = UiFactory.Picture("Progress", bar, null);
            line.color = TrackColor;
            Box(line, Inset, 0f, TextWidth, 2f);
            var fill = UiFactory.Picture("Fill", line.transform, null);
            fill.color = Accent;
            _barFill = fill.rectTransform;
            _barFill.anchorMin = Vector2.zero;
            _barFill.anchorMax = new Vector2(0f, 1f);
            _barFill.offsetMin = Vector2.zero;
            _barFill.offsetMax = Vector2.zero;
            _barTitle = Label("BarTitle", bar, _bold, 14, TextColor, TextAnchor.MiddleLeft, Inset, 8f, BarTextWidth, 20f);
            _barSubtitle = Label("BarSubtitle", bar, _regular, 12, Alpha(Accent, 0.9f), TextAnchor.MiddleLeft,
                Inset, 28f, BarTextWidth, 18f);
            var previous = MakeButton("BarPreviousButton", bar, BarPreviousX, BarButtonY, HeaderButton, HeaderButton,
                () => _player.Previous());
            Icon("Icon", previous.transform, _sprites.Previous, TextColor, 18f);
            var play = MakeButton("BarPlayButton", bar, BarPlayX, BarButtonY, HeaderButton, HeaderButton,
                () => _player.TogglePause());
            _barPlayIcon = Icon("Icon", play.transform, _sprites.Pause, TextColor, 24f);
            var next = MakeButton("BarNextButton", bar, BarNextX, BarButtonY, HeaderButton, HeaderButton,
                () => _player.Next());
            Icon("Icon", next.transform, _sprites.Next, TextColor, 18f);
            _bar.SetActive(false);
        }

        /// <summary>
        /// A button with no plate of its own, as in the source: an invisible
        /// square hit area that lights up faintly under a hover or a press.
        /// </summary>
        private Button MakeButton(string name, Transform parent, float x, float y, float w, float h, Action action)
        {
            var hit = UiFactory.Picture(name, parent, null);
            hit.color = new Color(1f, 1f, 1f, 0.1f);
            hit.raycastTarget = true;
            Box(hit, x, y, w, h);
            var button = hit.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.ColorTint;
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = new Color(1f, 1f, 1f, 0f);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.6f);
            colors.pressedColor = Color.white;
            colors.selectedColor = new Color(1f, 1f, 1f, 0f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0f);
            colors.colorMultiplier = 2f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() =>
            {
                if (_audio != null)
                {
                    _audio.PlayButtonClick(AudioService.SoundSelect);
                }

                action();
                Refresh();
            });
            return button;
        }

        /// <summary>The thin frame of the equalizer's switch and reset.</summary>
        private Image ButtonFrame(Transform button)
        {
            var frame = UiFactory.Picture("Frame", button, _sprites.Frame);
            Stretch(frame, Alpha(Accent, 0.6f));
            frame.pixelsPerUnitMultiplier = 2f;
            return frame;
        }

        /// <summary>The lit state of a mode button: the icon in teal over a small square, as Spotify marks it.</summary>
        private static GameObject ModeOn(Transform button, Sprite sprite)
        {
            var on = Icon("On", button, sprite, Accent, ModeIconSize);
            Dot(on.transform, (ModeIconSize / 2f) + DotSize);
            on.gameObject.SetActive(false);
            return on.gameObject;
        }

        /// <summary>The small teal square that marks a mode or a switch on, <paramref name="below"/> under the middle of <paramref name="parent"/>.</summary>
        private static GameObject Dot(Transform parent, float below)
        {
            var dot = UiFactory.Picture("Dot", parent, null);
            dot.color = Accent;
            var rt = dot.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, -below);
            rt.sizeDelta = new Vector2(DotSize, DotSize);
            return dot.gameObject;
        }

        private static Image Icon(string name, Transform parent, Sprite sprite, Color color, float size)
        {
            var image = UiFactory.Picture(name, parent, sprite);
            image.color = color;
            var rt = image.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(size, size);
            return image;
        }

        private static Text Label(
            string name, Transform parent, Font font, int size, Color color, TextAnchor alignment,
            float x, float y, float w, float h)
        {
            var label = UiFactory.Label(name, parent, font, size, color, alignment, string.Empty);
            Box(label, x, y, w, h);
            var shadow = label.gameObject.AddComponent<Shadow>();
            shadow.effectColor = ShadowColor;
            shadow.effectDistance = new Vector2(1f, -1f);
            return label;
        }

        /// <summary>A rect at x, y from the top-left corner of the parent, y down.</summary>
        private static T Box<T>(T component, float x, float y, float w, float h) where T : Component
        {
            GodotRect.Apply((RectTransform)component.transform, 0f, 0f, 0f, 0f, x, y, x + w, y + h, GodotRect.TopLeftPivot);
            return component;
        }

        private static void Stretch(Image image, Color color)
        {
            image.type = Image.Type.Sliced;
            image.color = color;
            GodotRect.FullRect(image.rectTransform);
        }

        private static Color Alpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        /// <summary>Puts <paramref name="value"/> on one line of <paramref name="width"/>, cut with an ellipsis when it is longer.</summary>
        private static void Ellipsize(Text label, string value, float width)
        {
            value = value ?? string.Empty;
            if (Fits(label, value, width))
            {
                label.text = value;
                return;
            }

            var low = 0;
            var high = value.Length;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (Fits(label, Cut(value, middle), width))
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            label.text = Cut(value, low);
        }

        private static string Cut(string value, int length)
        {
            // Never between the halves of a surrogate pair.
            if (length > 0 && length < value.Length && char.IsLowSurrogate(value[length]))
            {
                length--;
            }

            return value.Substring(0, length).TrimEnd() + "\u2026";
        }

        private static bool Fits(Text label, string value, float width)
        {
            var settings = label.GetGenerationSettings(Vector2.zero);
            return label.cachedTextGeneratorForLayout.GetPreferredWidth(value, settings) / label.pixelsPerUnit <= width;
        }

        /// <summary>One row of the ring the playlist hands on as it scrolls, and the track it shows.</summary>
        private sealed class ListRow
        {
            public RectTransform Rect;
            public Button Button;
            public GameObject Light;
            public Text Number;
            public Text Title;
            public Text Artist;
            public Text Time;
            public int Index = -1;
        }
    }
}