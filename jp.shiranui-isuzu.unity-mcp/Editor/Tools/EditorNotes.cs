using Newtonsoft.Json.Linq;

using UnityEditor;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Warnings that belong on a result rather than in a description.
    /// </summary>
    internal static class EditorNotes
    {
        /// <summary>
        /// Adds a note when a scene change is about to be thrown away.
        /// </summary>
        /// <remarks>
        /// Play Mode reverts the scene on exit. A tool that edits a GameObject during Play Mode
        /// therefore succeeds, reports the new state truthfully, and leaves nothing behind — the
        /// caller has no way to tell that from a durable edit, and finds out when the object is
        /// gone. Measured: gameobject_create in Play Mode returns a full success payload and the
        /// object does not exist after stopping, while asset edits made at the same moment
        /// survive. The asymmetry is the dangerous part, so it is said out loud at the moment it
        /// applies rather than left in a description that is read once.
        /// </remarks>
        public static JObject SceneChange(JObject result)
        {
            if (result != null && EditorApplication.isPlaying)
            {
                result["playModeWarning"] =
                    "Play Mode is running. Scene changes are reverted when it stops, so this edit " +
                    "will not survive. Asset changes made now do survive.";
            }

            return result;
        }
    }
}
