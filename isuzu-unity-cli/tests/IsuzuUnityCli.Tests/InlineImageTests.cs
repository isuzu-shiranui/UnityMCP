using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class InlineImageTests : IDisposable
{
    // A real PNG: the signature matters, because that is what the check reads.
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89,
    ];

    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "inline-image-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private JsonObject Reply() => new()
    {
        ["view"] = "game_view_window",
        ["width"] = 825,
        ["image"] = Convert.ToBase64String(Png),
    };

    [Fact]
    public void APictureIsWrittenToDiskRatherThanPrinted()
    {
        // Printed, the base64 is read as text and costs about ninety times what the picture
        // costs as an image.
        var reply = Reply();

        var path = InlineImage.Externalise(reply, directory);

        Assert.NotNull(path);
        Assert.Null(reply["image"]);
        Assert.Equal(path, (string?)reply["path"]);
        Assert.NotNull(reply["note"]);
        Assert.Equal(Png, File.ReadAllBytes(path!));
    }

    [Fact]
    public void TheRestOfTheReplyIsLeftAlone()
    {
        var reply = Reply();

        InlineImage.Externalise(reply, directory);

        Assert.Equal("game_view_window", (string?)reply["view"]);
        Assert.Equal(825, (int?)reply["width"]);
    }

    [Fact]
    public void AJobDetailCarriesItsPictureOneLevelDown()
    {
        // job_status nests the tool's answer under `result`, and the image sits inside that.
        var reply = new JsonObject { ["state"] = "completed", ["result"] = Reply() };

        var path = InlineImage.Externalise(reply, directory);

        Assert.NotNull(path);
        Assert.Null(reply["result"]!["image"]);
        Assert.NotNull(reply["result"]!["path"]);
    }

    [Fact]
    public void ACaptureTakenAlongsideOtherWorkIsFoundToo()
    {
        // input_replay's then_capture puts the picture under `capture`, beside what the replay
        // itself reports. Looking only at the top and at `result` printed the base64.
        var reply = new JsonObject
        {
            ["sent"] = 2,
            ["window"] = "Game",
            ["capture"] = Reply(),
        };

        var path = InlineImage.Externalise(reply, directory);

        Assert.NotNull(path);
        Assert.Null(reply["capture"]!["image"]);
        Assert.Equal(path, (string?)reply["capture"]!["path"]);
        Assert.Equal(2, (int?)reply["sent"]);
    }

    [Fact]
    public void APictureInsideAnArrayIsFound()
    {
        var reply = new JsonObject { ["captures"] = new JsonArray(Reply()) };

        Assert.NotNull(InlineImage.Externalise(reply, directory));
        Assert.Null(reply["captures"]![0]!["image"]);
    }

    [Fact]
    public void AReplyWithoutAPictureIsUntouched()
    {
        var reply = new JsonObject { ["isPlaying"] = false };

        Assert.Null(InlineImage.Externalise(reply, directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void AStringThatIsNotAPngIsLeftWhereItIs()
    {
        // Only a PNG signature moves. Any other string named `image` belongs to the tool.
        var reply = new JsonObject { ["image"] = "not a picture" };

        Assert.Null(InlineImage.Externalise(reply, directory));
        Assert.Equal("not a picture", (string?)reply["image"]);
    }
}
