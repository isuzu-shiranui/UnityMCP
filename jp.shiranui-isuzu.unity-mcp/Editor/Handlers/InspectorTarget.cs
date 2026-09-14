using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Handlers
{
    internal sealed class InspectorTarget : IDisposable
    {
        public UnityEngine.Object Target { get; private set; }
        private GameObject contents;
        private string assetPath;
        public bool IsAsset => this.assetPath != null;
        public static InspectorTarget Open(string assetPath, string objectPath, long? instanceId)
        {
            var scope = new InspectorTarget { assetPath = assetPath };
            if (assetPath == null)
            {
                scope.Target = ObjectResolve.Object(objectPath, instanceId);
                return scope;
            }
            if (instanceId.HasValue) throw new McpToolException("invalid_params", "asset_path cannot be combined with instance_id.");
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) throw new McpToolException("not_found", $"No main asset at '{assetPath}'.");
            try
            {
                if (asset is GameObject && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    scope.contents = PrefabUtility.LoadPrefabContents(assetPath);
                    scope.Target = ObjectResolve.Relative(scope.contents, objectPath);
                }
                else
                {
                    if (!string.IsNullOrEmpty(objectPath)) throw new McpToolException("invalid_params", "object_path with asset_path applies only to prefab contents.");
                    scope.Target = asset;
                }
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }
        public void Save()
        {
            if (this.contents != null)
            {
                PrefabUtility.SaveAsPrefabAsset(this.contents, this.assetPath, out var success);
                if (!success) throw new McpToolException("tool_failed", $"Saving '{this.assetPath}' was not confirmed.");
            }
            else
            {
                AssetDatabase.SaveAssetIfDirty(this.Target);
                if (EditorUtility.IsDirty(this.Target)) throw new McpToolException("tool_failed", $"Saving '{this.assetPath}' was not confirmed.");
            }
        }
        public JObject Overrides(UnityEngine.Object target, string[] properties)
        {
            var result = new JObject();
            if (this.contents == null) return result;
            var source = this.SourceOf(target);
            if (source == null) return result;
            foreach (var property in properties) result[property] = new JArray();
            foreach (var go in UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!go.scene.IsValid() || !go.scene.isLoaded || EditorUtility.IsPersistent(go)
                    || EditorSceneManager.IsPreviewScene(go.scene)) continue;
                var objects = target is GameObject ? new UnityEngine.Object[] { go } : go.GetComponents<Component>().Cast<UnityEngine.Object>();
                foreach (var instance in objects)
                {
                    if (instance == null || PrefabUtility.GetCorrespondingObjectFromSourceAtPath(instance, this.assetPath) != source) continue;
                    foreach (var property in properties)
                    {
                        var list = (JArray)result[property];
                        for (var current = instance; current != null && current != source; current = PrefabUtility.GetCorrespondingObjectFromSource(current))
                        {
                            using var serialized = new SerializedObject(current);
                            var prop = SerializedPropertyPath.Find(serialized, property, out _);
                            if (prop == null || !prop.prefabOverride) continue;
                            var path = ObjectResolve.PathOf(go);
                            if (list.Count < 50 && !list.Values<string>().Contains(path)) list.Add(path);
                            break;
                        }
                    }
                }
            }
            return result;
        }
        private UnityEngine.Object SourceOf(UnityEngine.Object target)
        {
            var targetGo = target is Component component ? component.gameObject : (GameObject)target;
            var indices = new System.Collections.Generic.Stack<int>();
            for (var t = targetGo.transform; t != this.contents.transform; t = t.parent) indices.Push(t.GetSiblingIndex());
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(this.assetPath).transform;
            while (indices.Count > 0) source = source.GetChild(indices.Pop());
            if (target is GameObject) return source.gameObject;
            var siblings = targetGo.GetComponents(target.GetType());
            return source.GetComponents(target.GetType())[Array.IndexOf(siblings, target)];
        }
        public void Dispose()
        {
            if (this.contents != null) PrefabUtility.UnloadPrefabContents(this.contents);
        }
    }
}
