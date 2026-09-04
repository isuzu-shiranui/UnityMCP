using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.Animations;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Reading an Animator Controller: its parameters and layers, and one layer's states in full.
    /// </summary>
    /// <remarks>
    /// The two shapes exist because a controller of twenty layers holding several hundred states
    /// cannot be returned whole and still be read. Without a layer the reply is counts and layer
    /// settings; with one it is that layer's states and every transition out of them.
    /// </remarks>
    internal static class AnimatorInspectTools
    {
        [McpTool(
            "animator_inspect",
            "Read an Animator Controller. Name the asset with 'path', or name a scene object with " +
            "'object_path' to reach the controller anything on it points at — the Animator, or a " +
            "component that holds several controllers, one per body layer. Without 'layer' this " +
            "returns every parameter with its type and default, and one line per layer: weight, " +
            "blending mode, whether it has a mask, how many states and how many sub-state machines. " +
            "It deliberately does not list states, because a twenty-layer controller has hundreds of " +
            "them. Name a 'layer' to get that layer's states with their motion, speed, Write " +
            "Defaults flag and position, every transition out of each with its conditions, and the " +
            "layer's Any State and Entry transitions. States inside sub-state machines are included, " +
            "addressed as 'Machine/State'. This reads and changes nothing.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Inspect(
            [McpArg("path", "Controller asset path, e.g. Assets/Animation/Avatar_FX.controller.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject, from scene_browse_hierarchy. Reads " +
                                   "the controller its components point at. When several point at " +
                                   "different controllers they are listed instead, for you to pick one " +
                                   "with 'path'.")]
            string objectPath = null,
            [McpArg("layer", "Narrow to one layer, by name or by index. A name matching several layers " +
                             "is refused; give the index. Without this no states are returned at all.")]
            string layer = null)
        {
            var controller = ResolveOrList(path, objectPath, out var listing);

            if (listing != null)
            {
                return listing;
            }

            return string.IsNullOrWhiteSpace(layer)
                ? Overview(controller)
                : LayerDetail(controller, AnimatorResolve.LayerIndex(controller, layer));
        }

        /// <summary>
        /// The controller, or a reply listing the candidates when a GameObject points at several.
        /// </summary>
        /// <remarks>
        /// Refusing would be the consistent thing for an editing tool, which is what
        /// <see cref="AnimatorResolve.Controller"/> does. For a read it is the wrong answer: the
        /// caller asked what is there, and "there are four of them" is that answer.
        /// </remarks>
        private static AnimatorController ResolveOrList(string path, string objectPath, out JObject listing)
        {
            listing = null;

            if (!string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(objectPath))
            {
                return AnimatorResolve.Controller(path, objectPath);
            }

            var go = ObjectResolve.Object(objectPath, null, "object_path", null);
            var found = AnimatorResolve.ControllersOn(go);

            if (found.Count == 1)
            {
                return found[0].Controller;
            }

            if (found.Count == 0)
            {
                throw new McpToolException(
                    "not_found",
                    $"Nothing on '{ObjectResolve.PathOf(go)}' points at an AnimatorController. An Animator " +
                    "with no controller assigned reads the same as no Animator at all here.");
            }

            listing = new JObject
            {
                ["objectPath"] = ObjectResolve.PathOf(go),
                ["controllerCount"] = found.Count,
                ["controllers"] = new JArray(found.Select(f => (object)new JObject
                {
                    ["path"] = AssetDatabase.GetAssetPath(f.Controller),
                    ["name"] = f.Controller.name,
                    ["component"] = f.Component,
                    ["property"] = f.Property,
                    ["layerCount"] = f.Controller.layers.Length,
                    ["parameterCount"] = f.Controller.parameters.Length,
                }).ToArray()),
                ["note"] = "Several components point at different controllers. Call again with one of " +
                           "these 'path' values.",
            };

            return null;
        }

        private static JObject Overview(AnimatorController controller)
        {
            var layers = controller.layers;

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["name"] = controller.name,
                ["parameterCount"] = controller.parameters.Length,
                ["layerCount"] = layers.Length,
                ["parameters"] = new JArray(controller.parameters.Select(p => (object)AnimatorResolve.Parameter(p)).ToArray()),
                ["layers"] = new JArray(layers.Select((l, i) => (object)AnimatorResolve.Layer(l, i)).ToArray()),
                ["note"] = "States are only returned for a named 'layer'. animator_audit reports the " +
                           "problems across every layer at once.",
            };
        }

        private static JObject LayerDetail(AnimatorController controller, int index)
        {
            var layer = controller.layers[index];
            var machine = layer.stateMachine;

            var result = new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["name"] = controller.name,
                ["layer"] = AnimatorResolve.Layer(layer, index),
            };

            if (machine == null)
            {
                result["states"] = new JArray();
                result["note"] = "This layer has no state machine at all, which is not a state Unity's " +
                                 "own editor can produce; it usually means the asset was written by hand.";

                return result;
            }

            var addressOf = AnimatorResolve.AddressLookup(machine);
            var defaultState = machine.defaultState;

            var states = new JArray();

            foreach (var entry in AnimatorResolve.States(machine))
            {
                var state = entry.State;

                var projected = new JObject
                {
                    ["path"] = entry.Path,
                    ["name"] = state.name,
                };

                AnimatorResolve.DescribeMotion(projected, state.motion);

                projected["speed"] = Math.Round(state.speed, 4);
                projected["writeDefaults"] = state.writeDefaultValues;
                projected["isDefault"] = state == defaultState;
                projected["position"] = new JObject
                {
                    ["x"] = Math.Round(entry.Position.x, 1),
                    ["y"] = Math.Round(entry.Position.y, 1),
                };

                if (!string.IsNullOrEmpty(state.tag))
                {
                    projected["tag"] = state.tag;
                }

                var behaviours = state.behaviours.Where(b => b != null).Select(b => (object)b.GetType().Name).ToArray();

                if (behaviours.Length > 0)
                {
                    projected["behaviours"] = new JArray(behaviours);
                }

                projected["transitions"] = new JArray(
                    state.transitions.Select((t, i) => (object)AnimatorResolve.Transition(t, i, addressOf)).ToArray());

                states.Add(projected);
            }

            result["stateCount"] = states.Count;
            result["states"] = states;

            result["anyStateTransitions"] = new JArray(
                machine.anyStateTransitions.Select((t, i) => (object)AnimatorResolve.Transition(t, i, addressOf)).ToArray());

            result["entryTransitions"] = new JArray(
                machine.entryTransitions.Select((t, i) => (object)AnimatorResolve.Transition(t, i, addressOf)).ToArray());

            var subMachines = AnimatorResolve.Machines(machine).Skip(1).ToArray();

            if (subMachines.Length > 0)
            {
                result["subStateMachines"] = new JArray(subMachines.Select(m => (object)new JObject
                {
                    ["path"] = m.Path,
                    ["stateCount"] = m.Machine.states.Length,
                    ["defaultState"] = m.Machine.defaultState == null ? null : (JToken)m.Machine.defaultState.name,
                    ["anyStateTransitionCount"] = m.Machine.anyStateTransitions.Length,
                }).ToArray());
            }

            return result;
        }
    }
}
