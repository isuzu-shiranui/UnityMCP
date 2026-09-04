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
    /// The problems an Animator Controller can hold that nothing in the Editor points out.
    /// </summary>
    /// <remarks>
    /// Every finding here is something Unity accepts without a warning and the Animator window
    /// draws without complaint. Mixed Write Defaults is the one that costs the most time: a state
    /// that leaves the flag off keeps whatever the previous state wrote, so a layer disagreeing
    /// with itself animates differently depending on the order things were entered, and only in
    /// play mode.
    /// </remarks>
    internal static class AnimatorAuditTools
    {
        [McpTool(
            "animator_audit",
            "Find the problems in an Animator Controller that nothing in the Editor reports. Changes " +
            "nothing: it only reads. Reports parameters no transition, blend tree or behaviour " +
            "references; states with no motion; states unreachable from the layer's default state, " +
            "following transitions, Entry and Any State; layers with no states; duplicate layer " +
            "names; transitions with neither a condition nor an exit time, which fire the instant " +
            "the state is entered; and Write Defaults mixed within one layer, with the majority and " +
            "the states that disagree. Mixed Write Defaults is the usual reason an avatar animates " +
            "differently in play than it looks in the Animator window. Name the asset with 'path' or " +
            "reach it through a scene object with 'object_path'.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Audit(
            [McpArg("path", "Controller asset path, e.g. Assets/Animation/Avatar_FX.controller.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject whose components point at the " +
                                   "controller, instead of naming the asset.")]
            string objectPath = null)
        {
            var controller = AnimatorResolve.Controller(path, objectPath);
            var layers = controller.layers;

            var unusedParameters = UnusedParameters(controller);
            var statesWithoutMotion = new JArray();
            var unreachableStates = new JArray();
            var emptyLayers = new JArray();
            var immediateTransitions = new JArray();
            var mixedWriteDefaults = new JArray();

            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                var machine = layer.stateMachine;

                if (machine == null)
                {
                    emptyLayers.Add(new JObject { ["index"] = i, ["layer"] = layer.name, ["reason"] = "no state machine" });
                    continue;
                }

                var states = AnimatorResolve.States(machine).ToList();

                if (states.Count == 0)
                {
                    emptyLayers.Add(new JObject { ["index"] = i, ["layer"] = layer.name, ["reason"] = "no states" });
                    continue;
                }

                var addressOf = AnimatorResolve.AddressLookup(machine);
                var reachable = Reachable(machine);
                var incoming = IncomingCounts(machine);

                foreach (var entry in states)
                {
                    if (entry.State.motion == null)
                    {
                        statesWithoutMotion.Add(new JObject
                        {
                            ["layer"] = layer.name,
                            ["layerIndex"] = i,
                            ["state"] = entry.Path,
                            // An empty state carrying a behaviour is usually deliberate: the state
                            // exists so the behaviour runs, and reporting it as a fault is noise.
                            ["behaviourCount"] = entry.State.behaviours.Count(b => b != null),
                        });
                    }

                    if (!reachable.Contains(entry.State))
                    {
                        unreachableStates.Add(new JObject
                        {
                            ["layer"] = layer.name,
                            ["layerIndex"] = i,
                            ["state"] = entry.Path,
                            // Zero means nothing points at it at all. A positive count means it sits
                            // in an island of states that only reach each other, which is a different
                            // repair: the way in is missing, not the state.
                            ["incomingTransitions"] = incoming.TryGetValue(entry.State, out var count) ? count : 0,
                        });
                    }

                    CollectImmediate(immediateTransitions, layer.name, i, entry.Path, entry.State.transitions, addressOf);
                }

                foreach (var entry in AnimatorResolve.Machines(machine))
                {
                    CollectImmediate(immediateTransitions, layer.name, i, entry.Path.Length == 0 ? "Any State" : entry.Path + "/Any State", entry.Machine.anyStateTransitions, addressOf);
                }

                var mixed = MixedWriteDefaults(layer.name, i, states);

                if (mixed != null)
                {
                    mixedWriteDefaults.Add(mixed);
                }
            }

            var duplicateLayerNames = new JArray(layers
                .Select((l, i) => new { l.name, index = i })
                .GroupBy(l => l.name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => (object)new JObject
                {
                    ["name"] = g.Key,
                    ["indices"] = new JArray(g.Select(l => (object)l.index).ToArray()),
                })
                .ToArray());

            var findings = new JObject
            {
                ["unusedParameters"] = unusedParameters.Count,
                ["statesWithoutMotion"] = statesWithoutMotion.Count,
                ["unreachableStates"] = unreachableStates.Count,
                ["emptyLayers"] = emptyLayers.Count,
                ["duplicateLayerNames"] = duplicateLayerNames.Count,
                ["immediateTransitions"] = immediateTransitions.Count,
                ["mixedWriteDefaults"] = mixedWriteDefaults.Count,
            };

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(controller),
                ["name"] = controller.name,
                ["parameterCount"] = controller.parameters.Length,
                ["layerCount"] = layers.Length,
                ["findingCount"] = findings.Properties().Sum(p => p.Value.Value<int>()),
                ["findings"] = findings,
                ["unusedParameters"] = unusedParameters,
                ["statesWithoutMotion"] = statesWithoutMotion,
                ["unreachableStates"] = unreachableStates,
                ["emptyLayers"] = emptyLayers,
                ["duplicateLayerNames"] = duplicateLayerNames,
                ["immediateTransitions"] = immediateTransitions,
                ["mixedWriteDefaults"] = mixedWriteDefaults,
                ["note"] = "A parameter is counted as referenced when its name appears in any string " +
                           "field of a StateMachineBehaviour, so a parameter sharing a name with some " +
                           "other string on a behaviour is not reported. A name a behaviour builds at " +
                           "run time cannot be seen at all.",
            };
        }

        private static void CollectImmediate(
            JArray into,
            string layerName,
            int layerIndex,
            string from,
            AnimatorStateTransition[] transitions,
            Func<AnimatorState, string> addressOf)
        {
            for (var i = 0; i < transitions.Length; i++)
            {
                var transition = transitions[i];

                if (transition.conditions.Length > 0 || transition.hasExitTime)
                {
                    continue;
                }

                into.Add(new JObject
                {
                    ["layer"] = layerName,
                    ["layerIndex"] = layerIndex,
                    ["from"] = from,
                    ["index"] = i,
                    ["to"] = AnimatorResolve.Transition(transition, i, addressOf)["to"],
                });
            }
        }

        private static JObject MixedWriteDefaults(string layerName, int layerIndex, List<AnimatorResolve.StateEntry> states)
        {
            var on = states.Where(s => s.State.writeDefaultValues).ToList();
            var off = states.Where(s => !s.State.writeDefaultValues).ToList();

            if (on.Count == 0 || off.Count == 0)
            {
                return null;
            }

            bool? majority = on.Count == off.Count ? (bool?)null : on.Count > off.Count;

            var disagreeing = states
                .Where(s => majority == null || s.State.writeDefaultValues != majority.Value)
                .Select(s => (object)new JObject
                {
                    ["state"] = s.Path,
                    ["writeDefaults"] = s.State.writeDefaultValues,
                })
                .ToArray();

            return new JObject
            {
                ["layer"] = layerName,
                ["layerIndex"] = layerIndex,
                ["writeDefaultsOn"] = on.Count,
                ["writeDefaultsOff"] = off.Count,
                // Null where the counts are equal, in which case every state is listed below with
                // its own flag rather than one half being named the odd one out arbitrarily.
                ["majority"] = majority.HasValue ? (JToken)majority.Value : JValue.CreateNull(),
                ["disagreeing"] = new JArray(disagreeing),
            };
        }

        /// <summary>How many transitions in the layer point at each state.</summary>
        private static Dictionary<AnimatorState, int> IncomingCounts(AnimatorStateMachine root)
        {
            var counts = new Dictionary<AnimatorState, int>();

            void Count(AnimatorState state)
            {
                if (state == null)
                {
                    return;
                }

                counts.TryGetValue(state, out var current);
                counts[state] = current + 1;
            }

            foreach (var entry in AnimatorResolve.Machines(root))
            {
                foreach (var transition in entry.Machine.anyStateTransitions)
                {
                    Count(transition.destinationState);
                }

                foreach (var transition in entry.Machine.entryTransitions)
                {
                    Count(transition.destinationState);
                }

                foreach (var child in entry.Machine.states)
                {
                    if (child.state == null)
                    {
                        continue;
                    }

                    foreach (var transition in child.state.transitions)
                    {
                        Count(transition.destinationState);
                    }
                }
            }

            return counts;
        }

        // ── reachability ──────────────────────────────────────────────────────────

        /// <summary>
        /// The states a layer can actually enter, from its default state and everything Entry and
        /// Any State reach.
        /// </summary>
        /// <remarks>
        /// A sub-state machine's own Any State transitions are followed only once something reaches
        /// that machine, which is what Unity does: they fire while the machine is inside it, not
        /// from the layer at large. The parent's transitions for a machine are followed too, because
        /// a transition marked Exit leaves through them.
        /// </remarks>
        private static HashSet<AnimatorState> Reachable(AnimatorStateMachine root)
        {
            var states = new HashSet<AnimatorState>();
            var machines = new HashSet<AnimatorStateMachine>();
            var stateQueue = new Queue<AnimatorState>();
            var machineQueue = new Queue<AnimatorStateMachine>();

            var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();

            foreach (var entry in AnimatorResolve.Machines(root))
            {
                parents[entry.Machine] = entry.Parent;
            }

            void SeedState(AnimatorState state)
            {
                if (state != null && states.Add(state))
                {
                    stateQueue.Enqueue(state);
                }
            }

            void SeedMachine(AnimatorStateMachine machine)
            {
                if (machine != null && machines.Add(machine))
                {
                    machineQueue.Enqueue(machine);
                }
            }

            SeedMachine(root);

            while (machineQueue.Count > 0 || stateQueue.Count > 0)
            {
                while (machineQueue.Count > 0)
                {
                    var machine = machineQueue.Dequeue();

                    SeedState(machine.defaultState);

                    foreach (var transition in machine.entryTransitions)
                    {
                        SeedState(transition.destinationState);
                        SeedMachine(transition.destinationStateMachine);
                    }

                    foreach (var transition in machine.anyStateTransitions)
                    {
                        SeedState(transition.destinationState);
                        SeedMachine(transition.destinationStateMachine);
                    }

                    if (parents.TryGetValue(machine, out var parent) && parent != null)
                    {
                        foreach (var transition in parent.GetStateMachineTransitions(machine))
                        {
                            SeedState(transition.destinationState);
                            SeedMachine(transition.destinationStateMachine);
                        }
                    }
                }

                while (stateQueue.Count > 0)
                {
                    foreach (var transition in stateQueue.Dequeue().transitions)
                    {
                        SeedState(transition.destinationState);
                        SeedMachine(transition.destinationStateMachine);
                    }
                }
            }

            return states;
        }

        // ── parameter references ──────────────────────────────────────────────────

        private static JArray UnusedParameters(AnimatorController controller)
        {
            var used = ReferencedParameters(controller);

            return new JArray(controller.parameters
                .Where(p => !used.Contains(p.name))
                .Select(p => (object)AnimatorResolve.Parameter(p))
                .ToArray());
        }

        private static HashSet<string> ReferencedParameters(AnimatorController controller)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null)
                {
                    continue;
                }

                foreach (var entry in AnimatorResolve.Machines(layer.stateMachine))
                {
                    var machine = entry.Machine;

                    foreach (var transition in machine.anyStateTransitions)
                    {
                        AddConditions(used, transition.conditions);
                    }

                    foreach (var transition in machine.entryTransitions)
                    {
                        AddConditions(used, transition.conditions);
                    }

                    foreach (var child in machine.stateMachines)
                    {
                        if (child.stateMachine == null)
                        {
                            continue;
                        }

                        foreach (var transition in machine.GetStateMachineTransitions(child.stateMachine))
                        {
                            AddConditions(used, transition.conditions);
                        }
                    }

                    AddBehaviours(used, machine.behaviours);

                    foreach (var child in machine.states)
                    {
                        var state = child.state;

                        if (state == null)
                        {
                            continue;
                        }

                        foreach (var transition in state.transitions)
                        {
                            AddConditions(used, transition.conditions);
                        }

                        AddIf(used, state.speedParameterActive, state.speedParameter);
                        AddIf(used, state.mirrorParameterActive, state.mirrorParameter);
                        AddIf(used, state.cycleOffsetParameterActive, state.cycleOffsetParameter);
                        AddIf(used, state.timeParameterActive, state.timeParameter);

                        AddBehaviours(used, state.behaviours);
                        AddMotion(used, state.motion, 0);
                    }
                }
            }

            return used;
        }

        private static void AddConditions(HashSet<string> used, AnimatorCondition[] conditions)
        {
            foreach (var condition in conditions)
            {
                AddIf(used, true, condition.parameter);
            }
        }

        private static void AddIf(HashSet<string> used, bool active, string name)
        {
            if (active && !string.IsNullOrEmpty(name))
            {
                used.Add(name);
            }
        }

        /// <summary>
        /// The parameters a blend tree drives, by blend type: a 1D tree ignores the Y parameter it
        /// still stores, and a direct tree ignores both in favour of one parameter per child.
        /// </summary>
        private static void AddMotion(HashSet<string> used, Motion motion, int depth)
        {
            if (depth > 12 || !(motion is BlendTree tree))
            {
                return;
            }

            switch (tree.blendType)
            {
                case BlendTreeType.Direct:
                    break;
                case BlendTreeType.Simple1D:
                    AddIf(used, true, tree.blendParameter);
                    break;
                default:
                    AddIf(used, true, tree.blendParameter);
                    AddIf(used, true, tree.blendParameterY);
                    break;
            }

            foreach (var child in tree.children)
            {
                if (tree.blendType == BlendTreeType.Direct)
                {
                    AddIf(used, true, child.directBlendParameter);
                }

                AddMotion(used, child.motion, depth + 1);
            }
        }

        /// <summary>
        /// Every string a behaviour holds, taken as a possible parameter name.
        /// </summary>
        /// <remarks>
        /// A StateMachineBehaviour is an arbitrary user script, so there is no field this package can
        /// know to read. Treating every string as a candidate errs towards calling a parameter used,
        /// which is the safe direction: telling someone to delete a parameter a behaviour drives
        /// breaks their controller, while leaving one unreported costs nothing.
        /// </remarks>
        private static void AddBehaviours(HashSet<string> used, StateMachineBehaviour[] behaviours)
        {
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null)
                {
                    continue;
                }

                using var serialized = new SerializedObject(behaviour);
                var property = serialized.GetIterator();

                while (property.Next(true))
                {
                    if (property.propertyType == SerializedPropertyType.String)
                    {
                        AddIf(used, true, property.stringValue);
                    }
                }
            }
        }
    }
}
