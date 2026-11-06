using DeskBox.Features.EyeRest;

namespace DeskBox.Tests;

/// <summary>
/// Pure-logic coverage for the break overlay background resolution: every
/// invalid input must degrade to the solid default, because a screen-covering
/// break must never fail to start over a missing wallpaper.
/// </summary>
public sealed class EyeRestBreakBackgroundFactoryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eyerest-bg-" + Path.GetRandomFileName());

    private string Touch(string relativeName)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, relativeName);
        File.WriteAllText(path, "stub");
        return path;
    }

    [Fact]
    public void NormalizeMode_UnknownFallsBackToSolid()
    {
        Assert.Equal(EyeRestBreakBackgroundFactory.ModeSolid,
            EyeRestBreakBackgroundFactory.NormalizeMode("Fancy"));
        Assert.Equal(EyeRestBreakBackgroundFactory.ModeSolid,
            EyeRestBreakBackgroundFactory.NormalizeMode(null));
        Assert.Equal(EyeRestBreakBackgroundFactory.ModeImage,
            EyeRestBreakBackgroundFactory.NormalizeMode("Image"));
        Assert.Equal(EyeRestBreakBackgroundFactory.ModeFolder,
            EyeRestBreakBackgroundFactory.NormalizeMode("Folder"));
    }

    [Theory]
    [InlineData("#161618", "#161618")]
    [InlineData("#FF161618", "#161618")] // #AARRGGBB keeps the RGB tail
    public void NormalizeColorHex_ValidCanonicalizes(string input, string expected)
    {
        Assert.Equal(expected, EyeRestBreakBackgroundFactory.NormalizeColorHex(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("red")]
    [InlineData("#12345")]        // too short
    [InlineData("#12345G")]       // not hex
    [InlineData(null)]
    public void NormalizeColorHex_InvalidFallsBackToDefault(string? input)
    {
        Assert.Equal(
            EyeRestBreakBackground.DefaultColorHex,
            EyeRestBreakBackgroundFactory.NormalizeColorHex(input));
    }

    [Fact]
    public void Resolve_SolidMode_UsesNormalizedColorWithoutPathChecks()
    {
        EyeRestBreakBackground result = EyeRestBreakBackgroundFactory.Resolve(
            EyeRestBreakBackgroundFactory.ModeSolid,
            "#204060",
            imagePath: "Z:\\does\\not\\exist.png",
            folderPath: "Z:\\missing");
        Assert.False(result.IsImage);
        Assert.Equal("#204060", result.ColorHex);
        Assert.Null(result.ImagePath);
    }

    [Fact]
    public void Resolve_ImageMode_ExistingImageWins()
    {
        string image = Touch("wall.png");
        EyeRestBreakBackground result = EyeRestBreakBackgroundFactory.Resolve(
            EyeRestBreakBackgroundFactory.ModeImage,
            null,
            image,
            folderPath: null);
        Assert.True(result.IsImage);
        Assert.Equal(image, result.ImagePath);
        Assert.Equal(EyeRestBreakBackground.DefaultColorHex, result.ColorHex);
    }

    [Fact]
    public void Resolve_ImageMode_MissingOrUnsupportedFileFallsBackToSolid()
    {
        string text = Touch("notes.txt");
        Assert.False(EyeRestBreakBackgroundFactory.Resolve(
            EyeRestBreakBackgroundFactory.ModeImage, null,
            Path.Combine(_root, "gone.png"), null).IsImage);
        Assert.False(EyeRestBreakBackgroundFactory.Resolve(
            EyeRestBreakBackgroundFactory.ModeImage, null, text, null).IsImage);
    }

    [Fact]
    public void Resolve_FolderMode_PicksOneOfTheFolderImages()
    {
        string[] images =
        [
            Touch("a.jpg"),
            Touch("b.jpeg"),
            Touch("c.webp"),
            Touch("ignored.txt"),
            Touch("ignored.docx")
        ];
        string imageFolder = Path.GetDirectoryName(images[0])!;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            EyeRestBreakBackground result = EyeRestBreakBackgroundFactory.Resolve(
                EyeRestBreakBackgroundFactory.ModeFolder,
                null,
                imagePath: null,
                folderPath: imageFolder);
            Assert.True(result.IsImage);
            Assert.Contains(result.ImagePath, images.Take(3));
        }
    }

    [Fact]
    public void Resolve_FolderMode_MissingOrEmptyFolderFallsBackToSolid()
    {
        Assert.False(EyeRestBreakBackgroundFactory.Resolve(
            EyeRestBreakBackgroundFactory.ModeFolder, null, null,
            Path.Combine(_root, "missing")).IsImage);

        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Assert.False(EyeRestBreakBackgroundFactory.Resolve(
            EyeRestBreakBackgroundFactory.ModeFolder, null, null, empty).IsImage);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
