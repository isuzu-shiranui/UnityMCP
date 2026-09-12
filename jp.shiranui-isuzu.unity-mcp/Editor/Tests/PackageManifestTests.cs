using System.IO;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The package.json this package ships with, as the clients that install it read it.
    /// </summary>
    /// <remarks>
    /// The release zip is the package folder archived as it is, so the file checked here is the
    /// one a VPM client unpacks into Packages/. Unity's Package Manager accepts either shape for a
    /// field that VRChat's resolver accepts in only one, and the resolver runs inside the Editor on
    /// every start, after the listing has already passed.
    /// </remarks>
    [TestFixture]
    internal sealed class PackageManifestTests
    {
        private static JObject Manifest()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ToolCatalog).Assembly);

            Assert.That(package, Is.Not.Null, "this assembly is expected to belong to a package");

            var path = Path.Combine(package.resolvedPath, "package.json");

            return JObject.Parse(File.ReadAllText(path));
        }

        /// <summary>
        /// The author is an object with a name, which is the only form a VPM client can read.
        /// </summary>
        /// <remarks>
        /// As a string, VRChat's resolver fails to deserialize the whole manifest on every Editor
        /// start, logs it as fatal, and lists the package as missing, while the package itself
        /// keeps working and nothing else says why.
        /// </remarks>
        [Test]
        public void TheAuthorIsAnObjectWithAName()
        {
            var author = Manifest()["author"];

            Assert.That(author, Is.InstanceOf<JObject>(), "a VPM client cannot read a string author");
            Assert.That(author["name"]?.Value<string>(), Is.Not.Null.And.Not.Empty);
        }
    }
}
