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

        // ── creating the asset ────────────────────────────────────────────────────

        [McpTool(
            "animator_create",
            "Create an Animator Controller asset, and optionally hang it on a GameObject's " +
            "Animator. Eleven animator_* tools edit a controller and none of them could make " +
            "one, so an empty project could not reach any of them without execute_code. The new " +
            "controller has one layer named Base Layer and no states; animator_add_state and " +
            "animator_add_parameter fill it in.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create Animator Controller",
            Group = "authoring")]
        public static JObject AnimatorCreate(
            [McpArg("path", "Where to write the .controller, e.g. Assets/Art/Hero.controller. " +
                            "Missing folders under Assets/ are created, and '.controller' is " +
                            "added when it is left off.", Required = true)]
            string path = null,
            [McpArg("object_path", "Scene path of a GameObject to hang it on. An Animator is " +
                                   "added when it has none. Omit to only create the asset.")]
            string objectPath = null,
            [McpArg("overwrite", "Replace an existing controller at this path rather than refusing.")]
            bool overwrite = false)
        {
            var target = AssetPath(path, ".controller");

            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(target);
            AssetTools.RefuseIncompatibleAsset(target, existing);

            if (existing != null && !overwrite)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{target}' already exists. Pass overwrite to replace it, or edit the one "
                    + "that is there with animator_add_state and animator_add_layer.");
            }

            // Resolved before the old controller is destroyed. Looking the object up afterwards
            // meant a path that does not resolve took the controller with it: the call failed and
            // the asset it was replacing was already gone.
            var host = string.IsNullOrWhiteSpace(objectPath)
                ? null
                : ObjectResolve.Object(objectPath, null, "object_path", null);

            EnsureFolder(target);

            AnimatorController controller;

            if (existing != null)
            {
                // Emptied in place rather than deleted and remade. A new file carries a new GUID,
                // and every Animator, prefab and override controller pointing at this one is left
                // holding a missing reference. RemoveLayer destroys the layer's state machine, so
                // nothing is orphaned inside the file either.
                //
                // Recorded through AnimatorResolve, which takes the controller and everything
                // stored inside its file: the states, transitions and behaviours the layers take
                // with them are separate objects there, and the controller alone leaves them off
                // the undo stack.
                AnimatorResolve.RecordUndo(existing, "MCP Create Animator Controller");

                for (var i = existing.layers.Length - 1; i >= 0; i--)
                {
                    existing.RemoveLayer(i);
                }

                for (var i = existing.parameters.Length - 1; i >= 0; i--)
                {
                    existing.RemoveParameter(i);
                }

                existing.AddLayer("Base Layer");

                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssetIfDirty(existing);

                controller = existing;
            }
            else
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(target);
            }

            var created = new JObject
            {
                ["path"] = target,
                ["layer"] = controller.layers[0].name,
                // False when the asset was already there: its GUID and every reference to it
                // survive, and a caller keying off created cannot tell the two apart otherwise.
                ["created"] = existing == null,
            };

            if (existing != null)
            {
                created["replaced"] = true;
            }

            if (host != null)
            {
                var animator = host.GetComponent<Animator>();

                if (animator == null)
                {
                    animator = Undo.AddComponent<Animator>(host);
                }
                else
                {
                    Undo.RecordObject(animator, "MCP Create Animator Controller");
                }

                animator.runtimeAnimatorController = controller;
                created["attachedTo"] = ObjectResolve.PathOf(host);
            }

            return created;
        }

        [McpTool(
            "animation_clip_create",
            "Create an empty AnimationClip asset. A controller's states need a motion, and " +
            "nothing else here makes one. The clip holds no curves, and a clip's length comes " +
            "from its curves, so there is no length to set until something animates it: " +
            "animation_clip_write_curves is what puts a motion in it. Looping is a setting rather " +
            "than a curve, so it is here.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create Animation Clip",
            Group = "authoring")]
        public static JObject AnimationClipCreate(
            [McpArg("path", "Where to write the .anim, e.g. Assets/Art/Idle.anim. Missing folders " +
                            "under Assets/ are created, and '.anim' is added when it is left off.",
                    Required = true)]
            string path = null,
            [McpArg("loop", "Whether the clip loops.")]
            bool loop = false,
            [McpArg("overwrite", "Replace an existing clip at this path rather than refusing.")]
            bool overwrite = false)
        {
            var target = AssetPath(path, ".anim");

            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(target);
            AssetTools.RefuseIncompatibleAsset(target, existing);

            if (existing != null && !overwrite)
            {
                throw new McpToolException(
                    "invalid_params", $"'{target}' already exists. Pass overwrite to replace it.");
            }

            EnsureFolder(target);

            var clip = new AnimationClip { name = System.IO.Path.GetFileNameWithoutExtension(target) };

            // loopTime is not on AnimationClip; it lives in the settings the importer writes.
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            if (existing != null)
            {
                AssetTools.ReplaceAssetContents(existing, clip, target);

                return new JObject
                {
                    ["path"] = target,
                    ["loop"] = loop,
                    ["created"] = false,
                    ["replaced"] = true,
                };
            }

            AssetDatabase.CreateAsset(clip, target);
            AssetDatabase.SaveAssetIfDirty(clip);

            return new JObject
            {
                ["path"] = target,
                ["loop"] = loop,
                ["created"] = true,
            };
        }

        [McpTool(
            "animation_clip_write_curves",
            "Write float curves into an AnimationClip. animation_clip_create makes the asset and " +
            "leaves it empty, so a state with a motion in it meant execute_code and " +
            "AnimationUtility. Each curve names what it drives: a child path from the animated " +
            "root, a component type, and a serialized property. Several curves go in one call " +
            "because a motion is never one curve — a bob, a sway and a nod are three. A clip's " +
            "length comes from its keys, so the reply says what it became.",
            Idempotency = McpIdempotency.Unsafe,
            Group = "authoring",
            Examples = new[]
            {
                @"{""path"":""Assets/Art/Walk.anim"",""curves"":[{""target"":""Hips"",""type"":""Transform""," +
                @"""property"":""m_LocalPosition.y"",""keys"":[{""time"":0,""value"":1},{""time"":0.4,""value"":1.08}," +
                @"{""time"":0.8,""value"":1}]}]}",
            })]
        public static JObject AnimationClipWriteCurves(
            [McpArg("path", "The .anim to write into, e.g. Assets/Art/Walk.anim.", Required = true)]
            string path = null,
            [McpArg("curves", "The curves, as an array. Each takes 'target' (the child path from " +
                              "the animated root, empty for the root itself), 'type' (component " +
                              "type name, e.g. Transform), 'property' (serialized property name, " +
                              "e.g. m_LocalPosition.y) and 'keys' (an array of {time, value}). " +
                              "Tangents are smoothed the way the Animation window's Auto does. " +
                              "Nothing is written unless every curve resolves.", Required = true)]
            JObject[] curves = null,
            [McpArg("frame_rate", "Clip frame rate.")]
            float frameRate = 60f,
            [McpArg("replace", "Clear the clip's existing curves first. Without it a curve " +
                               "replaces whatever shares its binding and the rest are left alone.")]
            bool replace = false)
        {
            var target = AssetPath(path, ".anim");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(target);

            if (clip == null)
            {
                throw new McpToolException(
                    "not_found", $"No AnimationClip at '{target}'. animation_clip_create makes one.");
            }

            if (curves == null || curves.Length == 0)
            {
                throw new McpToolException("invalid_params", "'curves' needs at least one curve.");
            }

            // Everything is resolved before anything is written, so a type name that turns out to
            // be wrong halfway through does not leave a clip holding half a motion.
            var resolved = new List<(EditorCurveBinding Binding, AnimationCurve Curve)>();

            foreach (var spec in curves)
            {
                var property = spec?["property"]?.ToString();

                if (string.IsNullOrWhiteSpace(property))
                {
                    throw new McpToolException("invalid_params", "A curve needs a 'property'.");
                }

                var type = GameObjectTools.FindComponentType(spec["type"]?.ToString());
                var binding = EditorCurveBinding.FloatCurve(spec["target"]?.ToString() ?? "", type, property);

                resolved.Add((binding, CurveFrom(spec["keys"] as JArray, property)));
            }

            Undo.RegisterCompleteObjectUndo(clip, "MCP Write Curves");

            if (replace)
            {
                clip.ClearCurves();
            }

            clip.frameRate = frameRate;

            foreach (var (binding, curve) in resolved)
            {
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);

            return new JObject
            {
                ["path"] = target,
                ["curves"] = AnimationUtility.GetCurveBindings(clip).Length,
                ["written"] = resolved.Count,
                ["length"] = clip.length,
                ["frameRate"] = clip.frameRate,
            };
        }

        /// <summary>One curve's keys, smoothed the way the Animation window smooths its own.</summary>
        /// <remarks>
        /// Keyframes left with their default tangents hold their value and then jump, which is not
        /// what a caller describing a bob with three keys is asking for.
        /// </remarks>
        private static AnimationCurve CurveFrom(JArray keys, string property)
        {
            if (keys == null || keys.Count == 0)
            {
                throw new McpToolException("invalid_params", $"The curve for '{property}' has no keys.");
            }

            var frames = new Keyframe[keys.Count];

            for (var i = 0; i < keys.Count; i++)
            {
                if (keys[i] is not JObject key || key["time"] == null || key["value"] == null)
                {
                    throw new McpToolException(
                        "invalid_params", $"A key of '{property}' is not a {{time, value}} pair.");
                }

                frames[i] = new Keyframe(key["time"].Value<float>(), key["value"].Value<float>());
            }

            var curve = new AnimationCurve(frames);

            for (var i = 0; i < curve.length; i++)
            {
                curve.SmoothTangents(i, 0f);
            }

            return curve;
        }

        /// <summary>An asset path under Assets/, with the extension the AssetDatabase needs.</summary>
        private static string AssetPath(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var target = path.Replace('\\', '/');

            if (!target.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                target += extension;
            }

            if (!target.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{target}' is outside Assets/. The asset has to live in the project.");
            }

            return target;
        }

        /// <summary>Creates the folders a path needs, the way the Project window would.</summary>
        private static void EnsureFolder(string assetPath)
        {
            var parts = assetPath.Split('/');
            var built = parts[0];

            for (var i = 1; i < parts.Length - 1; i++)
            {
                var next = built + "/" + parts[i];

                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(built, parts[i]);
                }

                built = next;
            }
        }

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
                            "animator_audit reports it when they do.", Required = true)]
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
            [McpArg("layer", "Layer to remove, by name or by index.", Required = true)]
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
            [McpArg("layer", "Layer to add to, by name or by index.", Required = true)]
            string layer = null,
            [McpArg("name", "Name for the new state.", Required = true)]
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
            [McpArg("layer", "Layer holding the state, by name or by index.", Required = true)]
            string layer = null,
            [McpArg("state", "State to remove, by name, or by path when it sits in a sub-state " +
                             "machine, e.g. 'Gestures/Point'.", Required = true)]
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
            [McpArg("layer", "Layer holding the state, by name or by index.", Required = true)]
            string layer = null,
            [McpArg("state", "State to change, by name or by path within the layer.", Required = true)]
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
            [McpArg("layer", "Layer holding both states, by name or by index.", Required = true)]
            string layer = null,
            [McpArg("from_state", "Source state, by name or by path. Omit to make it an Any State " +
                                  "transition, which can fire from anywhere in the layer.")]
            string fromState = null,
            [McpArg("to_state", "Destination state, by name or by path.", Required = true)]
            string toState = null,
            [McpArg("conditions", "Array of {parameter, mode, threshold}. Modes: If and IfNot for a " +
                                  "bool or trigger, Greater and Less for a float or int, Equals and " +
                                  "NotEqual for an int. The threshold is ignored by If and IfNot.")]
            JObject[] conditions = null,
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
            [McpArg("layer", "Layer holding the transition, by name or by index.", Required = true)]
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
            "animator_set_parameter",
            "Drive a parameter on a running Animator, the way a script would. Play Mode only: " +
            "this is the value the Animator holds right now, not the controller's default, and " +
            "Unity discards it when Play Mode ends. Setting a parameter is how a transition is " +
            "made to fire on purpose, which is what checking that a gimmick works comes down to. " +
            "Use animator_add_parameter to add one to the asset, and animator_inspect to read " +
            "what the running Animator currently holds.",
            Idempotency = McpIdempotency.Unsafe,
            Group = "authoring")]
        public static JObject AnimatorSetParameter(
            [McpArg("object_path", "Scene path of the GameObject carrying the Animator.",
                    Required = true)]
            string objectPath = null,
            [McpArg("name", "Parameter name, as animator_inspect reports it. Names are case " +
                            "sensitive.", Required = true)]
            string name = null,
            [McpArg("value", "The value to hold: a number for Float and Int, true or false for " +
                             "Bool. A Trigger is set by true and cleared by false.")]
            JToken value = null)
        {
            if (!EditorApplication.isPlaying)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Nothing is running, so there is no Animator to drive. A parameter's starting "
                    + "value belongs to the controller: set it with animator_add_parameter, or "
                    + "enter Play Mode with play_mode_play first.");
            }

            var go = ObjectResolve.Object(objectPath, null, "object_path", null);
            var animator = go.GetComponent<Animator>();

            if (animator == null)
            {
                throw new McpToolException("not_found", $"'{objectPath}' has no Animator.");
            }

            var parameter = animator.parameters.FirstOrDefault(
                p => string.Equals(p.name, name, StringComparison.Ordinal));

            if (parameter == null)
            {
                var present = string.Join(", ", animator.parameters.Select(p => p.name));

                throw new McpToolException(
                    "not_found",
                    $"This Animator has no parameter named '{name}'. It has: "
                    + (present.Length > 0 ? present : "none")
                    + ". animator_add_parameter adds one to the controller.");
            }

            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(name, value != null && value.Value<bool>());
                    break;

                case AnimatorControllerParameterType.Trigger:
                    // Both directions: a trigger left set fires again on the next transition, and
                    // resetting it is the only way to take that back.
                    if (value != null && !value.Value<bool>())
                    {
                        animator.ResetTrigger(name);
                    }
                    else
                    {
                        animator.SetTrigger(name);
                    }

                    break;

                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(name, value == null ? 0 : value.Value<int>());
                    break;

                default:
                    animator.SetFloat(name, value == null ? 0f : value.Value<float>());
                    break;
            }

            return new JObject
            {
                ["object"] = ObjectResolve.PathOf(go),
                ["parameter"] = name,
                ["type"] = parameter.type.ToString(),
                ["value"] = Current(animator, parameter),
                ["written"] = true,

                // The same thing inspect_write says, for the same reason.
                ["playModeWarning"] = "This is the running Animator's value. Unity discards it "
                                      + "when Play Mode ends; the controller's default is unchanged.",
            };
        }

        /// <summary>What the running Animator holds for this parameter now.</summary>
        private static JToken Current(Animator animator, AnimatorControllerParameter parameter)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return animator.GetBool(parameter.name);

                case AnimatorControllerParameterType.Int:
                    return animator.GetInteger(parameter.name);

                default:
                    return animator.GetFloat(parameter.name);
            }
        }

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
                            "exact string.", Required = true)]
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
            [McpArg("name", "Parameter to remove. Case sensitive.", Required = true)]
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

        private static List<ParsedCondition> ParseConditions(AnimatorController controller, JObject[] conditions)
        {
            var parsed = new List<ParsedCondition>();

            if (conditions == null)
            {
                return parsed;
            }

            foreach (var item in conditions)
            {
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
