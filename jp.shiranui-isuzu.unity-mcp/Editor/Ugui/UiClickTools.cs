using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Ugui
{
    internal static class UiClickTools
    {
        private static readonly Type TmpTextType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("TMPro.TMP_Text")).FirstOrDefault(type => type != null);

        [McpTool("ui_click", "Press a uGUI element through the EventSystem in play mode, without " +
            "Editor focus. Code that reads the input device directly does not see this click. " +
            "Supports Selectables and pointer down/up/click handlers on the target or its ancestors; " +
            "does not send drag or scroll. Refuses covered targets. Text snapshots include at most " +
            "2000 Text or TMP_Text components and report at most 50 changes.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Click(
            [McpArg("object_path", "Scene path of the element. Supply exactly one target.")] string objectPath = null,
            [McpArg("instance_id", "Target instance id.")] long? instanceId = null,
            [McpArg("text", "Exact visible label or object name of an active, interactable Selectable. Ambiguous matches are refused.")] string text = null,
            [McpArg("position", "Screen point [x, y], bottom-left origin.")] double[] position = null,
            [McpArg("normalized", "Read position as 0..1 screen fractions.")] bool normalized = false,
            [McpArg("wait_frames", "Frames to run after the click before reading text, stepped when paused; 0 to 1000.")] int waitFrames = 1,
            [McpArg("text_changes", "Include text changes after the click.")] bool textChanges = true)
        {
            if (!EditorApplication.isPlaying)
                throw new McpToolException("conflict", "uGUI clicking needs play mode: call play_mode_play first.", 409);
            if ((objectPath != null ? 1 : 0) + (instanceId.HasValue ? 1 : 0) + (text != null ? 1 : 0) + (position != null ? 1 : 0) != 1)
                throw new McpToolException("invalid_params", "Supply exactly one of object_path, instance_id, text, or position.");
            if (waitFrames < 0 || waitFrames > 1000)
                throw new McpToolException("invalid_params", "wait_frames must be between 0 and 1000.");

            var target = text != null ? FindLabel(text) : position == null ? ObjectResolve.Object(objectPath, instanceId) : null;
            Canvas.ForceUpdateCanvases();
            var point = UiTools.Point(position, normalized, target);
            var system = EventSystem.current;
            if (system == null)
                throw new McpToolException("conflict", "No EventSystem is active; no pointer event can be delivered.", 409);
            var data = new PointerEventData(system) { position = point, button = PointerEventData.InputButton.Left, pointerId = -1 };
            var hits = new List<RaycastResult>();
            system.RaycastAll(data, hits);
            var front = hits.Count == 0 ? null : hits[0].gameObject;
            if (front == null || (target != null && front != target && !front.transform.IsChildOf(target.transform)))
                throw new McpToolException("conflict", UiHitTest.Verdict(hits, target, front) + " Front hit: " + (UiHitTest.PathOf(front) ?? "none") + ".", 409);
            target = target != null ? target : front;
            var before = textChanges ? Snapshot() : null;
            var result = Deliver(system, target, hits[0], data);
            if (waitFrames == 0 || !(bool)result["clicked"])
            {
                if (before != null) result["textChanges"] = Difference(before, Snapshot());
                return result;
            }
            return new DeferredToolResult(FrameSequencer.Run(After(result, before, waitFrames), "ui_click"));
        }

        internal static GameObject FindLabel(string label)
        {
            var matches = Selectable.allSelectablesArray.Where(s => s.IsActive() && s.IsInteractable()
                && (s.name == label || s.GetComponentsInChildren<Component>().Any(c => TryText(c, out var value) && value == label)))
                .Select(s => s.gameObject).Distinct().ToArray();
            if (matches.Length != 1)
                throw new McpToolException(matches.Length == 0 ? "not_found" : "conflict",
                    matches.Length == 0 ? $"No active, interactable Selectable matches '{label}'."
                    : $"Several Selectables match '{label}': {string.Join(", ", matches.Select(UiHitTest.PathOf))}", matches.Length == 0 ? 404 : 409);
            return matches[0];
        }

        internal static JObject Deliver(EventSystem system, GameObject target, RaycastResult hit, PointerEventData data)
        {
            var front = hit.gameObject;
            var selectable = front.GetComponentInParent<Selectable>();
            var entered = new List<GameObject>();
            var result = new JObject { ["clicked"] = false, ["target"] = UiHitTest.PathOf(target),
                ["hit"] = UiHitTest.PathOf(front), ["at"] = new JArray(data.position.x, data.position.y) };
            bool Ready(string stage)
            {
                if (target != null && target.activeInHierarchy && front != null && front.activeInHierarchy
                    && (selectable == null || (selectable.IsActive() && selectable.IsInteractable()))) return true;
                result["stoppedAt"] = stage;
                result["reason"] = "The target became inactive or non-interactable.";
                return false;
            }
            data.pointerCurrentRaycast = hit;
            data.pointerPressRaycast = hit;
            data.pressPosition = data.position;
            data.eligibleForClick = true;
            data.clickCount = 1;
            data.clickTime = Time.unscaledTime;
            data.useDragThreshold = true;
            try
            {
                if (!Ready("enter")) return result;
                data.pointerEnter = front;
                for (var t = front.transform; t != null;)
                {
                    var parent = t.parent;
                    entered.Add(t.gameObject);
                    data.hovered.Add(t.gameObject);
                    ExecuteEvents.Execute(t.gameObject, data, ExecuteEvents.pointerEnterHandler);
                    if (!Ready("enter")) return result;
                    t = parent;
                }
                var selection = ExecuteEvents.GetEventHandler<ISelectHandler>(front);
                if (selection != system.currentSelectedGameObject) system.SetSelectedGameObject(null, data);
                if (!Ready("selection")) return result;
                data.pointerPress = ExecuteEvents.ExecuteHierarchy(front, data, ExecuteEvents.pointerDownHandler);
                if (!Ready("down")) return result;
                if (data.pointerPress == null) data.pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(front);
                data.rawPointerPress = front;
                result["handler"] = UiHitTest.PathOf(data.pointerPress);
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                if (!Ready("up")) return result;
                var release = ExecuteEvents.GetEventHandler<IPointerClickHandler>(front);
                if (release != null && release == data.pointerPress && data.eligibleForClick)
                {
                    ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerClickHandler);
                    result["clicked"] = true;
                }
                else
                {
                    result["stoppedAt"] = "click";
                    result["reason"] = "The release click handler does not match the press handler.";
                }
                return result;
            }
            finally
            {
                data.eligibleForClick = false;
                data.pointerPress = null;
                data.rawPointerPress = null;
                foreach (var go in entered) if (go != null) ExecuteEvents.Execute(go, data, ExecuteEvents.pointerExitHandler);
                data.pointerEnter = null;
                data.hovered.Clear();
            }
        }

        private static IEnumerator<FrameStep> After(JObject result, Dictionary<Component, (string path, string value)> before, int frames)
        {
            for (var i = 0; i < frames && EditorApplication.isPlaying; i++)
            {
                if (EditorApplication.isPaused) EditorApplication.Step();
                else
                {
                    var frame = Time.frameCount;
                    do { yield return FrameStep.Wait(); }
                    while (EditorApplication.isPlaying && !EditorApplication.isPaused && Time.frameCount == frame);
                    if (EditorApplication.isPlaying && Time.frameCount == frame) EditorApplication.Step();
                }
            }
            if (before != null) result["textChanges"] = Difference(before, Snapshot());
            yield return FrameStep.Done(result);
        }

        private static bool TryText(Component component, out string value)
        {
            value = null;
            if (component == null) return false;
            for (var type = component.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName != "UnityEngine.UI.Text" && type.FullName != "TMPro.TMP_Text") continue;
                value = (string)type.GetProperty("text").GetValue(component);
                return true;
            }
            return false;
        }

        internal static Dictionary<Component, (string path, string value)> Snapshot()
        {
            var result = new Dictionary<Component, (string, string)>();
            IEnumerable<Component> components = UnityEngine.Resources.FindObjectsOfTypeAll<Text>();
            if (TmpTextType != null)
                components = components.Concat(UnityEngine.Resources.FindObjectsOfTypeAll(TmpTextType).Cast<Component>());
            foreach (var c in components)
            {
                if (!c.gameObject.scene.IsValid() || EditorUtility.IsPersistent(c) || !TryText(c, out var value)) continue;
                result[c] = (UiHitTest.PathOf(c.gameObject), value);
                if (result.Count == 2000) break;
            }
            return result;
        }

        internal static JArray Difference(Dictionary<Component, (string path, string value)> before, Dictionary<Component, (string path, string value)> after)
        {
            var changes = new JArray();
            foreach (var key in before.Keys.Union(after.Keys))
            {
                before.TryGetValue(key, out var old);
                after.TryGetValue(key, out var current);
                if (old.value == current.value) continue;
                changes.Add(new JObject { ["path"] = current.path ?? old.path, ["from"] = old.value == null ? JValue.CreateNull() : new JValue(old.value), ["to"] = current.value == null ? JValue.CreateNull() : new JValue(current.value) });
                if (changes.Count == 50) break;
            }
            return changes;
        }
    }
}
