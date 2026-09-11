using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Ugui
{
    /// <summary>
    /// What a click at a screen point reaches, and what stops it reaching anything else.
    /// </summary>
    internal static class UiHitTest
    {
        /// <summary>
        /// Graphics examined when explaining why something under the point was not hit. A canvas
        /// with more elements than this is reported as bounded rather than scanned to the end.
        /// </summary>
        private const int MaxGraphicsScanned = 2000;

        public static string PathOf(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var parts = new List<string>();

            for (var t = go.transform; t != null; t = t.parent)
            {
                parts.Insert(0, t.name);
            }

            return "/" + string.Join("/", parts);
        }

        /// <summary>
        /// Why a Graphic under the point cannot be hit, in the order a reader should check them.
        /// Empty means nothing structural stops it, so it is either hit or covered by something
        /// drawn later.
        /// </summary>
        public static List<string> Blockers(Graphic graphic)
        {
            var reasons = new List<string>();

            if (graphic == null)
            {
                return reasons;
            }

            var go = graphic.gameObject;

            if (!go.activeInHierarchy)
            {
                reasons.Add("the object is not active");
            }

            if (!graphic.raycastTarget)
            {
                reasons.Add("Raycast Target is off");
            }

            var canvas = graphic.canvas;

            if (canvas == null)
            {
                reasons.Add("there is no Canvas above it");
            }
            else if (canvas.rootCanvas.GetComponent<BaseRaycaster>() == null)
            {
                reasons.Add($"the root Canvas '{canvas.rootCanvas.name}' has no GraphicRaycaster");
            }

            // A CanvasGroup applies to everything under it, and the walk stops at the first one
            // that ignores its parents, which is what the runtime does when it resolves a hit.
            for (var t = go.transform; t != null; t = t.parent)
            {
                var group = t.GetComponent<CanvasGroup>();

                if (group == null)
                {
                    continue;
                }

                if (!group.blocksRaycasts)
                {
                    reasons.Add($"the CanvasGroup on '{t.name}' has Blocks Raycasts off");
                }

                if (!group.interactable)
                {
                    reasons.Add($"the CanvasGroup on '{t.name}' is not interactable");
                }

                if (group.ignoreParentGroups)
                {
                    break;
                }
            }

            return reasons;
        }

        private static Graphic[] AllGraphics()
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType<Graphic>(FindObjectsInactive.Include);
#else
#pragma warning disable CS0618
            return Object.FindObjectsByType<Graphic>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore CS0618
#endif
        }

        private static JObject Describe(RaycastResult hit)
        {
            var go = hit.gameObject;
            var graphic = go == null ? null : go.GetComponent<Graphic>();
            var selectable = go == null ? null : go.GetComponentInParent<Selectable>();

            var entry = new JObject
            {
                ["path"] = PathOf(go),
                ["module"] = hit.module == null ? null : hit.module.GetType().Name,
                ["depth"] = hit.depth,
                ["sortingOrder"] = hit.sortingOrder,
            };

            if (graphic != null)
            {
                entry["graphic"] = graphic.GetType().Name;
                entry["raycastTarget"] = graphic.raycastTarget;
            }

            if (selectable != null)
            {
                entry["selectable"] = selectable.GetType().Name;
                entry["interactable"] = selectable.interactable;

                // A Button that is not interactable still swallows the click, so the caller has to
                // be told the difference between "nothing reached it" and "it ignored the click".
                if (!selectable.interactable)
                {
                    entry["note"] = "this takes the click and does nothing with it";
                }
            }

            return entry;
        }

        /// <summary>
        /// Runs the raycast and explains the outcome. <paramref name="wanted"/> is the object the
        /// caller expected to be clicked, or null when they only asked what is under the point.
        /// </summary>
        public static JObject Run(Vector2 point, GameObject wanted, int limit, bool includeReasons)
        {
            var system = EventSystem.current;
            var result = new JObject
            {
                ["screen"] = new JObject { ["width"] = Screen.width, ["height"] = Screen.height },
                ["point"] = new JArray(point.x, point.y),
            };

            if (wanted != null)
            {
                result["wanted"] = PathOf(wanted);
            }

            if (system == null)
            {
                result["eventSystem"] = JValue.CreateNull();
                result["hits"] = new JArray();
                result["verdict"] = "No EventSystem is active, so nothing in this scene can receive "
                    + "a pointer event at all. A uGUI scene needs one EventSystem object.";
                return result;
            }

            result["eventSystem"] = PathOf(system.gameObject);

            var hits = new List<RaycastResult>();
            system.RaycastAll(new PointerEventData(system) { position = point }, hits);

            var listed = new JArray();

            for (var i = 0; i < hits.Count && i < limit; i++)
            {
                listed.Add(Describe(hits[i]));
            }

            result["hits"] = listed;
            result["hitCount"] = hits.Count;

            if (hits.Count > limit)
            {
                result["truncated"] = true;
            }

            var front = hits.Count > 0 ? hits[0].gameObject : null;
            result["reaches"] = front == null ? JValue.CreateNull() : (JToken)PathOf(front);

            if (includeReasons)
            {
                result["notHit"] = NotHit(hits, out var scanned, out var bounded);
                result["graphicsScanned"] = scanned;

                if (bounded)
                {
                    result["note"] = $"Stopped after {MaxGraphicsScanned} graphics; 'notHit' is partial.";
                }
            }

            result["verdict"] = Verdict(hits, wanted, front);
            return result;
        }

        private static JArray NotHit(List<RaycastResult> hits, out int scanned, out bool bounded)
        {
            var hitObjects = new HashSet<GameObject>();

            foreach (var hit in hits)
            {
                hitObjects.Add(hit.gameObject);
            }

            var listed = new JArray();
            var graphics = AllGraphics();
            scanned = 0;
            bounded = false;

            foreach (var graphic in graphics)
            {
                if (scanned >= MaxGraphicsScanned)
                {
                    bounded = true;
                    break;
                }

                scanned++;

                if (hitObjects.Contains(graphic.gameObject))
                {
                    continue;
                }

                var reasons = Blockers(graphic);

                if (reasons.Count == 0)
                {
                    continue;
                }

                listed.Add(new JObject
                {
                    ["path"] = PathOf(graphic.gameObject),
                    ["reasons"] = new JArray(reasons),
                });
            }

            return listed;
        }

        internal static string Verdict(List<RaycastResult> hits, GameObject wanted, GameObject front)
        {
            if (wanted == null)
            {
                return hits.Count == 0
                    ? "Nothing is under this point. Either no Graphic covers it, or everything that "
                      + "does is listed under 'notHit' with the reason."
                    : $"A click here reaches '{PathOf(front)}'.";
            }

            if (front == wanted)
            {
                return $"A click here reaches '{PathOf(wanted)}'. Nothing is in the way.";
            }

            // Why the asked-about element cannot be hit is the answer worth leading with, and it
            // holds whether or not anything else was hit at this point.
            var blockers = Blockers(wanted.GetComponent<Graphic>());

            if (blockers.Count > 0)
            {
                return $"'{PathOf(wanted)}' cannot be hit: {string.Join("; ", blockers)}.";
            }

            var covering = new List<string>();

            foreach (var hit in hits)
            {
                if (hit.gameObject == wanted)
                {
                    break;
                }

                covering.Add(PathOf(hit.gameObject));
            }

            if (covering.Count > 0)
            {
                return $"'{PathOf(wanted)}' is under the point but {string.Join(", ", covering)} "
                    + "is drawn over it and takes the click. Turn Raycast Target off on the cover, "
                    + "or move it behind.";
            }

            if (wanted.GetComponent<Graphic>() == null)
            {
                return $"'{PathOf(wanted)}' has no Graphic, so there is nothing for a raycast to "
                    + "hit. A click lands on whichever Image or Text covers the point.";
            }

            return $"'{PathOf(wanted)}' is not under this point. Its rectangle is somewhere else; "
                + "call again without 'position' to aim at its centre.";
        }

        /// <summary>The screen point at the centre of an object's rectangle.</summary>
        /// <exception cref="McpToolException"><c>invalid_params</c> when it has no RectTransform.</exception>
        public static Vector2 CentreOf(GameObject go)
        {
            var rect = go.GetComponent<RectTransform>();

            if (rect == null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{PathOf(go)}' has no RectTransform, so it has no place on the screen. "
                    + "This argument takes a uGUI element; pass 'position' for a screen point.");
            }

            var canvas = go.GetComponentInParent<Canvas>();

            // An overlay canvas draws straight to the screen, and passing a camera for one puts the
            // point in the wrong place.
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

            return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        }
    }
}
