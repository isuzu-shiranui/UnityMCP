using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.Animations;

using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Addressing an Animator Controller, one of its layers and one of its states, and the JSON
    /// projections the animator tools share.
    /// </summary>
    /// <remarks>
    /// <c>AnimatorController.layers</c> and <c>AnimatorStateMachine.states</c> return a fresh array
    /// of fresh structs on every read. Assigning to an element of one changes nothing at all, and
    /// nothing reports an error, so a tool written that way silently does nothing. Every write goes
    /// through <see cref="ReplaceLayer"/> or <see cref="ReplaceState"/>, which put the array back.
    /// <para>
    /// Layer names carry no uniqueness requirement — duplicates are one of the things
    /// <c>animator_audit</c> reports — so a layer is addressed by name or by index, and a name
    /// matching several layers is refused rather than resolved to the first of them. State names
    /// have the same problem within a layer, which is why a state is addressed by a path through
    /// its sub-state machines.
    /// </para>
    /// </remarks>
    internal static class AnimatorResolve
    {
        /// <summary>How many names a "not found" message lists before giving up.</summary>
        private const int MaxSuggestions = 16;

        /// <summary>An AnimatorController found on a GameObject, and the field that pointed at it.</summary>
        internal readonly struct ControllerReference
        {
            public ControllerReference(AnimatorController controller, string component, string property)
            {
                this.Controller = controller;
                this.Component = component;
                this.Property = property;
            }

            public AnimatorController Controller { get; }

            public string Component { get; }

            public string Property { get; }
        }

        /// <summary>A state, where it sits in its layer, and the machine that owns it.</summary>
        internal readonly struct StateEntry
        {
            public StateEntry(AnimatorStateMachine machine, string machinePath, AnimatorState state, string path, Vector3 position, int index)
            {
                this.Machine = machine;
                this.MachinePath = machinePath;
                this.State = state;
                this.Path = path;
                this.Position = position;
                this.Index = index;
            }

            public AnimatorStateMachine Machine { get; }

            /// <summary>Path of the owning state machine within the layer; empty for the layer's root.</summary>
            public string MachinePath { get; }

            public AnimatorState State { get; }

            /// <summary>Address of the state within its layer, e.g. <c>Gestures/Point</c>.</summary>
            public string Path { get; }

            public Vector3 Position { get; }

            /// <summary>Index within <see cref="Machine"/>'s state array, which a write has to assign back through.</summary>
            public int Index { get; }
        }

        /// <summary>A state machine and its address within the layer.</summary>
        internal readonly struct MachineEntry
        {
            public MachineEntry(AnimatorStateMachine machine, AnimatorStateMachine parent, string path)
            {
                this.Machine = machine;
                this.Parent = parent;
                this.Path = path;
            }

            public AnimatorStateMachine Machine { get; }

            /// <summary>Null for the layer's root machine.</summary>
            public AnimatorStateMachine Parent { get; }

            /// <summary>Empty for the layer's root machine.</summary>
            public string Path { get; }
        }

        // ── addressing ────────────────────────────────────────────────────────────

        /// <summary>The controller named by an asset path or reached through a scene object.</summary>
        internal static AnimatorController Controller(string path, string objectPath)
        {
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(objectPath))
            {
                throw new McpToolException(
                    "invalid_params",
                    "Pass 'path' or 'object_path', not both: one names a controller asset, the other a " +
                    "scene object that points at one.");
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                return RequireAsset(path);
            }

            if (string.IsNullOrWhiteSpace(objectPath))
            {
                throw new McpToolException("invalid_params", "Either 'path' or 'object_path' is required.");
            }

            var go = ObjectResolve.Object(objectPath, null, "object_path", null);
            var found = ControllersOn(go);

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

            var listing = string.Join(", ", found.Select(f => $"{f.Component}.{f.Property} -> {AssetDatabase.GetAssetPath(f.Controller)}"));

            throw new McpToolException(
                "invalid_params",
                $"'{ObjectResolve.PathOf(go)}' points at {found.Count} controllers: {listing}. Name one " +
                "with 'path'. animator_inspect with 'object_path' lists them without failing.");
        }

        /// <summary>The controller asset at a path, refusing rather than returning null.</summary>
        internal static AnimatorController RequireAsset(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

            if (controller != null)
            {
                return controller;
            }

            var other = AssetDatabase.LoadMainAssetAtPath(path);

            throw new McpToolException(
                "not_found",
                other == null
                    ? $"No asset at '{path}'. asset_find with type 'AnimatorController' lists the ones in the project."
                    : $"'{path}' is a {other.GetType().Name}, not an AnimatorController.");
        }

        /// <summary>
        /// Every AnimatorController any component on this object points at, in component order.
        /// </summary>
        /// <remarks>
        /// The serialized fields are walked rather than the Animator asked directly, because the
        /// controller a caller wants is often not the Animator's: a component that holds a set of
        /// controllers, one per body layer, keeps them in an array of its own and leaves the
        /// Animator pointing at only one of them. Walking the fields reaches both without this
        /// package knowing anything about that component.
        /// </remarks>
        internal static List<ControllerReference> ControllersOn(GameObject go)
        {
            var found = new List<ControllerReference>();

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    // A component whose script is missing. Constructing a SerializedObject over one
                    // throws rather than returning an empty object.
                    continue;
                }

                using var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();

                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    var controller = Unwrap(property.objectReferenceValue);

                    if (controller == null || found.Any(f => f.Controller == controller))
                    {
                        continue;
                    }

                    found.Add(new ControllerReference(controller, component.GetType().Name, property.propertyPath));
                }
            }

            return found;
        }

        /// <summary>The controller behind an object reference, following an override controller to its base.</summary>
        private static AnimatorController Unwrap(UnityEngine.Object value)
        {
            var runtime = value as RuntimeAnimatorController;

            // An AnimatorOverrideController can sit on top of another override controller, and only
            // the AnimatorController at the bottom has layers and parameters to report.
            for (var guard = 0; guard < 16 && runtime != null; guard++)
            {
                if (runtime is AnimatorController controller)
                {
                    return controller;
                }

                if (!(runtime is AnimatorOverrideController over))
                {
                    return null;
                }

                runtime = over.runtimeAnimatorController;
            }

            return null;
        }

        /// <summary>The index of the layer a caller named, by name or by index.</summary>
        internal static int LayerIndex(AnimatorController controller, string layer, string argumentName = "layer")
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                throw new McpToolException("invalid_params", $"'{argumentName}' is required.");
            }

            var layers = controller.layers;
            var text = layer.Trim();

            // A layer whose name is only digits is unreachable by name here. Naming layers "0", "1"
            // is not a thing anyone does, and the alternative — an index that silently means a name —
            // is the more surprising failure.
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= layers.Length)
                {
                    throw new McpToolException(
                        "not_found",
                        $"Layer index {index} is out of range; the controller has {layers.Length} layer(s), 0 to {layers.Length - 1}.");
                }

                return index;
            }

            var matches = Enumerable.Range(0, layers.Length)
                .Where(i => string.Equals(layers[i].name, text, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 1)
            {
                return matches[0];
            }

            if (matches.Length == 0)
            {
                var names = string.Join(", ", layers.Take(MaxSuggestions).Select((l, i) => $"{i}:{l.name}"));

                throw new McpToolException(
                    "not_found",
                    $"No layer named '{text}'. The controller has: {names}.");
            }

            throw new McpToolException(
                "invalid_params",
                $"'{text}' names {matches.Length} layers (indices {string.Join(", ", matches)}). Give the " +
                "index instead. Duplicate layer names are one of the things animator_audit reports.");
        }

        /// <summary>Every state machine in a layer, the root first, depth first.</summary>
        internal static IEnumerable<MachineEntry> Machines(AnimatorStateMachine root)
        {
            if (root == null)
            {
                yield break;
            }

            yield return new MachineEntry(root, null, string.Empty);

            foreach (var entry in Descend(root, string.Empty))
            {
                yield return entry;
            }
        }

        private static IEnumerable<MachineEntry> Descend(AnimatorStateMachine parent, string prefix)
        {
            var children = parent.stateMachines;
            var names = children.Select(c => c.stateMachine == null ? null : c.stateMachine.name).ToArray();

            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i].stateMachine;

                if (child == null)
                {
                    continue;
                }

                var path = Join(prefix, Segment(names, i));

                yield return new MachineEntry(child, parent, path);

                foreach (var nested in Descend(child, path))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>Every state in a layer, including those inside sub-state machines.</summary>
        internal static IEnumerable<StateEntry> States(AnimatorStateMachine root)
        {
            foreach (var machine in Machines(root))
            {
                var states = machine.Machine.states;
                var names = states.Select(s => s.state == null ? null : s.state.name).ToArray();

                for (var i = 0; i < states.Length; i++)
                {
                    if (states[i].state == null)
                    {
                        continue;
                    }

                    yield return new StateEntry(
                        machine.Machine,
                        machine.Path,
                        states[i].state,
                        Join(machine.Path, Segment(names, i)),
                        states[i].position,
                        i);
                }
            }
        }

        /// <summary>The state a caller named, by full path or by a name unique within the layer.</summary>
        internal static StateEntry State(AnimatorStateMachine root, string state, string argumentName = "state")
        {
            if (string.IsNullOrWhiteSpace(state))
            {
                throw new McpToolException("invalid_params", $"'{argumentName}' is required.");
            }

            var text = state.Trim();
            var all = States(root).ToList();

            var exact = all.Where(s => string.Equals(s.Path, text, StringComparison.Ordinal)).ToList();

            if (exact.Count == 1)
            {
                return exact[0];
            }

            var byName = all.Where(s => string.Equals(s.State.name, text, StringComparison.Ordinal)).ToList();

            if (byName.Count == 1)
            {
                return byName[0];
            }

            if (byName.Count > 1)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{text}' names {byName.Count} states in this layer: {string.Join(", ", byName.Select(s => s.Path))}. " +
                    "Give the full path.");
            }

            var listing = all.Count == 0
                ? "the layer has no states"
                : string.Join(", ", all.Take(MaxSuggestions).Select(s => s.Path));

            throw new McpToolException("not_found", $"No state '{text}' in this layer. Found: {listing}.");
        }

        // ── writing ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes one layer back into the controller. Reading <c>layers</c>, changing the struct and
        /// dropping it is the silent no-op this exists to prevent.
        /// </summary>
        internal static void ReplaceLayer(AnimatorController controller, int index, AnimatorControllerLayer layer)
        {
            var layers = controller.layers;
            layers[index] = layer;
            controller.layers = layers;
        }

        /// <summary>Writes one state's entry back into its machine, for the fields the struct owns.</summary>
        internal static void ReplaceState(AnimatorStateMachine machine, int index, ChildAnimatorState child)
        {
            var states = machine.states;
            states[index] = child;
            machine.states = states;
        }

        /// <summary>Records the controller and everything stored inside it on the undo stack.</summary>
        /// <remarks>
        /// A layer, a state and a transition are all separate objects saved inside the controller's
        /// own asset file, so recording the controller alone leaves their fields outside the undo
        /// step and the change survives the Ctrl+Z that appears to reverse it.
        /// </remarks>
        internal static void RecordUndo(AnimatorController controller, string name)
        {
            var assetPath = AssetDatabase.GetAssetPath(controller);

            var objects = string.IsNullOrEmpty(assetPath)
                ? new UnityEngine.Object[] { controller }
                : AssetDatabase.LoadAllAssetsAtPath(assetPath).Where(o => o != null).ToArray();

            Undo.RegisterCompleteObjectUndo(objects, name);
        }

        /// <summary>
        /// Marks the controller dirty and writes its file, reporting whether it reached disk.
        /// </summary>
        internal static bool Save(AnimatorController controller)
        {
            EditorUtility.SetDirty(controller);

            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller)))
            {
                return false;
            }

            AssetDatabase.SaveAssetIfDirty(controller);

            return true;
        }

        // ── projections ───────────────────────────────────────────────────────────

        /// <summary>A string, or a real JSON null when there is nothing to say.</summary>
        /// <remarks>
        /// Assigning a null <c>string</c> straight into a JObject produces a token typed String
        /// holding null, not a null token, so a caller testing the type sees the wrong answer.
        /// </remarks>
        internal static JToken Text(string value)
        {
            return value == null ? JValue.CreateNull() : (JToken)value;
        }

        /// <summary>A parameter with its type and the default the Animator starts from.</summary>
        internal static JObject Parameter(AnimatorControllerParameter parameter)
        {
            JToken value;

            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float:
                    value = Math.Round(parameter.defaultFloat, 4);
                    break;
                case AnimatorControllerParameterType.Int:
                    value = parameter.defaultInt;
                    break;
                default:
                    // Bool and Trigger both read their default out of defaultBool. A trigger whose
                    // default is true fires once on entry, which is worth being able to see.
                    value = parameter.defaultBool;
                    break;
            }

            return new JObject
            {
                ["name"] = parameter.name,
                ["type"] = parameter.type.ToString(),
                ["default"] = value,
            };
        }

        /// <summary>A layer's own settings, without its states.</summary>
        internal static JObject Layer(AnimatorControllerLayer layer, int index)
        {
            var machine = layer.stateMachine;

            return new JObject
            {
                ["index"] = index,
                ["name"] = layer.name,
                ["weight"] = Math.Round(layer.defaultWeight, 4),
                ["blending"] = layer.blendingMode.ToString(),
                ["mask"] = layer.avatarMask == null ? null : (JToken)layer.avatarMask.name,
                ["ikPass"] = layer.iKPass,
                ["syncedLayerIndex"] = layer.syncedLayerIndex,
                ["stateCount"] = machine == null ? 0 : States(machine).Count(),
                ["subStateMachineCount"] = machine == null ? 0 : Machines(machine).Count() - 1,
                ["defaultState"] = machine == null || machine.defaultState == null
                    ? null
                    : (JToken)machine.defaultState.name,
            };
        }

        /// <summary>What a motion is, in the three fields that answer "is this state animating anything".</summary>
        internal static void DescribeMotion(JObject target, Motion motion)
        {
            target["motion"] = motion == null ? null : (JToken)motion.name;
            target["motionType"] = motion == null ? null : (JToken)(motion is BlendTree ? "BlendTree" : motion.GetType().Name);

            var path = motion == null ? null : AssetDatabase.GetAssetPath(motion);
            target["motionPath"] = string.IsNullOrEmpty(path) ? null : (JToken)path;
        }

        /// <summary>One transition, addressed by its index in the list it came from.</summary>
        internal static JObject Transition(AnimatorStateTransition transition, int index, Func<AnimatorState, string> addressOf)
        {
            var entry = new JObject
            {
                ["index"] = index,
                ["to"] = Destination(transition.isExit, transition.destinationState, transition.destinationStateMachine, addressOf),
                ["conditions"] = Conditions(transition.conditions),
                ["hasExitTime"] = transition.hasExitTime,
                ["duration"] = Math.Round(transition.duration, 4),
                ["hasFixedDuration"] = transition.hasFixedDuration,
            };

            if (transition.hasExitTime)
            {
                entry["exitTime"] = Math.Round(transition.exitTime, 4);
            }

            // Mute and solo change which transitions run and are invisible in a normal reading of
            // the graph, so they are reported only when they are doing something.
            if (transition.mute)
            {
                entry["mute"] = true;
            }

            if (transition.solo)
            {
                entry["solo"] = true;
            }

            return entry;
        }

        /// <summary>One Entry or Exit transition, which has conditions but no timing of its own.</summary>
        internal static JObject Transition(AnimatorTransition transition, int index, Func<AnimatorState, string> addressOf)
        {
            return new JObject
            {
                ["index"] = index,
                ["to"] = Destination(transition.isExit, transition.destinationState, transition.destinationStateMachine, addressOf),
                ["conditions"] = Conditions(transition.conditions),
            };
        }

        internal static JArray Conditions(AnimatorCondition[] conditions)
        {
            var list = new JArray();

            foreach (var condition in conditions)
            {
                var entry = new JObject
                {
                    ["parameter"] = condition.parameter,
                    ["mode"] = condition.mode.ToString(),
                };

                // If and IfNot test a bool or a trigger and carry a threshold of zero that means
                // nothing; printing it invites a caller to think it is part of the test.
                if (condition.mode != AnimatorConditionMode.If && condition.mode != AnimatorConditionMode.IfNot)
                {
                    entry["threshold"] = Math.Round(condition.threshold, 4);
                }

                list.Add(entry);
            }

            return list;
        }

        private static JToken Destination(
            bool isExit,
            AnimatorState state,
            AnimatorStateMachine machine,
            Func<AnimatorState, string> addressOf)
        {
            if (isExit)
            {
                return "Exit";
            }

            if (state != null)
            {
                return addressOf == null ? state.name : addressOf(state);
            }

            if (machine != null)
            {
                return machine.name + " (sub-state machine)";
            }

            return null;
        }

        /// <summary>A lookup from state to its address within the layer, for reporting destinations.</summary>
        internal static Func<AnimatorState, string> AddressLookup(AnimatorStateMachine root)
        {
            var map = new Dictionary<AnimatorState, string>();

            foreach (var entry in States(root))
            {
                map[entry.State] = entry.Path;
            }

            return state => state != null && map.TryGetValue(state, out var path) ? path : state == null ? null : state.name;
        }

        // ── path segments ─────────────────────────────────────────────────────────

        /// <summary>
        /// The segment naming item <paramref name="index"/>, with a <c>[n]</c> suffix only where the
        /// name repeats among its siblings, so the common case stays readable.
        /// </summary>
        private static string Segment(string[] names, int index)
        {
            var name = names[index];
            var seen = 0;
            var duplicates = 0;

            for (var i = 0; i < names.Length; i++)
            {
                if (!string.Equals(names[i], name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (i == index)
                {
                    seen = duplicates;
                }

                duplicates++;
            }

            return duplicates > 1 ? $"{name}[{seen}]" : name;
        }

        private static string Join(string prefix, string segment)
        {
            return string.IsNullOrEmpty(prefix) ? segment : prefix + "/" + segment;
        }
    }
}
