using System.IO;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Writing a .unitypackage from project assets.
    /// </summary>
    /// <remarks>
    /// The assertions read the file off disk rather than trusting the reply, because the failure
    /// this tool has to rule out is a call that returns a success envelope while nothing was
    /// written. The fixture is a material referencing a texture, so include_dependencies has
    /// something to reach that the plain export leaves out.
    /// </remarks>
    [TestFixture]
    internal sealed class AssetExportTests
    {
        private const string Folder = "Assets/_McpExportTests";
        private const string TexturePath = Folder + "/Fixture.png";
        private const string MaterialPath = Folder + "/Fixture.mat";

        private string outputDirectory;

        [Test]
        public void ExportPreservesAnUnrelatedPartialFile()
        {
            Directory.CreateDirectory(this.outputDirectory);
            var file = Path.Combine(this.outputDirectory, "preserve.unitypackage");
            File.WriteAllText(file + ".partial", "unrelated");
            AssetTools.ExportPackage(new[] { MaterialPath }, file);
            Assert.That(File.ReadAllText(file + ".partial"), Is.EqualTo("unrelated"));
            Assert.That(new FileInfo(file).Length, Is.GreaterThan(0));
        }

        [Test]
        [Platform("Win")]
        public void ALockedDestinationKeepsThePreviousBackupWhenCommitFails()
        {
            Directory.CreateDirectory(this.outputDirectory);
            var file = Path.Combine(this.outputDirectory, "locked.unitypackage");
            File.WriteAllText(file, "previous backup");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // A refusal the caller can read, not the 500 an escaping IOException becomes.
                var error = Assert.Throws<McpToolException>(
                    () => AssetTools.ExportPackage(new[] { MaterialPath }, file, overwrite: true));

                Assert.That(error.Message, Does.Contain("untouched"));
            }
            Assert.That(File.ReadAllText(file), Is.EqualTo("previous backup"));
            Assert.That(Directory.GetFiles(this.outputDirectory, "*.partial"), Is.Empty);
        }

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "_McpExportTests");
            }

            AssetDatabase.DeleteAsset(MaterialPath);
            AssetDatabase.DeleteAsset(TexturePath);

            var texture = new Texture2D(4, 4);
            texture.SetPixel(0, 0, Color.magenta);
            texture.Apply();
            File.WriteAllBytes(TexturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(TexturePath, ImportAssetOptions.ForceSynchronousImport);

            var material = new Material(Shader.Find("Unlit/Texture"))
            {
                mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath),
            };

            AssetDatabase.CreateAsset(material, MaterialPath);
            AssetDatabase.SaveAssets();

            this.outputDirectory = Path.Combine(Path.GetTempPath(), "McpExportTests");

            if (Directory.Exists(this.outputDirectory))
            {
                Directory.Delete(this.outputDirectory, true);
            }
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(MaterialPath);
            AssetDatabase.DeleteAsset(TexturePath);
            AssetDatabase.DeleteAsset(Folder);

            if (Directory.Exists(this.outputDirectory))
            {
                Directory.Delete(this.outputDirectory, true);
            }
        }

        [Test]
        public void WritesAFileAndReportsItsSize()
        {
            var file = Path.Combine(this.outputDirectory, "single.unitypackage");

            var result = AssetTools.ExportPackage(new[] { MaterialPath }, file);

            var written = result["file"].Value<string>();
            FileAssert.Exists(written);
            Assert.That(new FileInfo(written).Length, Is.GreaterThan(0));
            Assert.That(result["bytes"].Value<long>(), Is.EqualTo(new FileInfo(written).Length));
        }

        [Test]
        public void AppendsTheExtensionAndSaysWhereItWent()
        {
            var file = Path.Combine(this.outputDirectory, "no-extension");

            var result = AssetTools.ExportPackage(new[] { MaterialPath }, file);

            Assert.That(result["file"].Value<string>(), Does.EndWith(".unitypackage"));
            FileAssert.Exists(result["file"].Value<string>());
        }

        [Test]
        public void CreatesTheOutputDirectory()
        {
            var file = Path.Combine(this.outputDirectory, "nested", "deeper", "made.unitypackage");

            var result = AssetTools.ExportPackage(new[] { MaterialPath }, file);

            FileAssert.Exists(result["file"].Value<string>());
        }

        [Test]
        public void KeepsAnExistingFileUnlessOverwriteIsSet()
        {
            var file = Path.Combine(this.outputDirectory, "twice.unitypackage");

            AssetTools.ExportPackage(new[] { MaterialPath }, file);
            var first = File.ReadAllBytes(file);

            var refused = Assert.Throws<McpToolException>(
                () => AssetTools.ExportPackage(new[] { MaterialPath }, file));

            Assert.That(refused.Code, Is.EqualTo("already_exists"));
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(first));

            Assert.DoesNotThrow(
                () => AssetTools.ExportPackage(new[] { MaterialPath }, file, overwrite: true));
        }

        [Test]
        public void DependenciesReachTheTextureTheMaterialPointsAt()
        {
            var alone = Path.Combine(this.outputDirectory, "alone.unitypackage");
            var withTexture = Path.Combine(this.outputDirectory, "with-texture.unitypackage");

            AssetTools.ExportPackage(new[] { MaterialPath }, alone);

            var result = AssetTools.ExportPackage(
                new[] { MaterialPath }, withTexture, includeDependencies: true);

            // The count is the closure the flag reached, so it has to name more than the one asset
            // asked for or the reported blast radius means nothing.
            Assert.That(result["includedAssetCount"].Value<int>(), Is.GreaterThan(1));
            Assert.That(
                new FileInfo(withTexture).Length,
                Is.GreaterThan(new FileInfo(alone).Length));
        }

        [Test]
        public void AFolderBringsWhatIsUnderIt()
        {
            var file = Path.Combine(this.outputDirectory, "folder.unitypackage");

            var result = AssetTools.ExportPackage(new[] { Folder }, file);

            FileAssert.Exists(result["file"].Value<string>());
            Assert.That(result["recurse"].Value<bool>(), Is.True);
        }

        [Test]
        public void RefusesAPathThatIsNotInTheProject()
        {
            var file = Path.Combine(this.outputDirectory, "missing.unitypackage");

            var thrown = Assert.Throws<McpToolException>(
                () => AssetTools.ExportPackage(new[] { "Assets/NotThere.mat" }, file));

            Assert.That(thrown.Code, Is.EqualTo("not_found"));
            Assert.That(File.Exists(file), Is.False);
        }

        [Test]
        public void RefusesAnEmptyPathListAndAMissingDestination()
        {
            var noPaths = Assert.Throws<McpToolException>(
                () => AssetTools.ExportPackage(
                    new string[0], Path.Combine(this.outputDirectory, "x.unitypackage")));

            Assert.That(noPaths.Code, Is.EqualTo("invalid_params"));

            var blankPaths = Assert.Throws<McpToolException>(
                () => AssetTools.ExportPackage(
                    new[] { "   " }, Path.Combine(this.outputDirectory, "x.unitypackage")));

            Assert.That(blankPaths.Code, Is.EqualTo("invalid_params"));

            var noFile = Assert.Throws<McpToolException>(
                () => AssetTools.ExportPackage(new[] { MaterialPath }, null));

            Assert.That(noFile.Code, Is.EqualTo("invalid_params"));
        }

        /// <summary>
        /// A destination that names a folder is refused rather than turned into a hidden file.
        /// </summary>
        /// <remarks>
        /// The extension is appended when it is missing, so a trailing slash produced
        /// '&lt;folder&gt;/.unitypackage' — a file with no name, in a place the caller would not
        /// think to look for it.
        /// </remarks>
        [Test]
        public void ADestinationThatNamesAFolderIsRefused()
        {
            var error = Assert.Throws<McpToolException>(
                () => AssetTools.ExportPackage(
                    new[] { MaterialPath }, this.outputDirectory.Replace('\\', '/') + "/"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
            Assert.That(error.Message, Does.Contain("folder"));
            Assert.That(
                File.Exists(Path.Combine(this.outputDirectory, ".unitypackage")),
                Is.False,
                "nothing is written when the destination is refused");
        }

        /// <summary>
        /// A folder named without a trailing separator is refused as well.
        /// </summary>
        /// <remarks>
        /// The extension went onto the folder's own name, so the package landed beside the folder
        /// the caller meant to write into, under a name nobody typed.
        /// </remarks>
        [Test]
        public void AFolderWithoutATrailingSeparatorIsAlsoRefused()
        {
            Directory.CreateDirectory(this.outputDirectory);
            var folder = this.outputDirectory.Replace('\\', '/');

            // The file this refusal prevents sits beside the folder, which the fixture's own
            // clean-up does not reach.
            if (File.Exists(folder + ".unitypackage"))
            {
                File.Delete(folder + ".unitypackage");
            }

            var error = Assert.Throws<McpToolException>(
                () => AssetTools.ExportPackage(new[] { MaterialPath }, folder));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
            Assert.That(error.Message, Does.Contain("folder"));
            Assert.That(File.Exists(folder + ".unitypackage"), Is.False,
                        "nothing is written beside the folder either");
        }
    }
}
