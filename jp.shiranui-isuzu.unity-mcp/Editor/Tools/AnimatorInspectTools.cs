using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.Animations;

using UnityEngine;

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
        /// <summary>
        /// States reported from one layer. A layer holds its sub-state machines too, and each
        /// state brings its transitions and their conditions, so a rig's FX layer runs to hundreds.
        /// </summary>
        private const int MaxStates = 120;

        /// <summary>
        /// Transitions reported per state, and from Any State. A layer's size has two axes, and
        /// capping the states alone leaves this one free: six transitions on each of a hundred
        /// states is six hundred entries.
        /// </summary>
        private const int MaxTransitions = 24;

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
            "addressed as 'Machine/State'. In Play mode, naming an 'object_path' also reports what " +
            "that Animator is doing right now: the state each layer is in, how far through it is, " +
            "and what every parameter currently holds — which the asset cannot say. This reads and " +
            "changes nothing.",
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

            var described = string.IsNullOrWhiteSpace(layer)
                ? Overview(controller)
                : LayerDetail(controller, AnimatorResolve.LayerIndex(controller, layer));

            var running = Runtime(objectPath, controller);

            if (running != null)
            {
                described["runtime"] = running;
            }

            return described;
        }

        /// <summary>
        /// The controller, or a reply listing the candidates when a GameObject points at several.
        /// </summary>
        /// <remarks>
        /// Refusing would be the consistent thing for an editing tool, which is what
        /// <see cref="AnimatorResolve.Controller"/> does. For a read it is the wrong answer: the
        /// caller asked what is there, and "there are four of them" is that answer.
        /// </remarks>
        /// <summary>The name of the state a layer is in, or null when nothing matches.</summary>
        /// <remarks>
        /// IsName rather than a hash computed here: it is the API that knows how Unity spells a
        /// state's full path, including the sub-state machines a hand-built string would miss.
        /// </remarks>
        private static string StateName(
            AnimatorController controller, int layerIndex, AnimatorStateInfo state)
        {
            if (controller == null || layerIndex >= controller.layers.Length)
            {
                return null;
            }

            var layer = controller.layers[layerIndex];

            return Match(state, layer.name, layer.stateMachine, string.Empty);
        }

        /// <summary>Walks a machine and its children, looking for the state that is running.</summary>
        private static string Match(
            AnimatorStateInfo state, string layerName, AnimatorStateMachine machine, string prefix)
        {
            foreach (var child in machine.states)
            {
                var name = prefix + child.state.name;

                if (state.IsName(layerName + "." + name) || state.IsName(name))
                {
                    return name;
                }
            }

            // A sub-state machine's states are addressed through it, which is the spelling
            // animator_inspect uses for them elsewhere.
            foreach (var sub in machine.stateMachines)
            {
                var found = Match(state, layerName, sub.stateMachine, prefix + sub.stateMachine.name + ".");

                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// What the Animator on this object is doing, without the controller asset around it.
        /// </summary>
        /// <remarks>
        /// Watching an animation is a step and a look, and the look was a whole animator_inspect:
        /// twenty-one of them behind twenty-one steps in one run, each carrying the asset's states
        /// and transitions again for the sake of the few live numbers at the end of it. This is
        /// that end on its own, so play_mode_step can hand it back with the frame it belongs to.
        /// </remarks>
        internal static JObject LiveState(string objectPath)
        {
            var controller = ResolveOrList(null, objectPath, out var listing);

            return listing != null ? null : Runtime(objectPath, controller);
        }

        /// <summary>What the Animator on this object is doing, or null when nothing is playing.</summary>
        /// <remarks>
        /// The asset says what can happen; only the running Animator says what is happening. A
        /// gimmick that does not fire is usually in a state, or holding a parameter, that the
        /// controller alone cannot show — and reaching it meant execute_code.
        /// </remarks>
        private static JObject Runtime(string objectPath, AnimatorController controller)
        {
            if (!EditorApplication.isPlaying || string.IsNullOrWhiteSpace(objectPath))
            {
                return null;
            }

            var go = ObjectResolve.Object(objectPath, null, "object_path", null);
            var animator = go.GetComponent<Animator>();

            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return null;
            }

            var layers = new JArray();

            for (var i = 0; i < animator.layerCount; i++)
            {
                var state = animator.GetCurrentAnimatorStateInfo(i);

                var described = new JObject
                {
                    ["layer"] = animator.GetLayerName(i),
                    ["weight"] = animator.GetLayerWeight(i),
                    ["normalizedTime"] = state.normalizedTime,
                    ["loop"] = state.loop,
                    ["inTransition"] = animator.IsInTransition(i),
                };

                // The name, where the controller can supply one. A hash alone made "which state
                // is it in" answerable only by taking a second reading and comparing, which is a
                // roundabout way to learn something the controller already knows.
                var name = StateName(controller, i, state);

                if (name != null)
                {
                    described["state"] = name;
                }
                else
                {
                    described["stateHash"] = state.fullPathHash;
                }

                layers.Add(described);
            }

            var values = new JObject();

            foreach (var parameter in animator.parameters)
            {
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Bool:
                    case AnimatorControllerParameterType.Trigger:
                        values[parameter.name] = animator.GetBool(parameter.name);
                        break;

                    case AnimatorControllerParameterType.Int:
                        values[parameter.name] = animator.GetInteger(parameter.name);
                        break;

                    default:
                        values[parameter.name] = animator.GetFloat(parameter.name);
                        break;
                }
            }

            return new JObject
            {
                ["speed"] = animator.speed,
                ["layers"] = layers,
                ["parameters"] = values,

                // Only where the name could not be worked out. Saying it every time would be
                // noise on a reply that already answers the question.
                ["note"] = "A layer reported as stateHash rather than state is in something this "
                           + "controller does not list, such as a state added at runtime.",
            };
        }

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
            var stateTotal = 0;

            foreach (var entry in AnimatorResolve.States(machine))
            {
                // A layer is not a small unit: sub-state machines are walked too, and every state
                // carries its transitions and their conditions.
                if (++stateTotal > MaxStates)
                {
                    continue;
                }

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
                    state.transitions.Take(MaxTransitions)
                        .Select((t, i) => (object)AnimatorResolve.Transition(t, i, addressOf)).ToArray());

                if (state.transitions.Length > MaxTransitions)
                {
                    projected["transitionCount"] = state.transitions.Length;
                }

                states.Add(projected);
            }

            result["stateCount"] = stateTotal;
            result["states"] = states;

            if (stateTotal > MaxStates)
            {
                result["truncated"] = $"{stateTotal} states in this layer";
            }

            result["anyStateTransitions"] = new JArray(
                machine.anyStateTransitions.Take(MaxTransitions)
                    .Select((t, i) => (object)AnimatorResolve.Transition(t, i, addressOf)).ToArray());

            if (machine.anyStateTransitions.Length > MaxTransitions)
            {
                result["anyStateTransitionCount"] = machine.anyStateTransitions.Length;
            }

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
