using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The respawn presentation of every mode that returns a crew (the Uprising perk offer,
    /// the Big Boss next role): a dimmed full-screen layer whose content lets the death play
    /// out for a beat, then fades in, rises into place and shrinks to fit a small viewport.
    /// The mode builds its own content under <see cref="Content"/>.
    /// </summary>
    public sealed class BattleRespawnFrame
    {
        /// <summary>The death plays out this long before the frame fades in.</summary>
        public const float DelaySeconds = 1f;

        public const float FadeSeconds = 0.22f;

        /// <summary>The content rises this far into place while it fades in.</summary>
        public const float Rise = 18f;

        /// <summary>Viewport margin kept around content that has to shrink.</summary>
        public static readonly Vector2 Margin = new Vector2(32f, 24f);

        public RectTransform Root { get; private set; }

        public RectTransform Content { get; private set; }

        public CanvasGroup Group { get; private set; }

        public float Alpha { get { return Group != null ? Group.alpha : 0f; } }

        /// <summary>The fade <paramref name="elapsed"/> seconds after the death.</summary>
        public static float Fade(double elapsed)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((float)((elapsed - DelaySeconds) / FadeSeconds)));
        }

        /// <summary>
        /// Builds the hidden layer under a full-screen mode root. A sorting order above zero
        /// gives it its own canvas and raycaster, above the controls layer, for a mode root
        /// that has none.
        /// </summary>
        public static BattleRespawnFrame Create(Transform parent, string name, int sortingOrder = 0)
        {
            var frame = new BattleRespawnFrame();
            frame.Root = UiFactory.Node(name, parent);
            GodotRect.FullRect(frame.Root);
            if (sortingOrder > 0)
            {
                var canvas = frame.Root.gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = sortingOrder;
                frame.Root.gameObject.AddComponent<GraphicRaycaster>();
            }
            frame.Group = frame.Root.gameObject.AddComponent<CanvasGroup>();
            frame.Group.alpha = 0f;
            frame.Group.interactable = false;
            var dim = UiFactory.Picture("dim", frame.Root, null);
            GodotRect.FullRect(dim.rectTransform);
            dim.color = new Color(0f, 0f, 0f, 0.45f);
            dim.raycastTarget = true;
            frame.Content = UiFactory.Node("content", frame.Root);
            frame.Root.gameObject.SetActive(false);
            return frame;
        }

        /// <summary>
        /// Shows the layer <paramref name="elapsed"/> seconds after the death with content of
        /// <paramref name="size"/>, or hides it. While shown it swallows the pointer; an
        /// interactive frame takes input once it has fully faded in. Returns the fade.
        /// </summary>
        public float Present(bool showing, double elapsed, Vector2 size, bool interactive)
        {
            if (Root.gameObject.activeSelf != showing) { Root.gameObject.SetActive(showing); }
            if (!showing)
            {
                Group.alpha = 0f;
                Group.interactable = false;
                Group.blocksRaycasts = false;
                return 0f;
            }

            var alpha = Fade(elapsed);
            Group.alpha = alpha;
            Group.interactable = interactive && alpha >= 0.99f;
            Group.blocksRaycasts = true;
            var parent = Root.parent as RectTransform;
            var available = (parent != null ? parent.rect.size : size) - Margin;
            var fit = Mathf.Min(1f, Mathf.Min(available.x / Mathf.Max(1f, size.x), available.y / Mathf.Max(1f, size.y)));
            Content.localScale = Vector3.one * Mathf.Max(0.01f, fit) * Mathf.Lerp(0.96f, 1f, alpha);
            Content.anchoredPosition = new Vector2(0f, -Rise * (1f - alpha));
            return alpha;
        }
    }
}
