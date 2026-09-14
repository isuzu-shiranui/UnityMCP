using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    [InitializeOnLoad]
    internal static class PlayModeRequest
    {
        private const string Key = "UnityMCP.PlayModeRequest";
        private const string Label = "play_mode_request";
        static PlayModeRequest()
        {
            EditorApplication.playModeStateChanged += StateChanged;
            if (Pending != null) FrameSequencer.Run(Track(Read()), Label);
        }
        private static JObject Read() => JObject.Parse(SessionState.GetString(Key, "{}"));
        private static void Save(JObject state) => SessionState.SetString(Key, state.ToString(Newtonsoft.Json.Formatting.None));
        public static string Pending => (string)Read()["pending"];
        public static JToken Refused => Read()["refused"] ?? JValue.CreateNull();
        internal static void Clear() => SessionState.EraseString(Key);

        public static void Begin(string action, bool paused = false)
        {
            var state = new JObject { ["id"] = Guid.NewGuid().ToString("N"), ["pending"] = action, ["paused"] = paused };
            Save(state);
            FrameSequencer.Run(Track(state), Label);
        }
        private static void StateChanged(PlayModeStateChange change)
        {
            var state = Read();
            var action = (string)state["pending"];
            if (action == "play" && change == PlayModeStateChange.EnteredPlayMode)
            {
                EditorApplication.isPaused = (bool)state["paused"];
                state["entered"] = true;
                Save(state);
            }
            else if (action == "stop" && change == PlayModeStateChange.EnteredEditMode)
            {
                state["pending"] = null;
                Save(state);
            }
        }
        private static IEnumerator<FrameStep> Track(JObject request)
        {
            // Defer until the HTTP response is sent before a domain reload drops the connection.
            // delayCall can wait forever in an unfocused Editor after the request is answered;
            // FrameSequencer keeps the loop waker active until the deferred work has run.
            yield return FrameStep.Wait();
            var idleFrames = 0;
            while (true)
            {
                var state = Read();
                if ((string)state["id"] != (string)request["id"] || state["pending"]?.Type != JTokenType.String) break;
                var play = (string)state["pending"] == "play";
                if ((bool?)state["dispatched"] != true)
                {
                    state["dispatched"] = true;
                    Save(state);
                    if (play) EditorApplication.isPaused = (bool)state["paused"];
                    EditorApplication.isPlaying = play;
                }
                else if (play && EditorApplication.isPlaying && (bool?)state["entered"] == true)
                {
                    // EnteredPlayMode follows the initial scene update on supported Editors.
                    if (Time.frameCount == 0)
                    {
                        EditorApplication.Step();
                        EditorApplication.isPaused = (bool)state["paused"];
                    }
                    if (Time.frameCount > 0 && EditorApplication.isPaused == (bool)state["paused"])
                    {
                        state["pending"] = null;
                        Save(state);
                        break;
                    }
                }
                else if (!play && !EditorApplication.isPlaying)
                {
                    state["pending"] = null;
                    Save(state);
                    break;
                }
                else if (play && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    if (++idleFrames >= 2)
                    {
                        state["refused"] = new JObject { ["action"] = state["pending"], ["reason"] = EditorUtility.scriptCompilationFailed
                            ? "Play mode was refused because scripts have compile errors."
                            : $"The request did not complete: isPlaying={EditorApplication.isPlaying}, isPlayingOrWillChangePlaymode={EditorApplication.isPlayingOrWillChangePlaymode}." };
                        state["pending"] = null;
                        Save(state);
                        break;
                    }
                }
                else idleFrames = 0;
                yield return FrameStep.Wait();
            }
            yield return FrameStep.Done(new JObject { ["ok"] = true });
        }
    }
}
