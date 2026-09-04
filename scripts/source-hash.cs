#!/usr/bin/env dotnet
// Identifies the Editor sources, so a recorded test run can be matched to the code it ran
// against. Used by both scripts/run-editmode-tests.ps1 and the workflows: two implementations
// of "which sources are these" would disagree the first time one of them was changed, and the
// check would either block a good release or wave a bad one through.
//
//   dotnet run scripts/source-hash.cs                    # this repository
//   dotnet run scripts/source-hash.cs -- <repoRoot>      # a checkout somewhere else
//
// `dotnet run` launches a file-based app with the working directory set to the directory
// holding the .cs file, not the one the command was typed in, so a relative repository root
// passed on the command line is resolved against scripts/. Pass an absolute path, or pass
// nothing and let the search below find the repository from the script's own location.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

const string Package = "jp.shiranui-isuzu.unity-mcp";

var root = args.Length > 0 ? args[0] : RepositoryRoot();
var packageRoot = Path.Combine(root, Package);

if (!Directory.Exists(packageRoot))
{
    Console.Error.WriteLine($"No package directory at '{packageRoot}'.");
    return 1;
}

var sources = new List<(string Relative, string Full)>();
Collect(new DirectoryInfo(packageRoot));

// Ordinal, on the forward-slash relative path rather than the native one. Sorting native paths
// puts "Core/X.cs" and "CoreThing.cs" in opposite orders on Windows and Linux, because '/' sorts
// below letters and '\' sorts above them; the hash would then depend on which machine computed
// it, and an attestation written on a workstation could not be verified on a runner.
sources.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
var separator = new byte[] { 0 };

foreach (var (relative, full) in sources)
{
    // The path goes in too: moving a file changes what compiles even when no line does.
    hash.AppendData(Encoding.UTF8.GetBytes(relative));
    hash.AppendData(separator);
    hash.AppendData(WithoutCarriageReturns(File.ReadAllBytes(full)));
    hash.AppendData(separator);
}

Console.WriteLine(Convert.ToHexStringLower(hash.GetHashAndReset()));
return 0;

void Collect(DirectoryInfo directory)
{
    foreach (var entry in directory.EnumerateFileSystemInfos())
    {
        // A junction or symlink would be walked twice, once through each name it has.
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

        if (entry is DirectoryInfo child)
        {
            Collect(child);
        }
        else if (entry.Name.EndsWith(".cs", StringComparison.Ordinal) ||
                 entry.Name.EndsWith(".asmdef", StringComparison.Ordinal))
        {
            sources.Add((Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'), entry.FullName));
        }
    }
}

// Walking up rather than taking the script's parent directory outright: the compiler records
// whatever path it was handed, which is absolute for some invocations and relative to the
// working directory for others, and the working directory here is scripts/. The search reaches
// the repository from either form.
string RepositoryRoot([CallerFilePath] string scriptPath = "")
{
    for (var dir = new FileInfo(Path.GetFullPath(scriptPath)).Directory; dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, Package))) return dir.FullName;
    }

    return ".";
}

// Line endings are stripped before hashing. The repository stores LF, a Windows checkout has
// CRLF, and a Linux runner has LF, so hashing the bytes as they sit on disk would make the
// attestation fail on every platform but the one that wrote it.
//
// Done on the bytes rather than on decoded text: a UTF-8 byte order mark has to survive into
// the hash, and File.ReadAllText removes it. 0x0D cannot occur inside a multi-byte UTF-8
// sequence, so dropping the byte and dropping the character are the same edit.
static byte[] WithoutCarriageReturns(byte[] content)
{
    var kept = new byte[content.Length];
    var length = 0;

    foreach (var b in content)
    {
        if (b != 0x0D) kept[length++] = b;
    }

    return length == content.Length ? content : kept[..length];
}
