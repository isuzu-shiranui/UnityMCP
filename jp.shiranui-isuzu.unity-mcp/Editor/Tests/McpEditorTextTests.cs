using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using NUnit.Framework;

using UnityEditor;
using UnityEngine;

using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The Preferences page is the only translated surface, and a bad entry in its table shows up
    /// as a broken page rather than as a compile error.
    /// </summary>
    [TestFixture]
    internal sealed class McpEditorTextTests
    {
        private static readonly Regex Placeholder = new Regex(@"\{(\d+)\}");

        private int savedLanguage;
        private bool hadEditorLocale;
        private string savedEditorLocale;

        [SetUp]
        public void SaveLanguage()
        {
            this.savedLanguage = McpSettings.instance.uiLanguage;
            this.hadEditorLocale = EditorPrefs.HasKey("Editor.kEditorLocale");
            this.savedEditorLocale = EditorPrefs.GetString("Editor.kEditorLocale", string.Empty);
        }

        [TearDown]
        public void RestoreLanguage()
        {
            McpSettings.instance.uiLanguage = this.savedLanguage;
            if (this.hadEditorLocale)
            {
                EditorPrefs.SetString("Editor.kEditorLocale", this.savedEditorLocale);
            }
            else
            {
                EditorPrefs.DeleteKey("Editor.kEditorLocale");
            }
        }

        [TestCase(McpUiLanguage.Japanese)]
        [TestCase(McpUiLanguage.Vietnamese)]
        public void EveryTranslationCarriesTheSamePlaceholdersAsItsKey(McpUiLanguage language)
        {
            var wrong = new List<string>();

            foreach (var entry in Entries(language))
            {
                if (!Indices(entry.Key).SetEquals(Indices(entry.Value)))
                {
                    wrong.Add(entry.Key);
                }
            }

            Assert.That(wrong, Is.Empty, "these translations do not use the same placeholders as their key");
        }

        [Test]
        public void EnglishDrawsTheKeyItself()
        {
            McpSettings.instance.uiLanguage = (int)McpUiLanguage.English;

            Assert.That(McpEditorText.Resolve(), Is.EqualTo(SystemLanguage.English));
            Assert.That(McpEditorText.Tr("Connection"), Is.EqualTo("Connection"));
        }

        [Test]
        public void JapaneseDrawsTheTranslation()
        {
            McpSettings.instance.uiLanguage = (int)McpUiLanguage.Japanese;

            Assert.That(McpEditorText.Resolve(), Is.EqualTo(SystemLanguage.Japanese));
            Assert.That(McpEditorText.Tr("Connection"), Is.EqualTo("接続"));
        }

        [Test]
        public void VietnameseDrawsTheTranslation()
        {
            McpSettings.instance.uiLanguage = (int)McpUiLanguage.Vietnamese;

            Assert.That(McpEditorText.Resolve(), Is.EqualTo(SystemLanguage.Vietnamese));
            Assert.That(McpEditorText.Tr("Connection"), Is.EqualTo("Kết nối"));
            Assert.That(string.Format(McpEditorText.Tr("Listening on port {0}"), 27400),
                Is.EqualTo("Đang lắng nghe trên cổng 27400"));
        }

        [Test]
        public void AutoFollowsTheVietnameseEditorLocale()
        {
            EditorPrefs.SetString("Editor.kEditorLocale", "Vietnamese");
            McpSettings.instance.uiLanguage = (int)McpUiLanguage.Auto;

            Assert.That(McpEditorText.Resolve(), Is.EqualTo(SystemLanguage.Vietnamese));
            Assert.That(McpEditorText.Tr("Connection"), Is.EqualTo("Kết nối"));
        }

        [Test]
        public void ExplicitVietnameseOverridesTheEditorLocale()
        {
            EditorPrefs.SetString("Editor.kEditorLocale", "Japanese");
            McpSettings.instance.uiLanguage = (int)McpUiLanguage.Vietnamese;

            Assert.That(McpEditorText.Resolve(), Is.EqualTo(SystemLanguage.Vietnamese));
        }

        [Test]
        public void VietnameseTranslatesBothLabelAndTooltip()
        {
            McpSettings.instance.uiLanguage = (int)McpUiLanguage.Vietnamese;

            var content = McpEditorText.Content("Auto-start on launch", "Start the server when the Editor opens this project.");

            Assert.That(content.text, Is.EqualTo("Tự khởi chạy"));
            Assert.That(content.tooltip, Is.EqualTo("Khởi chạy máy chủ khi Editor mở dự án này."));
        }

        [TestCase(McpUiLanguage.Japanese)]
        [TestCase(McpUiLanguage.Vietnamese)]
        public void AnUntranslatedStringDrawsInEnglish(McpUiLanguage language)
        {
            McpSettings.instance.uiLanguage = (int)language;

            const string absent = "A string that is deliberately not in the table.";
            Assert.That(McpEditorText.Tr(absent), Is.EqualTo(absent));
        }

        [TestCase(McpUiLanguage.Japanese)]
        [TestCase(McpUiLanguage.Vietnamese)]
        public void NoTranslationIsLeftAsItsKey(McpUiLanguage language)
        {
            var untranslated = Entries(language)
                .Where(e => string.IsNullOrWhiteSpace(e.Value) || e.Key == e.Value)
                .Select(e => e.Key)
                .Where(k => k != "MCP URL")
                .ToList();

            Assert.That(untranslated, Is.Empty, "these entries add nothing over the English");
        }

        private static IReadOnlyDictionary<string, string> Entries(McpUiLanguage language)
        {
            return language == McpUiLanguage.Vietnamese
                ? McpEditorText.VietnameseEntries
                : McpEditorText.JapaneseEntries;
        }

        private static HashSet<string> Indices(string text)
        {
            return new HashSet<string>(Placeholder.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value));
        }
    }
}
