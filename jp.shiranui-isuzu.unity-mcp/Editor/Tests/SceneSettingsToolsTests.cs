using NUnit.Framework;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The sky, the ambient light and the fog are most of what a room looks like before a single
    /// object is placed, and they live on a scene-wide singleton that nothing addresses by path.
    /// </summary>
    public sealed class SceneSettingsToolsTests
    {
        private bool fog;
        private Color fogColor;
        private FogMode fogMode;
        private float ambientIntensity;

        [SetUp]
        public void Remember()
        {
            this.fog = RenderSettings.fog;
            this.fogColor = RenderSettings.fogColor;
            this.fogMode = RenderSettings.fogMode;
            this.ambientIntensity = RenderSettings.ambientIntensity;
        }

        [TearDown]
        public void Restore()
        {
            RenderSettings.fog = this.fog;
            RenderSettings.fogColor = this.fogColor;
            RenderSettings.fogMode = this.fogMode;
            RenderSettings.ambientIntensity = this.ambientIntensity;
        }

        [Test]
        public void ReadingWithNoArgumentsReportsEverySetting()
        {
            var all = SceneSettingsTools.SceneSettings();

            Assert.That(all["settings"]["fog"], Is.Not.Null);
            Assert.That(all["settings"]["skybox"], Is.Not.Null);
            Assert.That(all["settings"]["ambientMode"], Is.Not.Null);
        }

        [Test]
        public void AFlagAColourAndAnEnumAllTakeTheirOwnShape()
        {
            SceneSettingsTools.SceneSettings("fog", true);
            Assert.That(RenderSettings.fog, Is.True);

            SceneSettingsTools.SceneSettings("fogColor", Newtonsoft.Json.Linq.JObject.Parse(
                "{\"r\":1,\"g\":0,\"b\":0,\"a\":1}"));
            Assert.That(RenderSettings.fogColor.r, Is.EqualTo(1f).Within(0.001f));

            SceneSettingsTools.SceneSettings("fogMode", "Linear");
            Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.Linear));

            SceneSettingsTools.SceneSettings("ambientIntensity", 0.5f);
            Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(0.5f).Within(0.001f));
        }

        /// <summary>A name that is not a setting has to be named back, with what is.</summary>
        [Test]
        public void ASettingThatDoesNotExistIsRefusedWithTheOnesThatDo()
        {
            var thrown = Assert.Throws<McpToolException>(() => SceneSettingsTools.SceneSettings("nosuch"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("fogColor"));
        }

        [Test]
        public void AnEnumValueThatIsNotOneOfTheChoicesNamesThem()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => SceneSettingsTools.SceneSettings("fogMode", "Nope"));

            Assert.That(thrown.Message, Does.Contain("ExponentialSquared"));
        }

        [Test]
        public void AValueWithoutAPropertyIsRefused()
        {
            Assert.Throws<McpToolException>(() => SceneSettingsTools.SceneSettings(null, true));
        }
    }
}
