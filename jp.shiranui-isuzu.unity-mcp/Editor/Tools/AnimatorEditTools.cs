using System;
using System.Collections.Generic;
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
    /// Editing an Animator Controller: layers, states, transitions, parameters and Write Defaults.
    /// </summary>
    /// <remarks>
    /// One tool per operation rather than one taking an action name, for the reason set out in
    /// <see cref="PlayModeTools"/>: a tool's classification has to describe every path through it,
    /// and lumping a read in with a write forces the read to take the stricter one. Here it also
    /// keeps each schema to the arguments its own operation needs, instead of a union in which most
    /// arguments are wrong for whatever the caller is doing.
    /// <para>
    /// Every write goes through <see cref="AnimatorResolve.RecordUndo"/> first, which records the
    /// controller and every object stored inside its asset file. Recording the controller alone is
    /// not enough: a layer, a state and a transition are separate objects inside that file, so their
    /// fields would sit outside the undo step.
    /// </para>
    /// </remarks>
    internal static class AnimatorEditTools
    {
        /// <summary>
        /// The two consequences a caller cannot see, repeated on every write.
        /// </summary>
        /// <remarks>
        /// A controller is not owned by the scene that happens to be open, and the change is on disk
        /// before the reply arrives. Undo reverses the first but not the second, which is the same
        /// shape as <c>material_set</c> and the same surprise.
        /// </remarks>
        private const string Shared =
            " The controller is a shared asset: every scene, prefab and character using it is changed " +
            "too, not just the one you are looking at. Its file is written to disk before this " +
            "returns. One Ctrl+Z reverses the whole call in memory, but not the file, so until " +
            "something saves again the file holds a change the Editor no longer shows.";

        // ── layers ────────────────────────────────────────────────────────────────

        [McpTool(
            "animator_add_layer",
            "Add a layer to an Animator Controller, with an empty state machine. Set 'weight' to 1 " +
            "unless the layer is meant to start switched off; this tool creates one at 1, and then " +
            "animates nothing however it is wired. The first layer's weight is ignored by Unity, " +
            "which always plays it at 1." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Add Animator Layer")]
        public static JObject AddLayer(
            [McpArg("path", "Controller asset path, e.g. Assets/Animation/Avatar_FX.controller.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject whose components point at the " +
                                   "controller, instead of naming the asset.")]
            string objectPath = null,
            [McpArg("name", "Name for the new layer. Nothing stops two layers sharing a name; " +
                            "animator_audit reports it when they do.")]
            string name = null,
            [McpArg("weight", "Default weight, 0 to 1.")]
            float weight = 1f,
            [McpArg("blending", "'Override' or 'Additive'.")]
            string blending = "Override",
            [McpArg("mask", "AvatarMask asset path to limit the layer to part of the body. Omit for none.")]
            string mask = null,
            [McpArg("index", "Position to insert at. Omit to append. Layer order decides which layer " +
                             "wins when two drive the same property.")]
            int? index = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpToolException("invalid_params", "'name' is required.");
            }

            var controller = AnimatorResolve.Controller(path, objectPath);
            var layers = controller.layers.ToList();
            var at = index ?? layers.Count;

            if (at < 0 || at > layers.Count)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'index' {at} is out of range; the controller has {layers.Count} layer(s), so 0 to {layers.Count} are insertable.");
            }

            var blendingMode = ParseBlending(blending);
            var avatarMask = RequireMask(mask);

            AnimatorResolve.RecordUndo(controller, "MCP Add Animator Layer");

            var machine = new AnimatorStateMachine
            {
                name = name,
                // Without this the state machine shows as a loose object beside the controller in the
                // Project window, which is not how Unity's own AddLayer leaves it.
                hideFlags = HideFlags.HideInHierarchy,
            };

            var assetPath = AssetDatabase.GetAssetPath(controller);

            if (!string.IsNullOrEmpty(assetPath))
            {
                AssetDatabase.AddObjectToAsset(machine, assetPath);
            }

            Undo.RegisterCreatedObjectUndo(machine, "MCP Add Animator Layer");

            layers.Insert(at, new AnimatorControllerLayer
            {
                name = name,
                stateMachine = machine,
                defaultWeight = weight,
                blendingMode = blendingMode,
                avatarMask = avatarMask,
            });

            controller.layers = layers.ToArray();

            var saved = AnimatorResolve.Save(controller);

            return new JObject
            {
                ["path"] = assetPath,
                ["layer"] = AnimatorResolve.Layer(controller.layers[at], at),
                ["layerCount"] = controller.layers.Length,
                ["savedToDisk"] = saved,
            };
        }

        [McpTool(
            "animator_remove_layer",
            "Remove a layer and everything in it: its states, its transitions and its state machine, " +
            "all of which are destroyed. Nothing else in the controller refers to a layer by index, " +
            "but a StateMachineBehaviour that drives layer weights does, and removing a layer " +
            "renumbers every layer after it." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Remove Animator Layer")]
        public static JObject RemoveLayer(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("layer", "Layer to remove, by name or by index.")]
            string layer = null)
        {
            var controller = AnimatorResolve.Controller(path, objectPath);
            var index = AnimatorResolve.LayerIndex(controller, layer);
            var target = controller.layers[index];
            var stateCount = target.stateMachine == null ? 0 : AnimatorResolve.States(target.stateMachine).Count();

            AnimatorResolve.RecordUndo(controller, "MCP Remove Animator Layer");

            controller.RemoveLayer(index);

            var saved = AnimatorResolve.Save(controller);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["removed"] = target.name,
                ["removedIndex"] = index,
                ["removedStates"] = stateCount,
                ["layerCount"] = controller.layers.Length,
                ["savedToDisk"] = saved,
                ["note"] = AnimatorResolve.Text(index < controller.layers.Length
                    ? $"Layers after index {index} moved down by one."
                    : null),
            };
        }

        // ── states ────────────────────────────────────────────────────────────────

        [McpTool(
            "animator_add_state",
            "Add a state to a layer. The first state added to an empty state machine becomes its " +
            "default state, which is where the layer starts. Unity makes the name unique within its " +
            "state machine, so the name it ended up with is reported back rather than the one asked " +
            "for. A state with no motion is legal and holds whatever the layer last wrote." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Add Animator State")]
        public static JObject AddState(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("layer", "Layer to add to, by name or by index.")]
            string layer = null,
            [McpArg("name", "Name for the new state.")]
            string name = null,
            [McpArg("motion", "AnimationClip asset path for the state to play. Omit for an empty state.")]
            string motion = null,
            [McpArg("write_defaults", "Write Defaults for the new state. Match the rest of the layer: " +
                                      "a layer whose states disagree animates differently depending on " +
                                      "what ran before it. animator_audit reports a layer that does.")]
            bool writeDefaults = true,
            [McpArg("position", "Where the node sits in the Animator window, as {x, y}. Cosmetic, but " +
                                "states stacked on one another are unreadable for the next person.")]
            JToken position = null,
            [McpArg("machine", "Sub-state machine to add into, by path, e.g. 'Gestures'. Omit for the " +
                               "layer's own state machine.")]
            string machine = null,
            [McpArg("make_default", "Make this the layer's default state, replacing the current one.")]
            bool makeDefault = false)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpToolException("invalid_params", "'name' is required.");
            }

            var controller = AnimatorResolve.Controller(path, objectPath);
            var index = AnimatorResolve.LayerIndex(controller, layer);
            var root = RequireStateMachine(controller, index);
            var target = RequireMachine(root, machine);
            var clip = RequireMotion(motion);

            AnimatorResolve.RecordUndo(controller, "MCP Add Animator State");

            var state = target.AddState(name, ParsePosition(position, target.states.Length));

            Undo.RegisterCreatedObjectUndo(state, "MCP Add Animator State");

            state.writeDefaultValues = writeDefaults;

            if (clip != null)
            {
                state.motion = clip;
            }

            if (makeDefault)
            {
                target.defaultState = state;
            }

            var saved = AnimatorResolve.Save(controller);

            var entry = AnimatorResolve.States(root).First(s => s.State == state);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["layer"] = controller.layers[index].name,
                ["state"] = entry.Path,
                ["name"] = state.name,
                ["renamed"] = !string.Equals(state.name, name, StringComparison.Ordinal),
                ["isDefault"] = target.defaultState == state,
                ["writeDefaults"] = state.writeDefaultValues,
                ["savedToDisk"] = saved,
            };
        }

        [McpTool(
            "animator_remove_state",
            "Remove a state. Unity also removes every transition that pointed at it, so the count of " +
            "those is reported: a layer can lose its only way into a region this way. Removing the " +
            "default state leaves the state machine picking another one, and which is not something " +
            "you choose." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Remove Animator State")]
        public static JObject RemoveState(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("layer", "Layer holding the state, by name or by index.")]
            string layer = null,
            [McpArg("state", "State to remove, by name, or by path when it sits in a sub-state " +
                             "machine, e.g. 'Gestures/Point'.")]
            string state = null)
        {
            var controller = AnimatorResolve.Controller(path, objectPath);
            var index = AnimatorResolve.LayerIndex(controller, layer);
            var root = RequireStateMachine(controller, index);
            var entry = AnimatorResolve.State(root, state);

            var wasDefault = entry.Machine.defaultState == entry.State;
            var incoming = CountIncoming(root, entry.State);

            AnimatorResolve.RecordUndo(controller, "MCP Remove Animator State");

            entry.Machine.RemoveState(entry.State);

            var saved = AnimatorResolve.Save(controller);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["layer"] = controller.layers[index].name,
                ["removed"] = entry.Path,
                ["wasDefaultState"] = wasDefault,
                ["removedIncomingTransitions"] = incoming,
                ["defaultState"] = entry.Machine.defaultState == null ? null : (JToken)entry.Machine.defaultState.name,
                ["stateCount"] = AnimatorResolve.States(root).Count(),
                ["savedToDisk"] = saved,
            };
        }

        [McpTool(
            "animator_set_state",
            "Change one state: what it plays, how fast, its Write Defaults flag, its tag, or where " +
            "its node sits. Only the arguments given are changed. To set Write Defaults across a " +
            "whole layer at once, which is what fixing a mixed layer needs, use " +
            "animator_set_write_defaults instead." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Set Animator State")]
        public static JObject SetState(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("layer", "Layer holding the state, by name or by index.")]
            string layer = null,
            [McpArg("state", "State to change, by name or by path within the layer.")]
            string state = null,
            [McpArg("motion", "AnimationClip asset path to play. Pass an empty string to clear the " +
                              "motion and leave the state empty.")]
            string motion = null,
            [McpArg("speed", "Playback speed multiplier.")]
            float? speed = null,
            [McpArg("write_defaults", "Write Defaults for this state alone.")]
            bool? writeDefaults = null,
            [McpArg("tag", "State tag, which scripts read with Animator.GetCurrentAnimatorStateInfo.")]
            string tag = null,
            [McpArg("position", "Where the node sits in the Animator window, as {x, y}.")]
            JToken position = null,
            [McpArg("make_default", "Make this the default state of the state machine holding it.")]
            bool makeDefault = false)
        {
            var controller = AnimatorResolve.Controller(path, objectPath);
            var index = AnimatorResolve.LayerIndex(controller, layer);
            var root = RequireStateMachine(controller, index);
            var entry = AnimatorResolve.State(root, state);
            var target = entry.State;

            var clearMotion = motion != null && motion.Length == 0;
            var clip = clearMotion ? null : RequireMotion(motion);

            AnimatorResolve.RecordUndo(controller, "MCP Set Animator State");

            var changed = new JArray();

            if (clearMotion)
            {
                target.motion = null;
                changed.Add("motion cleared");
            }
            else if (clip != null)
            {
                target.motion = clip;
                changed.Add($"motion = {clip.name}");
            }

            if (speed.HasValue)
            {
                target.speed = speed.Value;
                changed.Add($"speed = {speed.Value}");
            }

            if (writeDefaults.HasValue)
            {
                target.writeDefaultValues = writeDefaults.Value;
                changed.Add($"write_defaults = {writeDefaults.Value.ToString().ToLowerInvariant()}");
            }

            if (tag != null)
            {
                target.tag = tag;
                changed.Add($"tag = {tag}");
            }

            if (position != null)
            {
                // The position lives on the ChildAnimatorState struct in the machine's array, not on
                // the state object, so it only sticks when the array is written back.
                var child = entry.Machine.states[entry.Index];
                child.position = ParsePosition(position, entry.Index);
                AnimatorResolve.ReplaceState(entry.Machine, entry.Index, child);
                changed.Add($"position = ({child.position.x}, {child.position.y})");
            }

            if (makeDefault)
            {
                entry.Machine.defaultState = target;
                changed.Add("default state");
            }

            if (changed.Count == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Nothing to do: pass at least one of 'motion', 'speed', 'write_defaults', 'tag', " +
                    "'position' or 'make_default'.");
            }

            var saved = AnimatorResolve.Save(controller);

            var result = new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["layer"] = controller.layers[index].name,
                ["state"] = entry.Path,
                ["changed"] = changed,
                ["savedToDisk"] = saved,
            };

            AnimatorResolve.DescribeMotion(result, target.motion);

            return result;
        }

        [McpTool(
            "animator_set_write_defaults",
            "Set Write Defaults on every state of one layer, or of the whole controller. This is the " +
            "fix for what animator_audit calls mixed Write Defaults: a layer whose states disagree " +
            "plays differently depending on which state ran before, and only in play mode, so it " +
            "cannot be seen in the Animator window. Which value to settle on is a decision about the " +
            "whole character, not one layer: with Write Defaults off, every property a state does " +
            "not animate keeps whatever the last state left, and the character depends on something " +
            "else putting those properties back." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Set Animator Write Defaults")]
        public static JObject SetWriteDefaults(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("write_defaults", "The value to give every state in scope.")]
            bool writeDefaults = true,
            [McpArg("layer", "Layer to change, by name or by index. Omit to change every layer in the " +
                             "controller.")]
            string layer = null)
        {
            var controller = AnimatorResolve.Controller(path, objectPath);
            var layers = controller.layers;

            var scope = string.IsNullOrWhiteSpace(layer)
                ? Enumerable.Range(0, layers.Length).ToArray()
                : new[] { AnimatorResolve.LayerIndex(controller, layer) };

            AnimatorResolve.RecordUndo(controller, "MCP Set Animator Write Defaults");

            var perLayer = new JArray();
            var total = 0;

            foreach (var index in scope)
            {
                var machine = layers[index].stateMachine;

                if (machine == null)
                {
                    continue;
                }

                var changed = 0;

                foreach (var entry in AnimatorResolve.States(machine))
                {
                    if (entry.State.writeDefaultValues == writeDefaults)
                    {
                        continue;
                    }

                    entry.State.writeDefaultValues = writeDefaults;
                    changed++;
                }

                total += changed;

                perLayer.Add(new JObject
                {
                    ["index"] = index,
                    ["layer"] = layers[index].name,
                    ["changed"] = changed,
                    ["states"] = AnimatorResolve.States(machine).Count(),
                });
            }

            var saved = AnimatorResolve.Save(controller);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["writeDefaults"] = writeDefaults,
                ["statesChanged"] = total,
                ["layers"] = perLayer,
                ["savedToDisk"] = saved,
            };
        }

        // ── transitions ───────────────────────────────────────────────────────────

        [McpTool(
            "animator_add_transition",
            "Add a transition between two states, or from Any State when 'from_state' is left out. " +
            "Conditions are given as objects: parameter, mode, and a threshold for the modes that " +
            "compare a number. A transition with no condition and no exit time fires the moment its " +
            "source state is entered, which is almost never what was meant; the reply says so when " +
            "one is created. Transitions into or out of a sub-state machine, and transitions to Exit, " +
            "are not created here." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Add Animator Transition",
            Examples = new[]
            {
                "{\"path\":\"Assets/Animation/Avatar_FX.controller\",\"layer\":\"Hands\",\"from_state\":\"Idle\",\"to_state\":\"Fist\",\"conditions\":[{\"parameter\":\"GestureLeft\",\"mode\":\"Equals\",\"threshold\":1}]}",
                "{\"path\":\"Assets/Animation/Avatar_FX.controller\",\"layer\":\"Toggles\",\"to_state\":\"On\",\"conditions\":[{\"parameter\":\"HatOn\",\"mode\":\"If\"}]}",
            })]
        public static JObject AddTransition(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("layer", "Layer holding both states, by name or by index.")]
            string layer = null,
            [McpArg("from_state", "Source state, by name or by path. Omit to make it an Any State " +
                                  "transition, which can fire from anywhere in the layer.")]
            string fromState = null,
            [McpArg("to_state", "Destination state, by name or by path.")]
            string toState = null,
            [McpArg("conditions", "Array of {parameter, mode, threshold}. Modes: If and IfNot for a " +
                                  "bool or trigger, Greater and Less for a float or int, Equals and " +
                                  "NotEqual for an int. The threshold is ignored by If and IfNot.")]
            JToken conditions = null,
            [McpArg("has_exit_time", "Let the transition fire when the source clip reaches 'exit_time', " +
                                     "with no condition needed.")]
            bool hasExitTime = false,
            [McpArg("exit_time", "Point in the source clip the transition may start, in normalised " +
                                 "time: 1 is the end of one loop.")]
            float exitTime = 0.75f,
            [McpArg("duration", "Blend length. In seconds by default, or a fraction of the source " +
                                "clip when 'has_fixed_duration' is false.")]
            float duration = 0.25f,
            [McpArg("has_fixed_duration", "Read 'duration' as seconds rather than as a fraction of the " +
                                          "source clip.")]
            bool hasFixedDuration = true)
        {
            var controller = AnimatorResolve.Controller(path, objectPath);
            var index = AnimatorResolve.LayerIndex(controller, layer);
            var root = RequireStateMachine(controller, index);

            var destination = AnimatorResolve.State(root, toState, "to_state");
            var parsed = ParseConditions(controller, conditions);

            var fromAnyState = string.IsNullOrWhiteSpace(fromState);
            var source = fromAnyState ? default : AnimatorResolve.State(root, fromState, "from_state");

            if (!fromAnyState && source.State == destination.State)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{source.Path}' cannot transition to itself. An Any State transition with " +
                    "'can transition to self' is how a state re-enters itself.");
            }

            AnimatorResolve.RecordUndo(controller, "MCP Add Animator Transition");

            var transition = fromAnyState
                ? root.AddAnyStateTransition(destination.State)
                : source.State.AddTransition(destination.State);

            transition.hasExitTime = hasExitTime;
            transition.exitTime = exitTime;
            transition.duration = duration;
            transition.hasFixedDuration = hasFixedDuration;

            foreach (var condition in parsed)
            {
                transition.AddCondition(condition.Mode, condition.Threshold, condition.Parameter);
            }

            var saved = AnimatorResolve.Save(controller);

            var list = fromAnyState ? root.anyStateTransitions : source.State.transitions;
            var at = Array.IndexOf(list, transition);
            var addressOf = AnimatorResolve.AddressLookup(root);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["layer"] = controller.layers[index].name,
                ["from"] = fromAnyState ? "Any State" : source.Path,
                ["transition"] = AnimatorResolve.Transition(transition, at, addressOf),
                ["savedToDisk"] = saved,
                ["note"] = AnimatorResolve.Text(parsed.Count == 0 && !hasExitTime
                    ? "This transition has neither a condition nor an exit time, so it fires as soon " +
                      "as the source state is entered."
                    : null),
            };
        }

        [McpTool(
            "animator_remove_transition",
            "Remove one transition by its index, as animator_inspect reports it. Indices shift when a " +
            "transition is removed, so read the layer again between removals rather than working down " +
            "a list of indices taken before the first one. Leaving 'from_state' out reaches the " +
            "layer's own Any State transitions; one belonging to a sub-state machine is not " +
            "addressable here." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Remove Animator Transition")]
        public static JObject RemoveTransition(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("layer", "Layer holding the transition, by name or by index.")]
            string layer = null,
            [McpArg("from_state", "Source state, by name or by path. Omit to remove one of the layer's " +
                                  "Any State transitions.")]
            string fromState = null,
            [McpArg("index", "Index of the transition in that source's list, from animator_inspect.")]
            int index = 0)
        {
            var controller = AnimatorResolve.Controller(path, objectPath);
            var layerIndex = AnimatorResolve.LayerIndex(controller, layer);
            var root = RequireStateMachine(controller, layerIndex);

            var fromAnyState = string.IsNullOrWhiteSpace(fromState);
            var source = fromAnyState ? default : AnimatorResolve.State(root, fromState, "from_state");
            var list = fromAnyState ? root.anyStateTransitions : source.State.transitions;

            if (index < 0 || index >= list.Length)
            {
                var where = fromAnyState ? "Any State" : source.Path;

                throw new McpToolException(
                    "not_found",
                    $"'{where}' has {list.Length} transition(s), so index {index} does not name one.");
            }

            var transition = list[index];
            var addressOf = AnimatorResolve.AddressLookup(root);
            var described = AnimatorResolve.Transition(transition, index, addressOf);

            AnimatorResolve.RecordUndo(controller, "MCP Remove Animator Transition");

            if (fromAnyState)
            {
                root.RemoveAnyStateTransition(transition);
            }
            else
            {
                source.State.RemoveTransition(transition);
            }

            var saved = AnimatorResolve.Save(controller);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["layer"] = controller.layers[layerIndex].name,
                ["from"] = fromAnyState ? "Any State" : source.Path,
                ["removed"] = described,
                ["remaining"] = fromAnyState ? root.anyStateTransitions.Length : source.State.transitions.Length,
                ["savedToDisk"] = saved,
            };
        }

        // ── parameters ────────────────────────────────────────────────────────────

        [McpTool(
            "animator_add_parameter",
            "Add a parameter to an Animator Controller. A parameter is what a script or a menu drives " +
            "to make a transition fire; adding one changes nothing on its own until a condition " +
            "references it. A Trigger is a bool that clears itself once a transition consumes it." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Add Animator Parameter")]
        public static JObject AddParameter(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("name", "Parameter name. Names are case sensitive and a condition matches on the " +
                            "exact string.")]
            string name = null,
            [McpArg("type", "'Float', 'Int', 'Bool' or 'Trigger'.")]
            string type = "Float",
            [McpArg("default_value", "Value the Animator starts from: a number for Float and Int, true " +
                                     "or false for Bool and Trigger.")]
            JToken defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpToolException("invalid_params", "'name' is required.");
            }

            var controller = AnimatorResolve.Controller(path, objectPath);

            if (controller.parameters.Any(p => string.Equals(p.name, name, StringComparison.Ordinal)))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{name}' is already a parameter on this controller. Unity would have added a " +
                    "second one under a made-up name rather than refusing.");
            }

            var parameter = new AnimatorControllerParameter
            {
                name = name,
                type = ParseParameterType(type),
            };

            ApplyDefault(parameter, defaultValue);

            AnimatorResolve.RecordUndo(controller, "MCP Add Animator Parameter");

            controller.AddParameter(parameter);

            var saved = AnimatorResolve.Save(controller);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["parameter"] = AnimatorResolve.Parameter(controller.parameters.Last()),
                ["parameterCount"] = controller.parameters.Length,
                ["savedToDisk"] = saved,
            };
        }

        [McpTool(
            "animator_remove_parameter",
            "Remove a parameter. Conditions that referenced it are left behind pointing at a name that " +
            "no longer exists: Unity does not delete them and does not report them, and the transition " +
            "then never fires. Those conditions are listed in the reply, so remove or repoint them " +
            "afterwards. animator_audit lists the parameters nothing references, which are the ones " +
            "safe to remove." + Shared,
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Remove Animator Parameter")]
        public static JObject RemoveParameter(
            [McpArg("path", "Controller asset path.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject that points at the controller.")]
            string objectPath = null,
            [McpArg("name", "Parameter to remove. Case sensitive.")]
            string name = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpToolException("invalid_params", "'name' is required.");
            }

            var controller = AnimatorResolve.Controller(path, objectPath);
            var parameters = controller.parameters;

            var index = Array.FindIndex(parameters, p => string.Equals(p.name, name, StringComparison.Ordinal));

            if (index < 0)
            {
                var names = string.Join(", ", parameters.Take(16).Select(p => p.name));

                throw new McpToolException("not_found", $"No parameter named '{name}'. The controller has: {names}.");
            }

            var referencedBy = ReferencesTo(controller, name);
            var removed = AnimatorResolve.Parameter(parameters[index]);

            AnimatorResolve.RecordUndo(controller, "MCP Remove Animator Parameter");

            controller.RemoveParameter(index);

            var saved = AnimatorResolve.Save(controller);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["removed"] = removed,
                ["parameterCount"] = controller.parameters.Length,
                ["referencedBy"] = referencedBy,
                ["savedToDisk"] = saved,
                ["note"] = AnimatorResolve.Text(referencedBy.Count == 0
                    ? null
                    : $"{referencedBy.Count} condition(s) still name '{name}'. Those transitions can no " +
                      "longer fire."),
            };
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private readonly struct ParsedCondition
        {
            public ParsedCondition(string parameter, AnimatorConditionMode mode, float threshold)
            {
                this.Parameter = parameter;
                this.Mode = mode;
                this.Threshold = threshold;
            }

            public string Parameter { get; }

            public AnimatorConditionMode Mode { get; }

            public float Threshold { get; }
        }

        private static List<ParsedCondition> ParseConditions(AnimatorController controller, JToken conditions)
        {
            var parsed = new List<ParsedCondition>();

            if (conditions == null || conditions.Type == JTokenType.Null)
            {
                return parsed;
            }

            if (!(conditions is JArray array))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'conditions' is an array of objects, e.g. [{\"parameter\":\"Grounded\",\"mode\":\"If\"}].");
            }

            foreach (var entry in array)
            {
                if (!(entry is JObject item))
                {
                    throw new McpToolException("invalid_params", "Each condition is an object with 'parameter' and 'mode'.");
                }

                var name = item["parameter"]?.ToString();

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new McpToolException("invalid_params", "Each condition needs a 'parameter'.");
                }

                var parameter = controller.parameters.FirstOrDefault(p => string.Equals(p.name, name, StringComparison.Ordinal));

                if (parameter == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{name}' is not a parameter on this controller. animator_inspect lists them, and " +
                        "animator_add_parameter adds one. Names are case sensitive.");
                }

                var modeText = item["mode"]?.ToString();

                if (string.IsNullOrWhiteSpace(modeText) || !Enum.TryParse<AnimatorConditionMode>(modeText, true, out var mode))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{modeText}' is not a condition mode. Use If, IfNot, Greater, Less, Equals or NotEqual.");
                }

                RequireModeFits(parameter, mode);

                var threshold = item["threshold"]?.Value<float?>() ?? 0f;

                parsed.Add(new ParsedCondition(name, mode, threshold));
            }

            return parsed;
        }

        /// <summary>
        /// Refuses a mode the parameter's type cannot answer.
        /// </summary>
        /// <remarks>
        /// Unity accepts every combination through the API and simply never fires the transition, so
        /// a Greater on a bool is a silent dead transition rather than an error.
        /// </remarks>
        private static void RequireModeFits(AnimatorControllerParameter parameter, AnimatorConditionMode mode)
        {
            var boolean = mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;

            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    if (!boolean)
                    {
                        throw new McpToolException(
                            "invalid_params",
                            $"'{parameter.name}' is a {parameter.type}, so its condition mode is If or IfNot, not {mode}.");
                    }

                    break;

                case AnimatorControllerParameterType.Float:
                    if (mode != AnimatorConditionMode.Greater && mode != AnimatorConditionMode.Less)
                    {
                        throw new McpToolException(
                            "invalid_params",
                            $"'{parameter.name}' is a Float, so its condition mode is Greater or Less, not {mode}. " +
                            "Floats cannot be compared for equality.");
                    }

                    break;

                default:
                    if (boolean)
                    {
                        throw new McpToolException(
                            "invalid_params",
                            $"'{parameter.name}' is an Int, so its condition mode is Greater, Less, Equals or NotEqual, not {mode}.");
                    }

                    break;
            }
        }

        private static JArray ReferencesTo(AnimatorController controller, string parameter)
        {
            var found = new JArray();
            var layers = controller.layers;

            for (var i = 0; i < layers.Length; i++)
            {
                var root = layers[i].stateMachine;

                if (root == null)
                {
                    continue;
                }

                foreach (var machine in AnimatorResolve.Machines(root))
                {
                    Collect(found, layers[i].name, i, machine.Path.Length == 0 ? "Any State" : machine.Path + "/Any State", machine.Machine.anyStateTransitions.Select(t => t.conditions), parameter);
                    Collect(found, layers[i].name, i, machine.Path.Length == 0 ? "Entry" : machine.Path + "/Entry", machine.Machine.entryTransitions.Select(t => t.conditions), parameter);
                }

                foreach (var entry in AnimatorResolve.States(root))
                {
                    Collect(found, layers[i].name, i, entry.Path, entry.State.transitions.Select(t => t.conditions), parameter);
                }
            }

            return found;
        }

        private static void Collect(
            JArray into,
            string layerName,
            int layerIndex,
            string from,
            IEnumerable<AnimatorCondition[]> conditionLists,
            string parameter)
        {
            var transitionIndex = 0;

            foreach (var conditions in conditionLists)
            {
                for (var i = 0; i < conditions.Length; i++)
                {
                    if (!string.Equals(conditions[i].parameter, parameter, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    into.Add(new JObject
                    {
                        ["layer"] = layerName,
                        ["layerIndex"] = layerIndex,
                        ["from"] = from,
                        ["transition"] = transitionIndex,
                        ["condition"] = i,
                    });
                }

                transitionIndex++;
            }
        }

        private static int CountIncoming(AnimatorStateMachine root, AnimatorState state)
        {
            var count = 0;

            foreach (var machine in AnimatorResolve.Machines(root))
            {
                count += machine.Machine.anyStateTransitions.Count(t => t.destinationState == state);
                count += machine.Machine.entryTransitions.Count(t => t.destinationState == state);
            }

            foreach (var entry in AnimatorResolve.States(root))
            {
                count += entry.State.transitions.Count(t => t.destinationState == state);
            }

            return count;
        }

        private static AnimatorStateMachine RequireStateMachine(AnimatorController controller, int index)
        {
            var machine = controller.layers[index].stateMachine;

            if (machine == null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"Layer {index} has no state machine, so there is nothing to edit in it.");
            }

            return machine;
        }

        private static AnimatorStateMachine RequireMachine(AnimatorStateMachine root, string machine)
        {
            if (string.IsNullOrWhiteSpace(machine))
            {
                return root;
            }

            var text = machine.Trim();

            foreach (var entry in AnimatorResolve.Machines(root))
            {
                if (string.Equals(entry.Path, text, StringComparison.Ordinal))
                {
                    return entry.Machine;
                }
            }

            var listing = string.Join(", ", AnimatorResolve.Machines(root).Skip(1).Select(m => m.Path));

            throw new McpToolException(
                "not_found",
                listing.Length == 0
                    ? $"This layer has no sub-state machines, so '{text}' does not name one."
                    : $"No sub-state machine '{text}' in this layer. Found: {listing}.");
        }

        private static Motion RequireMotion(string motion)
        {
            if (string.IsNullOrWhiteSpace(motion))
            {
                return null;
            }

            var loaded = AssetDatabase.LoadAssetAtPath<Motion>(motion);

            if (loaded != null)
            {
                return loaded;
            }

            var other = AssetDatabase.LoadMainAssetAtPath(motion);

            throw new McpToolException(
                "not_found",
                other == null
                    ? $"No asset at '{motion}'. asset_find with type 'AnimationClip' lists the clips in the project."
                    : $"'{motion}' is a {other.GetType().Name}, not an AnimationClip. A clip inside an FBX " +
                      "is a sub-asset, so name the clip's own path rather than the model's.");
        }

        private static AvatarMask RequireMask(string mask)
        {
            if (string.IsNullOrWhiteSpace(mask))
            {
                return null;
            }

            var loaded = AssetDatabase.LoadAssetAtPath<AvatarMask>(mask);

            if (loaded == null)
            {
                throw new McpToolException("not_found", $"No AvatarMask at '{mask}'.");
            }

            return loaded;
        }

        private static AnimatorLayerBlendingMode ParseBlending(string blending)
        {
            if (string.IsNullOrWhiteSpace(blending) || !Enum.TryParse<AnimatorLayerBlendingMode>(blending, true, out var mode))
            {
                throw new McpToolException("invalid_params", $"'{blending}' is not a blending mode. Use 'Override' or 'Additive'.");
            }

            return mode;
        }

        private static AnimatorControllerParameterType ParseParameterType(string type)
        {
            if (string.IsNullOrWhiteSpace(type) || !Enum.TryParse<AnimatorControllerParameterType>(type, true, out var parsed))
            {
                throw new McpToolException("invalid_params", $"'{type}' is not a parameter type. Use Float, Int, Bool or Trigger.");
            }

            return parsed;
        }

        private static void ApplyDefault(AnimatorControllerParameter parameter, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                return;
            }

            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float:
                    parameter.defaultFloat = value.Value<float>();
                    break;
                case AnimatorControllerParameterType.Int:
                    parameter.defaultInt = value.Value<int>();
                    break;
                default:
                    parameter.defaultBool = value.Type == JTokenType.Boolean
                        ? value.Value<bool>()
                        : value.Value<float>() != 0f;
                    break;
            }
        }

        /// <summary>
        /// A node position from <c>{x, y}</c> or <c>[x, y]</c>, or a stacked default when none is given.
        /// </summary>
        private static Vector3 ParsePosition(JToken position, int ordinal)
        {
            if (position == null || position.Type == JTokenType.Null)
            {
                // Unity's own AddState puts every state at the same point, which leaves a new layer
                // as one node with everything hidden behind it.
                return new Vector3(280f, ordinal * 70f, 0f);
            }

            if (position is JArray array && array.Count >= 2)
            {
                return new Vector3(array[0].Value<float>(), array[1].Value<float>(), 0f);
            }

            if (position is JObject item && item["x"] != null && item["y"] != null)
            {
                return new Vector3(item["x"].Value<float>(), item["y"].Value<float>(), 0f);
            }

            throw new McpToolException("invalid_params", "'position' is {x, y} or [x, y].");
        }
    }
}
