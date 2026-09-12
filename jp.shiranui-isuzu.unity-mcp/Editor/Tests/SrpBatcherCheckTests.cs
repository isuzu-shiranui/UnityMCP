using System.IO;

using NUnit.Framework;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    internal sealed class SrpBatcherCheckTests
    {
        private const string ScratchFolder = "Assets/__srpbatchertest";

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;

            if (AssetDatabase.IsValidFolder(ScratchFolder))
            {
                AssetDatabase.DeleteAsset(ScratchFolder);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void AShaderThatFailedToCompileIsReportedRatherThanGivenAVerdict()
        {
            // Unity substitutes an error subshader for a failed compile, so the check would run and
            // answer for something that renders magenta and batches nothing. The Editor's own bounds
            // check on the subshader index is compiled out of a release build, so the tool must never
            // reach it with a made-up index either; this call has taken an Editor down once.
            LogAssert.ignoreFailingMessages = true;

            Directory.CreateDirectory(ScratchFolder);
            var path = ScratchFolder + "/Broken.shader";
            File.WriteAllText(path, "Shader \"Bench/Broken\" { this is not shaderlab }");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null, "a broken shader still imports as a Shader asset");
            Assert.That(shader.isSupported, Is.False);

            var answer = SrpBatcherCheck.Check(shader);

            Assert.That((bool)answer["checked"], Is.False);
            Assert.That((string)answer["note"], Does.Contain("compile"));
            Assert.That(answer["compatible"], Is.Null, "no verdict is invented for a shader that has none");

            // The name comes back empty for a broken shader, and a row that names nothing cannot be
            // acted on.
            Assert.That((string)answer["shader"], Is.Not.Empty);
        }

        [Test]
        public void TheEditorStillExposesTheCompatibilityCheckThisToolReads()
        {
            // Four internal ShaderUtil methods carry the whole tool. If a later Unity renames one,
            // the tool refuses as unsupported on every project, which would otherwise look like a
            // project problem rather than a broken tool.
            Assert.That(SrpBatcherCheck.Available, Is.True);
        }

        [Test]
        public void TheBuiltInPipelineIsRefusedRatherThanReportingEveryShaderAsIncompatible()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                Assert.Ignore("This project is on a scriptable pipeline, where the check does run.");
            }

            var thrown = Assert.Throws<McpToolException>(() => SrpBatcherCheck.RequireSupport());
            Assert.That(thrown.Code, Is.EqualTo("not_supported"));
            Assert.That(thrown.HttpStatus, Is.EqualTo(501));
            Assert.That(thrown.Message, Does.Contain("built-in"));
        }

        [Test]
        public void NamingNothingIsRefusedWithEveryWayOfNamingSomething()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => ShaderTools.RequireOneSource(null, null, null, null, null));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(
                thrown.Message,
                Does.Contain("path").And.Contain("name").And.Contain("object_path").And.Contain("scene"));
        }

        [Test]
        public void NamingTwoSourcesIsRefusedRatherThanOneBeingPicked()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => ShaderTools.RequireOneSource(null, "Standard", null, null, "scene"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("name").And.Contain("scope"));
        }

        [Test]
        public void AScopeOtherThanSceneIsRefusedWithWhatItTakes()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => ShaderTools.RequireOneSource(null, null, null, null, "project"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("'scene'"));
        }

        [Test]
        public void OneSourceIsAccepted()
        {
            Assert.DoesNotThrow(() => ShaderTools.RequireOneSource(null, "Standard", null, null, null));
            Assert.DoesNotThrow(() => ShaderTools.RequireOneSource(null, null, null, null, "scene"));
            Assert.DoesNotThrow(() => ShaderTools.RequireOneSource(null, null, "/Cube", null, null));
            Assert.DoesNotThrow(() => ShaderTools.RequireOneSource(null, null, null, 42L, null));
        }

        [Test]
        public void TheArgumentsAreJudgedBeforeThePipelineIs()
        {
            // A malformed call gets told what is wrong with it whether or not this project has a
            // scriptable pipeline, so the same call does not report two different problems on two
            // machines.
            var thrown = Assert.Throws<McpToolException>(() => ShaderTools.BatchingCheck());
            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
        }
    }
}
