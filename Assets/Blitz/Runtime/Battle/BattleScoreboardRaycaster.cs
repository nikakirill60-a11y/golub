using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Invisible scoreboard handles are input regions, not rendered graphics.
    /// GraphicRaycaster rejects culled/unprocessed transparent meshes; hit test
    /// the actual handle rectangles instead, keeping EventSystem arbitration
    /// with the controls, modal canvases and other fingers intact.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleScoreboardRaycaster : GraphicRaycaster
    {
        private Canvas _canvas;
        private Button _ally, _enemy;
        private readonly List<Component> _filters = new List<Component>();

        private int _sortingOrder;

        public void Bind(Button ally, Button enemy, int sortingOrder)
        {
            _canvas = GetComponent<Canvas>();
            _ally = ally;
            _enemy = enemy;
            _sortingOrder = sortingOrder;
        }

        /// <summary>
        /// EventSystem ranks modules by this before anything else. The stock
        /// GraphicRaycaster returns canvas.sortingOrder, and on the Android
        /// player a runtime-nested override canvas reports its root HUD
        /// canvas (BattleHud.SortingOrder) there, one below the controls
        /// canvas. The full screen touch_surface then won every tap on the
        /// list and the board never toggled; verified on a device via
        /// EventSystem.RaycastAll. Report the intended order explicitly.
        /// </summary>
        public override int sortOrderPriority
        {
            get { return Mathf.Max(_sortingOrder, base.sortOrderPriority); }
        }

        /// <summary>Tie-break after sortOrderPriority; never below the canvas.</summary>
        public override int renderOrderPriority
        {
            get { return int.MaxValue; }
        }

        public override void Raycast(PointerEventData eventData, List<RaycastResult> results)
        {
            if (_canvas == null || !_canvas.isActiveAndEnabled) return;
            var extended = eventData as ExtendedPointerEventData;
            if (extended != null && extended.displayIndex != _canvas.targetDisplay) return;
            var position = eventData.position;
            if (position.x < 0 || position.y < 0 || position.x > Screen.width || position.y > Screen.height) return;
            Hit(_ally, eventData, results);
            Hit(_enemy, eventData, results);
        }

        private void Hit(Button button, PointerEventData pointer, List<RaycastResult> results)
        {
            if (button == null || !button.IsActive() || !button.IsInteractable()) return;
            var graphic = button.targetGraphic;
            // Apply ancestor CanvasGroup/Mask filters, but deliberately
            // do not depend on CanvasRenderer.cull or graphic.depth.
            if (graphic == null || !graphic.raycastTarget ||
                !RectTransformUtility.RectangleContainsScreenPoint((RectTransform)button.transform, pointer.position, eventCamera) ||
                !AllowsRaycast(button.transform, pointer.position)) return;
            results.Add(new RaycastResult
            {
                gameObject = button.gameObject,
                module = this,
                index = results.Count,
                distance = 0,
                depth = button.transform.GetSiblingIndex(),
                sortingLayer = _canvas.sortingLayerID,
                sortingOrder = sortOrderPriority,
                screenPosition = pointer.position,
                displayIndex = _canvas.targetDisplay,
                worldPosition = button.transform.position,
                worldNormal = -button.transform.forward,
            });
        }

        private bool AllowsRaycast(Transform target, Vector2 position)
        {
            var ignoreParentGroups = false;
            while (target != null)
            {
                var stop = false;
                target.GetComponents(_filters);
                foreach (var component in _filters)
                {
                    var canvas = component as Canvas;
                    if (canvas != null && canvas.overrideSorting) stop = true;
                    var filter = component as ICanvasRaycastFilter;
                    if (filter == null) continue;
                    var behaviour = component as Behaviour;
                    if (behaviour != null && !behaviour.isActiveAndEnabled) continue;
                    var group = component as CanvasGroup;
                    if (group != null)
                    {
                        if (ignoreParentGroups) continue;
                        if (group.ignoreParentGroups) ignoreParentGroups = true;
                    }
                    if (!filter.IsRaycastLocationValid(position, eventCamera)) return false;
                }
                if (stop) break;
                target = target.parent;
            }
            return true;
        }
    }
}
