using UnityEngine;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A StateMachineBehaviour that names a parameter in a string field, as VRChat's own ones do.
    /// </summary>
    /// <remarks>
    /// In its own file, and named after it: <c>AnimatorState.AddStateMachineBehaviour</c> has to
    /// find the MonoScript for the type, and for a class whose file has another name it finds
    /// nothing and returns null rather than failing.
    /// </remarks>
    internal sealed class AnimatorTestBehaviour : StateMachineBehaviour
    {
        public string parameterName;
    }
}
