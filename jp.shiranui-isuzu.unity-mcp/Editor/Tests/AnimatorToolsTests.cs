using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;
using UnityEditor.Animations;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Reading, auditing and editing an Animator Controller asset.
    /// </summary>
    /// <remarks>
    /// The fixture is one controller holding every fault animator_audit looks for, so a single
    /// build covers the findings and the edits both. It is written to a folder under Assets and
    /// deleted afterwards, because an AnimatorController that is not an asset cannot hold the
    /// sub-assets — states, state machines, blend trees — that every one of these tools addresses.
    /// </remarks>
    [TestFixture]
    internal sealed class AnimatorToolsTests
    {
        private const string Folder = "Assets/_McpAnimatorTests";
        private const string ControllerPath = Folder + "/Fixture.controller";
        private const string ClipPath = Folder + "/Fixture.anim";

        private AnimatorController controller;
        private AnimationClip clip;
        private AnimatorTestBehaviour behaviour;

        private AnimatorState idle;
        private AnimatorState instant;
        private AnimatorState viaAny;
        private AnimatorState orphan;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "_McpAnimatorTests");
            }

            AssetDatabase.DeleteAsset(ControllerPath);
            AssetDatabase.DeleteAsset(ClipPath);

            this.clip = new AnimationClip { name = "Fixture" };
            AssetDatabase.CreateAsset(this.clip, ClipPath);

            this.controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            this.controller.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
            this.controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            this.controller.AddParameter("Count", AnimatorControllerParameterType.Int);
            this.controller.AddParameter("BehaviourDriven", AnimatorControllerParameterType.Bool);
            this.controller.AddParameter("Unused", AnimatorControllerParameterType.Float);

            var root = this.controller.layers[0].stateMachine;

            this.idle = root.AddState("Idle", new Vector3(0f, 0f, 0f));
            this.instant = root.AddState("Instant", new Vector3(200f, 0f, 0f));
            this.viaAny = root.AddState("ViaAny", new Vector3(400f, 0f, 0f));
            this.orphan = root.AddState("Orphan", new Vector3(600f, 0f, 0f));

            root.defaultState = this.idle;

            // Idle has no motion at all, which is one of the findings.
            this.instant.motion = this.clip;
            this.orphan.motion = this.clip;

            var tree = new BlendTree
            {
                name = "Blend",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "Count",
                hideFlags = HideFlags.HideInHierarchy,
            };

            AssetDatabase.AddObjectToAsset(tree, this.controller);
            this.viaAny.motion = tree;

            this.instant.speedParameterActive = true;
            this.instant.speedParameter = "Speed";

            // Built and attached by hand rather than through AddStateMachineBehaviour, which looks
            // the type up by MonoScript and finds nothing for a class living in a test assembly:
            // it logs "Can't find monoscript" and returns null.
            this.behaviour = ScriptableObject.CreateInstance<AnimatorTestBehaviour>();
            this.behaviour.name = nameof(AnimatorTestBehaviour);
            this.behaviour.hideFlags = HideFlags.HideInHierarchy;
            this.behaviour.parameterName = "BehaviourDriven";

            AssetDatabase.AddObjectToAsset(this.behaviour, this.controller);
            this.idle.behaviours = new StateMachineBehaviour[] { this.behaviour };

            // Idle -> Instant with neither a condition nor an exit time: it fires the instant the
            // layer starts, which is the "immediate transition" finding.
            var immediate = this.idle.AddTransition(this.instant);
            immediate.hasExitTime = false;

            // ViaAny is reachable only through Any State. An audit that walked state transitions
            // alone would call it orphaned.
            var anyState = root.AddAnyStateTransition(this.viaAny);
            anyState.AddCondition(AnimatorConditionMode.If, 0f, "Toggle");

            // Orphan has nothing pointing at it at all.
            this.orphan.writeDefaultValues = false;

            this.controller.AddLayer("Duplicate");
            this.controller.AddLayer("Duplicate");
            this.controller.AddLayer("Empty");

            // AddLayer(string) makes the name unique, and duplicate layer names are one of the
            // things being tested, so they are written back over the names it chose.
            Rename(1, "Duplicate");
            Rename(2, "Duplicate");

            this.controller.layers[1].stateMachine.AddState("A").motion = this.clip;
            this.controller.layers[2].stateMachine.AddState("B").motion = this.clip;

            EditorUtility.SetDirty(this.controller);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            this.controller = null;
            this.clip = null;
            this.behaviour = null;

            AssetDatabase.DeleteAsset(ControllerPath);
            AssetDatabase.DeleteAsset(ClipPath);
            AssetDatabase.DeleteAsset(Folder);
        }

        private void Rename(int index, string name)
        {
            var layers = this.controller.layers;
            layers[index].name = name;
            this.controller.layers = layers;
        }

        private JObject Audit() => AnimatorAuditTools.Audit(ControllerPath);

        private static JArray Array(JObject result, string key) => (JArray)result[key];

        private AnimatorStateMachine Root => this.controller.layers[0].stateMachine;

        // ── the array-copy trap ───────────────────────────────────────────────────

        [Test]
        public void TheLayerAndStateArraysAreCopiesSoWritingThroughThemDoesNothing()
        {
            var layerName = this.controller.layers[0].name;
            this.controller.layers[0].name = "MutatedInPlace";

            Assert.That(this.controller.layers[0].name, Is.EqualTo(layerName),
                "layers stopped returning a copy; the write-back in AnimatorResolve is now redundant, not wrong");

            var position = this.Root.states[0].position;
            this.Root.states[0].position = new Vector3(999f, 999f, 0f);

            Assert.That(this.Root.states[0].position, Is.EqualTo(position),
                "states stopped returning a copy");
        }

        [Test]
        public void SettingAStatePositionSurvivesBeingReadBack()
        {
            AnimatorEditTools.SetState(
                ControllerPath,
                layer: "0",
                state: "Idle",
                position: new JObject { ["x"] = 123f, ["y"] = 456f });

            var written = this.Root.states.Single(s => s.state == this.idle).position;

            // Position lives on the ChildAnimatorState struct inside the copied array. A tool that
            // set it without assigning the array back would leave this at its original value and
            // report success.
            Assert.That(written.x, Is.EqualTo(123f));
            Assert.That(written.y, Is.EqualTo(456f));
        }

        [Test]
        public void AddingALayerGrowsTheControllersOwnArray()
        {
            var before = this.controller.layers.Length;

            AnimatorEditTools.AddLayer(ControllerPath, name: "Added", weight: 1f);

            Assert.That(this.controller.layers.Length, Is.EqualTo(before + 1),
                "the layer was inserted into a copy of the array and dropped");
            Assert.That(this.controller.layers.Last().name, Is.EqualTo("Added"));
            Assert.That(this.controller.layers.Last().defaultWeight, Is.EqualTo(1f));
            Assert.That(this.controller.layers.Last().stateMachine, Is.Not.Null);
        }

        // ── animator_inspect ──────────────────────────────────────────────────────

        [Test]
        public void InspectWithoutALayerReportsCountsAndNoStates()
        {
            var result = AnimatorInspectTools.Inspect(ControllerPath);

            Assert.That(result["parameterCount"].Value<int>(), Is.EqualTo(5));
            Assert.That(result["layerCount"].Value<int>(), Is.EqualTo(4));
            Assert.That(result["states"], Is.Null, "a twenty-layer controller must not dump every state");

            var parameters = Array(result, "parameters");
            Assert.That(parameters.Select(p => p["name"].Value<string>()),
                Is.EquivalentTo(new[] { "Toggle", "Speed", "Count", "BehaviourDriven", "Unused" }));

            var layers = Array(result, "layers");
            Assert.That(layers[0]["stateCount"].Value<int>(), Is.EqualTo(4));
            Assert.That(layers[0]["defaultState"].Value<string>(), Is.EqualTo("Idle"));
            Assert.That(layers[3]["stateCount"].Value<int>(), Is.EqualTo(0));
        }

        [Test]
        public void InspectNarrowedToALayerReportsStatesMotionsAndTransitions()
        {
            var result = AnimatorInspectTools.Inspect(ControllerPath, layer: "0");
            var states = Array(result, "states");

            Assert.That(result["stateCount"].Value<int>(), Is.EqualTo(4));

            var idleEntry = states.Single(s => s["path"].Value<string>() == "Idle");
            Assert.That(idleEntry["motion"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(idleEntry["isDefault"].Value<bool>(), Is.True);
            Assert.That(idleEntry["writeDefaults"].Value<bool>(), Is.True);
            Assert.That(idleEntry["behaviours"].Values<string>(), Contains.Item("AnimatorTestBehaviour"));

            var viaAnyEntry = states.Single(s => s["path"].Value<string>() == "ViaAny");
            Assert.That(viaAnyEntry["motionType"].Value<string>(), Is.EqualTo("BlendTree"));

            var transition = (JObject)idleEntry["transitions"][0];
            Assert.That(transition["to"].Value<string>(), Is.EqualTo("Instant"));
            Assert.That(transition["hasExitTime"].Value<bool>(), Is.False);
            Assert.That(((JArray)transition["conditions"]).Count, Is.EqualTo(0));

            var any = Array(result, "anyStateTransitions");
            Assert.That(any.Count, Is.EqualTo(1));
            Assert.That(any[0]["to"].Value<string>(), Is.EqualTo("ViaAny"));
            Assert.That(any[0]["conditions"][0]["parameter"].Value<string>(), Is.EqualTo("Toggle"));
            Assert.That(any[0]["conditions"][0]["threshold"], Is.Null,
                "If carries a threshold of zero that means nothing and must not be reported as part of the test");
        }

        [Test]
        public void InspectAddressesStatesInsideSubStateMachines()
        {
            var nested = this.Root.AddStateMachine("Nested");
            nested.AddState("Deep").motion = this.clip;

            var result = AnimatorInspectTools.Inspect(ControllerPath, layer: "0");
            var paths = Array(result, "states").Select(s => s["path"].Value<string>()).ToArray();

            Assert.That(paths, Contains.Item("Nested/Deep"));
            Assert.That(result["subStateMachines"][0]["path"].Value<string>(), Is.EqualTo("Nested"));
        }

        [Test]
        public void AnAmbiguousLayerNameIsRefusedRatherThanResolvedToTheFirst()
        {
            var refusal = Assert.Throws<McpToolException>(() => AnimatorInspectTools.Inspect(ControllerPath, layer: "Duplicate"));

            Assert.That(refusal.Message, Does.Contain("names 2 layers"));
            Assert.That(AnimatorInspectTools.Inspect(ControllerPath, layer: "1")["layer"]["index"].Value<int>(), Is.EqualTo(1));
        }

        // ── animator_audit ────────────────────────────────────────────────────────

        [Test]
        public void AuditDoesNotCallAStateReachableOnlyThroughAnyStateOrphaned()
        {
            var unreachable = Array(this.Audit(), "unreachableStates")
                .Select(s => s["state"].Value<string>())
                .ToArray();

            Assert.That(unreachable, Does.Not.Contain("ViaAny"),
                "the walk stopped at state transitions and never followed Any State");
        }

        [Test]
        public void AuditReportsAGenuinelyOrphanedStateWithNothingPointingAtIt()
        {
            var unreachable = Array(this.Audit(), "unreachableStates");

            var entry = unreachable.Single(s => s["state"].Value<string>() == "Orphan");
            Assert.That(entry["layer"].Value<string>(), Is.EqualTo("Base Layer"));
            Assert.That(entry["incomingTransitions"].Value<int>(), Is.EqualTo(0));
            Assert.That(unreachable.Count, Is.EqualTo(1));
        }

        [Test]
        public void AuditReportsAnIslandOfStatesThatOnlyReachEachOther()
        {
            var island = this.Root.AddState("Island");
            island.motion = this.clip;
            this.orphan.AddTransition(island).hasExitTime = true;

            var entry = Array(this.Audit(), "unreachableStates").Single(s => s["state"].Value<string>() == "Island");

            // Something does point at it, so the repair is the way into the island, not this state.
            Assert.That(entry["incomingTransitions"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void AuditReportsStatesWithNoMotion()
        {
            var entries = Array(this.Audit(), "statesWithoutMotion");

            Assert.That(entries.Select(e => e["state"].Value<string>()), Is.EquivalentTo(new[] { "Idle" }));
            Assert.That(entries[0]["behaviourCount"].Value<int>(), Is.EqualTo(1),
                "an empty state carrying a behaviour is usually deliberate and the count is what says so");
        }

        [Test]
        public void AuditReportsTransitionsThatFireImmediately()
        {
            var entries = Array(this.Audit(), "immediateTransitions");

            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0]["from"].Value<string>(), Is.EqualTo("Idle"));
            Assert.That(entries[0]["to"].Value<string>(), Is.EqualTo("Instant"));
            Assert.That(entries[0]["layer"].Value<string>(), Is.EqualTo("Base Layer"));
        }

        [Test]
        public void AuditReportsMixedWriteDefaultsWithTheMajorityAndTheDissenters()
        {
            var entries = Array(this.Audit(), "mixedWriteDefaults");

            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0]["layer"].Value<string>(), Is.EqualTo("Base Layer"));
            Assert.That(entries[0]["writeDefaultsOn"].Value<int>(), Is.EqualTo(3));
            Assert.That(entries[0]["writeDefaultsOff"].Value<int>(), Is.EqualTo(1));
            Assert.That(entries[0]["majority"].Value<bool>(), Is.True);

            var disagreeing = (JArray)entries[0]["disagreeing"];
            Assert.That(disagreeing.Count, Is.EqualTo(1));
            Assert.That(disagreeing[0]["state"].Value<string>(), Is.EqualTo("Orphan"));
            Assert.That(disagreeing[0]["writeDefaults"].Value<bool>(), Is.False);
        }

        [Test]
        public void AuditLeavesMajorityNullWhenALayerIsEvenlySplit()
        {
            this.instant.writeDefaultValues = false;

            var entry = Array(this.Audit(), "mixedWriteDefaults").Single();

            Assert.That(entry["majority"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(((JArray)entry["disagreeing"]).Count, Is.EqualTo(4),
                "with no majority every state is listed with its own flag rather than one half named arbitrarily");
        }

        [Test]
        public void AuditReportsOnlyParametersNothingReferences()
        {
            var unused = Array(this.Audit(), "unusedParameters").Select(p => p["name"].Value<string>()).ToArray();

            Assert.That(unused, Is.EquivalentTo(new[] { "Unused" }));

            // Each of the other four is reached by a different route: a transition condition, a
            // state's speed parameter, a blend tree, and a string field on a behaviour.
            Assert.That(unused, Does.Not.Contain("Toggle"));
            Assert.That(unused, Does.Not.Contain("Speed"));
            Assert.That(unused, Does.Not.Contain("Count"));
            Assert.That(unused, Does.Not.Contain("BehaviourDriven"));
        }

        [Test]
        public void AuditReportsDuplicateLayerNamesAndEmptyLayers()
        {
            var result = this.Audit();

            var duplicates = Array(result, "duplicateLayerNames");
            Assert.That(duplicates.Count, Is.EqualTo(1));
            Assert.That(duplicates[0]["name"].Value<string>(), Is.EqualTo("Duplicate"));
            Assert.That(duplicates[0]["indices"].Values<int>(), Is.EquivalentTo(new[] { 1, 2 }));

            var empty = Array(result, "emptyLayers");
            Assert.That(empty.Count, Is.EqualTo(1));
            Assert.That(empty[0]["layer"].Value<string>(), Is.EqualTo("Empty"));
        }

        [Test]
        public void AuditCountsEveryFindingAndChangesNothing()
        {
            var before = System.IO.File.ReadAllText(ControllerPath);
            var result = this.Audit();

            Assert.That(result["findingCount"].Value<int>(), Is.EqualTo(
                Array(result, "unusedParameters").Count +
                Array(result, "statesWithoutMotion").Count +
                Array(result, "unreachableStates").Count +
                Array(result, "emptyLayers").Count +
                Array(result, "duplicateLayerNames").Count +
                Array(result, "immediateTransitions").Count +
                Array(result, "mixedWriteDefaults").Count));

            Assert.That(System.IO.File.ReadAllText(ControllerPath), Is.EqualTo(before));
        }

        // ── editing ───────────────────────────────────────────────────────────────

        [Test]
        public void RemovingALayerTakesItsStatesWithIt()
        {
            var result = AnimatorEditTools.RemoveLayer(ControllerPath, layer: "1");

            Assert.That(result["removed"].Value<string>(), Is.EqualTo("Duplicate"));
            Assert.That(result["removedStates"].Value<int>(), Is.EqualTo(1));
            Assert.That(this.controller.layers.Length, Is.EqualTo(3));
            Assert.That(result["savedToDisk"].Value<bool>(), Is.True);
        }

        [Test]
        public void AddingAStateToAnEmptyLayerMakesItTheDefault()
        {
            var result = AnimatorEditTools.AddState(ControllerPath, layer: "Empty", name: "First", motion: ClipPath);

            Assert.That(result["isDefault"].Value<bool>(), Is.True);
            Assert.That(this.controller.layers[3].stateMachine.defaultState.name, Is.EqualTo("First"));
            Assert.That(this.controller.layers[3].stateMachine.defaultState.motion, Is.EqualTo(this.clip));
        }

        [Test]
        public void AddingAStateReportsTheNameUnityActuallyGaveIt()
        {
            var result = AnimatorEditTools.AddState(ControllerPath, layer: "0", name: "Idle");

            Assert.That(result["renamed"].Value<bool>(), Is.True);
            Assert.That(result["name"].Value<string>(), Is.Not.EqualTo("Idle"));
        }

        [Test]
        public void AddingAStateIntoANamedSubStateMachine()
        {
            this.Root.AddStateMachine("Nested");

            var result = AnimatorEditTools.AddState(ControllerPath, layer: "0", name: "Deep", machine: "Nested");

            Assert.That(result["state"].Value<string>(), Is.EqualTo("Nested/Deep"));
        }

        [Test]
        public void RemovingAStateReportsTheTransitionsThatWentWithIt()
        {
            var result = AnimatorEditTools.RemoveState(ControllerPath, layer: "0", state: "Instant");

            Assert.That(result["removedIncomingTransitions"].Value<int>(), Is.EqualTo(1));
            Assert.That(result["stateCount"].Value<int>(), Is.EqualTo(3));
            Assert.That(this.idle.transitions.Length, Is.EqualTo(0), "Unity removes the transitions into a removed state");
        }

        [Test]
        public void SetStateChangesOnlyWhatWasNamed()
        {
            var result = AnimatorEditTools.SetState(ControllerPath, layer: "0", state: "Idle", speed: 2.5f);

            Assert.That(this.idle.speed, Is.EqualTo(2.5f));
            Assert.That(this.idle.motion, Is.Null, "an unnamed argument must not clear the motion");
            Assert.That(this.idle.writeDefaultValues, Is.True);
            Assert.That(result["changed"].Values<string>().Single(), Does.Contain("speed"));
        }

        [Test]
        public void SetStateClearsTheMotionOnlyForAnEmptyString()
        {
            AnimatorEditTools.SetState(ControllerPath, layer: "0", state: "Instant", motion: string.Empty);

            Assert.That(this.instant.motion, Is.Null);
        }

        [Test]
        public void SetStateWithNothingToDoIsRefused()
        {
            Assert.Throws<McpToolException>(() => AnimatorEditTools.SetState(ControllerPath, layer: "0", state: "Idle"));
        }

        [Test]
        public void SetWriteDefaultsAcrossOneLayerFixesTheMixedLayer()
        {
            var result = AnimatorEditTools.SetWriteDefaults(ControllerPath, writeDefaults: true, layer: "0");

            Assert.That(result["statesChanged"].Value<int>(), Is.EqualTo(1));
            Assert.That(this.orphan.writeDefaultValues, Is.True);
            Assert.That(Array(this.Audit(), "mixedWriteDefaults").Count, Is.EqualTo(0));
        }

        [Test]
        public void SetWriteDefaultsWithoutALayerReachesEveryLayer()
        {
            var result = AnimatorEditTools.SetWriteDefaults(ControllerPath, writeDefaults: false);

            Assert.That(((JArray)result["layers"]).Count, Is.EqualTo(4));
            Assert.That(AnimatorResolve.States(this.Root).All(s => !s.State.writeDefaultValues), Is.True);
        }

        [Test]
        public void AddingATransitionWithConditions()
        {
            var conditions = new JArray
            {
                new JObject { ["parameter"] = "Count", ["mode"] = "Equals", ["threshold"] = 3 },
            };

            var result = AnimatorEditTools.AddTransition(
                ControllerPath, layer: "0", fromState: "Instant", toState: "Idle", conditions: conditions, hasExitTime: true);

            Assert.That(this.instant.transitions.Length, Is.EqualTo(1));
            Assert.That(this.instant.transitions[0].conditions[0].parameter, Is.EqualTo("Count"));
            Assert.That(this.instant.transitions[0].conditions[0].threshold, Is.EqualTo(3f));
            Assert.That(result["note"].Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void AddingATransitionWithNoSourceMakesItAnAnyStateTransition()
        {
            var result = AnimatorEditTools.AddTransition(ControllerPath, layer: "0", toState: "Orphan", hasExitTime: false);

            Assert.That(result["from"].Value<string>(), Is.EqualTo("Any State"));
            Assert.That(this.Root.anyStateTransitions.Length, Is.EqualTo(2));
            Assert.That(result["note"].Value<string>(), Does.Contain("fires as soon"));

            // The orphan now has a way in, so the audit stops calling it unreachable.
            Assert.That(Array(this.Audit(), "unreachableStates").Count, Is.EqualTo(0));
        }

        [Test]
        public void AddingATransitionRefusesAModeTheParameterCannotAnswer()
        {
            var conditions = new JArray
            {
                new JObject { ["parameter"] = "Toggle", ["mode"] = "Greater", ["threshold"] = 1 },
            };

            var refusal = Assert.Throws<McpToolException>(() => AnimatorEditTools.AddTransition(
                ControllerPath, layer: "0", fromState: "Instant", toState: "Idle", conditions: conditions));

            Assert.That(refusal.Message, Does.Contain("If or IfNot"));
            Assert.That(this.instant.transitions.Length, Is.EqualTo(0), "the refusal must land before anything is written");
        }

        [Test]
        public void AddingATransitionRefusesAnUnknownParameter()
        {
            var conditions = new JArray
            {
                new JObject { ["parameter"] = "NoSuchParameter", ["mode"] = "If" },
            };

            Assert.Throws<McpToolException>(() => AnimatorEditTools.AddTransition(
                ControllerPath, layer: "0", fromState: "Instant", toState: "Idle", conditions: conditions));
        }

        [Test]
        public void RemovingATransitionByIndex()
        {
            var result = AnimatorEditTools.RemoveTransition(ControllerPath, layer: "0", fromState: "Idle", index: 0);

            Assert.That(result["removed"]["to"].Value<string>(), Is.EqualTo("Instant"));
            Assert.That(result["remaining"].Value<int>(), Is.EqualTo(0));
            Assert.That(this.idle.transitions.Length, Is.EqualTo(0));
        }

        [Test]
        public void RemovingAnAnyStateTransitionWhenNoSourceIsNamed()
        {
            AnimatorEditTools.RemoveTransition(ControllerPath, layer: "0", index: 0);

            Assert.That(this.Root.anyStateTransitions.Length, Is.EqualTo(0));
        }

        [Test]
        public void RemovingATransitionOutOfRangeIsRefused()
        {
            Assert.Throws<McpToolException>(() => AnimatorEditTools.RemoveTransition(ControllerPath, layer: "0", fromState: "Idle", index: 7));
        }

        [Test]
        public void AddingAParameterKeepsItsDefault()
        {
            var result = AnimatorEditTools.AddParameter(ControllerPath, name: "Weight", type: "Float", defaultValue: 0.25f);

            Assert.That(result["parameter"]["name"].Value<string>(), Is.EqualTo("Weight"));
            Assert.That(this.controller.parameters.Last().defaultFloat, Is.EqualTo(0.25f));
        }

        [Test]
        public void AddingAParameterThatAlreadyExistsIsRefused()
        {
            var refusal = Assert.Throws<McpToolException>(() => AnimatorEditTools.AddParameter(ControllerPath, name: "Toggle", type: "Bool"));

            Assert.That(refusal.Message, Does.Contain("already a parameter"));
            Assert.That(this.controller.parameters.Length, Is.EqualTo(5));
        }

        [Test]
        public void RemovingAParameterListsTheConditionsLeftPointingAtIt()
        {
            var result = AnimatorEditTools.RemoveParameter(ControllerPath, name: "Toggle");

            var referencedBy = Array(result, "referencedBy");
            Assert.That(referencedBy.Count, Is.EqualTo(1));
            Assert.That(referencedBy[0]["from"].Value<string>(), Is.EqualTo("Any State"));
            Assert.That(result["note"].Value<string>(), Does.Contain("no longer fire"));

            // Unity leaves the condition behind naming a parameter that is gone.
            Assert.That(this.Root.anyStateTransitions[0].conditions[0].parameter, Is.EqualTo("Toggle"));
        }

        [Test]
        public void RemovingAnUnusedParameterReportsNothingLeftBehind()
        {
            var result = AnimatorEditTools.RemoveParameter(ControllerPath, name: "Unused");

            Assert.That(Array(result, "referencedBy").Count, Is.EqualTo(0));
            Assert.That(result["note"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(this.controller.parameters.Length, Is.EqualTo(4));
        }

        [Test]
        public void AWriteReachesTheFileOnDiskBeforeItReturns()
        {
            AnimatorEditTools.AddParameter(ControllerPath, name: "WrittenToDisk", type: "Bool");

            Assert.That(System.IO.File.ReadAllText(ControllerPath), Does.Contain("WrittenToDisk"),
                "the reply says savedToDisk, so the file has to hold it");
        }

        [Test]
        public void AWriteToASubAssetReachesTheFileToo()
        {
            // A state is a separate object inside the controller's file. Marking only the controller
            // dirty would save the controller's own fields and leave this one in memory.
            AnimatorEditTools.SetState(ControllerPath, layer: "0", state: "Idle", tag: "TagOnDisk");

            Assert.That(System.IO.File.ReadAllText(ControllerPath), Does.Contain("TagOnDisk"));
        }

        [Test]
        public void AnAssetThatIsNotAControllerIsNamedAsSuch()
        {
            var refusal = Assert.Throws<McpToolException>(() => AnimatorInspectTools.Inspect(ClipPath));

            Assert.That(refusal.Message, Does.Contain("AnimationClip"));
        }

        // ── undo ──────────────────────────────────────────────────────────────────

        [Test]
        public void OneToolCallIsOneUndoStepInMemory()
        {
            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(AnimatorEditTools) });
            var descriptor = catalog.Tools.Single(t => t.Name == "animator_add_parameter");

            Undo.ClearAll();
            Undo.IncrementCurrentGroup();

            ToolInvoker.Invoke(descriptor, new JObject
            {
                ["path"] = ControllerPath,
                ["name"] = "Undone",
                ["type"] = "Bool",
            });

            Assert.That(this.controller.parameters.Length, Is.EqualTo(6));

            Undo.PerformUndo();

            Assert.That(this.controller.parameters.Select(p => p.name), Does.Not.Contain("Undone"),
                "the write was not on the undo stack; the tool descriptions promise one Ctrl+Z reverses it");

            // The change is still in the file. Undo restores the objects in memory and nothing
            // rewrites the asset, which is why every editing tool says so.
            Assert.That(System.IO.File.ReadAllText(ControllerPath), Does.Contain("Undone"));

            Undo.ClearAll();
        }

        [Test]
        public void UndoBringsBackARemovedLayerAndTheSubAssetsItDestroyed()
        {
            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(AnimatorEditTools) });
            var descriptor = catalog.Tools.Single(t => t.Name == "animator_remove_layer");

            Undo.ClearAll();
            Undo.IncrementCurrentGroup();

            ToolInvoker.Invoke(descriptor, new JObject { ["path"] = ControllerPath, ["layer"] = "1" });

            Assert.That(this.controller.layers.Length, Is.EqualTo(3));

            Undo.PerformUndo();

            Assert.That(this.controller.layers.Length, Is.EqualTo(4));
            Assert.That(this.controller.layers[1].stateMachine, Is.Not.Null,
                "the layer came back pointing at a state machine that was destroyed and not restored");
            Assert.That(this.controller.layers[1].stateMachine.states.Length, Is.EqualTo(1));

            Undo.ClearAll();
        }
    }
}
