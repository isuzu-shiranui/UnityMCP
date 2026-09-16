using NUnit.Framework;

using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers the one line of a shader message that says which variant produced it. Unity writes
    /// that line in a few shapes, and the rest of the context is the platform defines the reply
    /// leaves out.
    /// </summary>
    [TestFixture]
    internal sealed class ShaderMessageTests
    {
        [Test]
        public void ANamedPassKeepsItsSubshaderNameAndStage()
        {
            var details = "Compiling Subshader: 0, Pass: ForwardLit, Fragment program with <no keywords>\n"
                + "Platform defines: SHADER_API_DESKTOP UNITY_ENABLE_DETAIL_NORMALMAP\n"
                + "Disabled keywords: INSTANCING_ON _NORMALMAP";

            Assert.That(ShaderTools.PassLabel(details), Is.EqualTo("Subshader 0 / ForwardLit / fragment"));
        }

        [Test]
        public void AMessageThatNamesNoPassCarriesTheStageAlone()
        {
            var details = "Compiling Vertex program with DIRECTIONAL SHADOWS_SCREEN\n"
                + "Platform defines: SHADER_API_DESKTOP";

            Assert.That(ShaderTools.PassLabel(details), Is.EqualTo("vertex"));
        }

        [Test]
        public void TextThatNamesNoVariantHasNoPass()
        {
            Assert.That(ShaderTools.PassLabel("Shader warning in 'Custom/Thing': implicit truncation"), Is.Null);
            Assert.That(ShaderTools.PassLabel(""), Is.Null);
            Assert.That(ShaderTools.PassLabel(null), Is.Null);
        }
    }
}
