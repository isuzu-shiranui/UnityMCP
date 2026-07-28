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
    /// and the wire contract stays put: the value is still called <c>instanceId</c> and is still a
    /// JSON number.
    /// <para>
    /// The wire type is <c>long</c>, not <c>int</c>. A 64-bit EntityId does not fit in an int, and
    /// truncating it would hand back an id that resolves to the wrong object or to nothing. Real
    /// ids are small — a scene object's is a negative number in the thousands — so this stays well
    /// inside the range a JSON number represents exactly.
    /// </para>
    /// </remarks>
    internal static class EntityIdCompat
    {
        /// <summary>The wire identifier for an object.</summary>
        public static long IdOf(UnityObject obj)
        {
#if UNITY_6000_5_OR_NEWER
            return (long)UnityEngine.EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>The object a wire identifier names, or null.</summary>
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
    }
}
