using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using NUnit.Framework;

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

        [SetUp]
        public void SaveLanguage()
        {
            this.savedLanguage = McpSettings.instance.uiLanguage;
        }

        [TearDown]
        public void RestoreLanguage()
        {
            McpSettings.instance.uiLanguage = this.savedLanguage;
        }

        [Test]
        public void EveryTranslationCarriesTheSamePlaceholdersAsItsKey()
        {
            var wrong = new List<string>();

            foreach (var entry in McpEditorText.JapaneseEntries)
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
        public void AnUntranslatedStringDrawsInEnglish()
        {
            McpSettings.instance.uiLanguage = (int)McpUiLanguage.Japanese;

            const string absent = "A string that is deliberately not in the table.";
            Assert.That(McpEditorText.Tr(absent), Is.EqualTo(absent));
        }

        [Test]
        public void NoTranslationIsLeftAsItsKey()
        {
            var untranslated = McpEditorText.JapaneseEntries
                .Where(e => e.Key == e.Value)
                .Select(e => e.Key)
                .Where(k => k != "MCP URL")
                .ToList();

            Assert.That(untranslated, Is.Empty, "these entries add nothing over the English");
        }

        private static HashSet<string> Indices(string text)
        {
            return new HashSet<string>(Placeholder.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value));
        }
    }
}
