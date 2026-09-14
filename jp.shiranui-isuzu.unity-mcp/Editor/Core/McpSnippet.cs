using System.Globalization;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class McpSnippet
    {
        public static string PathOf(GameObject go) => Tools.ObjectResolve.PathOf(go);
        public static GameObject Find(string path) => Tools.ObjectResolve.Object(path, null);
        public static string IdOf(Object obj) => obj == null ? null : Core.EntityIdCompat.IdOf(obj).ToString(CultureInfo.InvariantCulture);
        public static T[] All<T>(bool includeInactive = false) where T : Object
        {
            var inactive = includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType<T>(inactive);
#else
            return Object.FindObjectsByType<T>(inactive, FindObjectsSortMode.None);
#endif
        }
    }
}
