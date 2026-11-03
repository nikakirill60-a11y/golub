using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Fades a set of Modern widgets in and out together, on unscaled time.
    /// Every widget gets a CanvasGroup. A widget that is being hidden stops
    /// taking taps at once; one that is being shown takes them from the
    /// first frame of its fade.
    /// </summary>
    public sealed class ModernFade : MonoBehaviour
    {
        private readonly List<CanvasGroup> _groups = new List<CanvasGroup>();
        private bool _shown = true;
        private float _playhead = 1f;

        /// <summary>The length of a full fade, either way.</summary>
        public float Seconds = ModernTheme.TweenTime;

        /// <summary>Switches every widget off once it has faded out completely.</summary>
        public bool DeactivateWhenHidden;

        /// <summary>Where the fade is heading.</summary>
        public bool IsShown
        {
            get { return _shown; }
        }

        /// <summary>0 = faded out, 1 = fully drawn.</summary>
        public float Playhead
        {
            get { return _playhead; }
        }

        /// <summary>The alpha the widgets are drawn with right now.</summary>
        public float Alpha
        {
            get { return Smooth(_playhead); }
        }

        /// <summary>True while the fade is still moving.</summary>
        public bool IsFading
        {
            get { return _playhead != (_shown ? 1f : 0f); }
        }

        public int Count
        {
            get { return _groups.Count; }
        }

        /// <summary>
        /// Puts <paramref name="widget"/> under this fade and draws it in
        /// the fade's current state at once.
        /// </summary>
        public CanvasGroup Add(Component widget)
        {
            if (widget == null)
            {
                throw new ArgumentNullException("widget");
            }

            var group = widget.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = widget.gameObject.AddComponent<CanvasGroup>();
            }

            if (!_groups.Contains(group))
            {
                _groups.Add(group);
            }

            Apply();
            return group;
        }

        public bool Contains(Component widget)
        {
            if (widget == null)
            {
                return false;
            }

            for (var i = 0; i < _groups.Count; i++)
            {
                if (_groups[i] != null && _groups[i].gameObject == widget.gameObject)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Starts a fade towards <paramref name="shown"/>. A fade that is
        /// already running turns around from where it is.
        /// </summary>
        public void Show(bool shown)
        {
            _shown = shown;
            Apply();
        }

        /// <summary>Jumps to the end state, with no fade.</summary>
        public void ShowImmediate(bool shown)
        {
            _shown = shown;
            _playhead = shown ? 1f : 0f;
            Apply();
        }

        /// <summary>One step of the fade.</summary>
        public void Advance(float deltaSeconds)
        {
            var target = _shown ? 1f : 0f;
            if (_playhead == target)
            {
                return;
            }

            var step = Seconds > 0f ? Mathf.Max(deltaSeconds, 0f) / Seconds : 1f;
            _playhead = Mathf.MoveTowards(_playhead, target, step);
            Apply();
        }

        /// <summary>Smoothstep: the fade eases in and out.</summary>
        public static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - (2f * t));
        }

        private void Update()
        {
            // A long frame must not swallow the fade (MenuStyleRules.TweenStep).
            Advance(MenuStyleRules.TweenStep(Time.unscaledDeltaTime));
        }

        private void Apply()
        {
            var alpha = Alpha;
            var active = _shown || _playhead > 0f;
            for (var i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                if (group == null)
                {
                    continue;
                }

                group.alpha = alpha;
                group.blocksRaycasts = _shown;
                if (DeactivateWhenHidden && group.gameObject.activeSelf != active)
                {
                    group.gameObject.SetActive(active);
                }
            }
        }
    }
}
