using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Ugui.Tests
{
    internal sealed class UiClickProbe : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public readonly List<string> Events = new();
        public bool DisableOnDown;
        public void OnPointerEnter(PointerEventData e) => Events.Add("enter");
        public void OnPointerDown(PointerEventData e)
        {
            Events.Add("down");
            Assert.That(e.pointerPressRaycast.gameObject, Is.Not.Null);
            Assert.That(e.pressPosition, Is.EqualTo(e.position));
            Assert.That(e.eligibleForClick, Is.True);
            if (DisableOnDown) GetComponent<Button>().interactable = false;
        }
        public void OnPointerUp(PointerEventData e) => Events.Add("up");
        public void OnPointerClick(PointerEventData e) => Events.Add("click");
        public void OnPointerExit(PointerEventData e) => Events.Add("exit");
    }

    internal sealed class UiClickTests
    {
        private GameObject root;
        private GameObject systemObject;
        [SetUp] public void SetUp()
        {
            root = new GameObject("ClickRoot", typeof(RectTransform));
            systemObject = new GameObject("ClickEvents", typeof(EventSystem));
        }
        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(systemObject);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void PressUsesAncestorHandlerAndStopsWhenDisabled(bool disable)
        {
            root.AddComponent<Button>();
            var probe = root.AddComponent<UiClickProbe>();
            probe.DisableOnDown = disable;
            var child = new GameObject("Label", typeof(RectTransform));
            child.transform.SetParent(root.transform);
            var system = systemObject.GetComponent<EventSystem>();
            var result = UiClickTools.Deliver(system, root, new RaycastResult { gameObject = child },
                new PointerEventData(system) { position = new Vector2(4, 8) });
            Assert.That((bool)result["clicked"], Is.EqualTo(!disable));
            Assert.That(probe.Events, Is.EqualTo(disable ? new[] { "enter", "down", "exit" } : new[] { "enter", "down", "up", "click", "exit" }));
            if (disable) Assert.That((string)result["stoppedAt"], Is.EqualTo("down"));
            else Assert.That((string)result["handler"], Is.EqualTo("/ClickRoot"));
        }
        [Test]
        public void RepeatedLabelsRefuseInsteadOfChoosingOne()
        {
            root.AddComponent<Button>();
            var child = new GameObject("ClickRoot", typeof(RectTransform));
            child.transform.SetParent(root.transform);
            child.AddComponent<Button>();
            Assert.That(Assert.Throws<McpToolException>(() => UiClickTools.FindLabel("ClickRoot")).Message,
                Does.Contain("/ClickRoot/ClickRoot"));
        }
        [TestCase("UnityEngine.UI.Text")]
        [TestCase("TMPro.TextMeshProUGUI")]
        public void SnapshotDiffReportsChangedAndDestroyedText(string typeName)
        {
            var type = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName)).FirstOrDefault(candidate => candidate != null);
            if (type == null) Assert.Ignore(typeName + " is not installed.");
            var label = root.AddComponent(type);
            var text = type.GetProperty("text");
            text.SetValue(label, "Idle");
            root.SetActive(false);
            var before = UiClickTools.Snapshot();
            text.SetValue(label, "Started");
            var changes = UiClickTools.Difference(before, UiClickTools.Snapshot());
            Assert.That((string)changes.Single(c => (string)c["path"] == "/ClickRoot")["from"], Is.EqualTo("Idle"));
            Assert.That((string)changes.Single(c => (string)c["path"] == "/ClickRoot")["to"], Is.EqualTo("Started"));
            Object.DestroyImmediate(label);
            changes = UiClickTools.Difference(before, UiClickTools.Snapshot());
            Assert.That(changes.Single(c => (string)c["path"] == "/ClickRoot")["to"].Type, Is.EqualTo(JTokenType.Null));
        }
    }
}
