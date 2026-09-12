using System;
using System.Diagnostics;
using System.IO;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A credential file is readable by its owner alone once its contents are in it.
    /// </summary>
    [TestFixture]
    internal sealed class McpInstanceDescriptorTests
    {
        [Test]
        public void ASecretIsReadableByItsOwnerAlone()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                Assert.Ignore("Windows keeps the user profile private to the account, so nothing is restricted there.");
            }

            var path = Path.Combine(Path.GetTempPath(), "mcp-secret-" + Guid.NewGuid().ToString("N"));

            try
            {
                McpInstanceDescriptor.WriteSecret(path, "secret");

                using var ls = Process.Start(new ProcessStartInfo("ls", $"-l \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });

                var listing = ls.StandardOutput.ReadToEnd();
                ls.WaitForExit();

                Assert.That(listing, Does.StartWith("-rw-------"));
                Assert.That(File.ReadAllText(path), Is.EqualTo("secret"));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
