using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityObject = UnityEngine.Object;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// One place that knows how an object is identified across Unity versions.
    /// </summary>
    /// <remarks>
    /// Unity 6.5 made the int instance-id API obsolete-as-error and added the EntityId conversion
    /// helpers (<c>EntityId.ToULong</c> / <c>FromULong</c>, <c>objectReferenceEntityIdValue</c>).
    /// 6.0 through 6.4 keep the int API without warning, so the split is at 6.5, not earlier —
    /// verified by compiling against 6000.3 (int API present, EntityId helpers absent) and 6000.5
    /// (the reverse). The same three operations — object to id, id to object, and a serialized
    /// reference's id — therefore have two spellings depending on the Editor. Splitting on
    /// <c>UNITY_6000_5_OR_NEWER</c> here means the ten call sites do not each carry a <c>#if</c>,
    /// and the wire contract stays put: the value is still called <c>instanceId</c>.
    /// <para>
    /// In process the id is a <c>long</c>. On the wire it is a JSON number before 6.5 and a JSON
    /// string from 6.5 on. A 6.5 EntityId packed into a ulong is around 5.7e17, far above the
    /// 2^53 a JSON number survives on the way through JavaScript: the MCP server and every MCP
    /// client parse numbers as doubles, so a numeric id would come back with its low bits rounded
    /// off and name a different object, or none. <see cref="Wire"/> makes that choice once, and
    /// <c>ToolInvoker</c> accepts either spelling as an argument without going through a double.
    /// </para>
    /// </remarks>
    internal static class EntityIdCompat
    {
        /// <summary>The identifier of an object, as a value.</summary>
        public static long IdOf(UnityObject obj)
        {
#if UNITY_6000_5_OR_NEWER
            return (long)UnityEngine.EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>The object an identifier names, or null.</summary>
        public static UnityObject Find(long id)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject(UnityEngine.EntityId.FromULong((ulong)id));
#else
            return EditorUtility.InstanceIDToObject((int)id);
#endif
        }

        /// <summary>The identifier of the object a serialized property points at.</summary>
        public static long ObjectReferenceId(SerializedProperty property)
        {
#if UNITY_6000_5_OR_NEWER
            return (long)UnityEngine.EntityId.ToULong(property.objectReferenceEntityIdValue);
#else
            return property.objectReferenceInstanceIDValue;
#endif
        }

        /// <summary>
        /// An identifier in the form it is written into a tool result: a JSON string on Unity 6.5
        /// and later, where it does not fit a JSON number exactly, and a JSON number before that.
        /// </summary>
        public static JToken Wire(long id)
        {
#if UNITY_6000_5_OR_NEWER
            return new JValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
#else
            return new JValue(id);
#endif
        }

        /// <summary><see cref="Wire"/> of <see cref="IdOf"/>.</summary>
        public static JToken WireIdOf(UnityObject obj) => Wire(IdOf(obj));

        /// <summary><see cref="Wire"/> of <see cref="ObjectReferenceId"/>.</summary>
        public static JToken WireObjectReferenceId(SerializedProperty property) => Wire(ObjectReferenceId(property));
    }
}
