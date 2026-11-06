namespace DeskBox.Features.EyeRest;

/// <summary>
/// The break overlay background, resolved from the eye-rest background
/// preferences for one break: either a solid color or one image file. A
/// "Folder" mode resolves to a random image from the folder per break, so
/// <see cref="Resolve"/> is called once per break, not cached.
/// </summary>
public sealed record EyeRestBreakBackground(
    bool IsImage,
    string ColorHex,
    string? ImagePath)
{
    /// <summary>The built-in dark overlay color (matches the pre-background feature look).</summary>
    public const string DefaultColorHex = "#161618";

    /// <summary>The resolved default: solid built-in dark.</summary>
    public static EyeRestBreakBackground Default { get; } =
        new(IsImage: false, DefaultColorHex, ImagePath: null);
}

/// <summary>
/// Normalizes and resolves the raw background preference strings into an
/// <see cref="EyeRestBreakBackground"/>. Every invalid input degrades to the
/// solid default instead of failing a break: a screen-covering break whose
/// background failed to resolve must still start.
/// </summary>
public static class EyeRestBreakBackgroundFactory
{
    /// <summary>Wire value for the solid color mode.</summary>
    public const string ModeSolid = "Solid";

    /// <summary>Wire value for the single-image mode.</summary>
    public const string ModeImage = "Image";

    /// <summary>Wire value for the random-image-from-folder mode.</summary>
    public const string ModeFolder = "Folder";

    private static readonly string[] AllowedImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];

    /// <summary>Maps an unknown wire mode to the solid mode.</summary>
    public static string NormalizeMode(string? mode) =>
        mode is ModeImage or ModeFolder or ModeSolid ? mode : ModeSolid;

    /// <summary>
    /// Validates a #RRGGBB (or #AARRGGBB) color; invalid input returns the
    /// built-in default hex. The returned string is always canonical
    /// #RRGGBB form so the overlay can parse it without branching.
    /// </summary>
    public static string NormalizeColorHex(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            string trimmed = hex.Trim();
            if (trimmed.Length == 7)
            {
                bool valid = trimmed[0] == '#';
                for (int i = 1; valid && i < 7; i++)
                {
                    valid = Uri.IsHexDigit(trimmed[i]);
                }

                if (valid)
                {
                    return trimmed;
                }
            }
            // #AARRGGBB: keep the RGB tail; the overlay alpha is not configurable.
            else if (trimmed.Length == 9 &&
                trimmed[0] == '#' &&
                IsHexString(trimmed[1..]))
            {
                return "#" + trimmed[3..];
            }
        }

        return EyeRestBreakBackground.DefaultColorHex;
    }

    /// <summary>
    /// Resolves one break's background. Image and folder modes validate the
    /// path (existence plus extension) and fall back to the solid color when
    /// the target is gone — a deleted wallpaper must not break the break.
    /// The Features layer stays host-free, so diagnostics go through the
    /// optional <paramref name="log"/> sink the Services caller wires up.
    /// </summary>
    public static EyeRestBreakBackground Resolve(
        string? mode,
        string? colorHex,
        string? imagePath,
        string? folderPath,
        Action<string>? log = null)
    {
        string normalizedMode = NormalizeMode(mode);
        string normalizedColor = NormalizeColorHex(colorHex);

        if (normalizedMode is ModeImage or ModeFolder)
        {
            string? candidate = normalizedMode == ModeImage
                ? ValidateImagePath(imagePath)
                : PickRandomFolderImage(folderPath, log);
            if (candidate is not null)
            {
                return new EyeRestBreakBackground(IsImage: true, normalizedColor, candidate);
            }
        }

        return new EyeRestBreakBackground(IsImage: false, normalizedColor, ImagePath: null);
    }

    private static string? ValidateImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        string extension = Path.GetExtension(imagePath).ToLowerInvariant();
        return AllowedImageExtensions.Contains(extension) ? imagePath : null;
    }

    private static string? PickRandomFolderImage(string? folderPath, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        try
        {
            string[] images = Directory.EnumerateFiles(folderPath)
                .Where(path => AllowedImageExtensions.Contains(
                    Path.GetExtension(path).ToLowerInvariant()))
                .ToArray();
            if (images.Length == 0)
            {
                return null;
            }

            return images[Random.Shared.Next(images.Length)];
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder degrades to the solid background; the break
            // schedule itself must not be disturbed.
            log?.Invoke($"[EyeRest] Background folder scan failed for '{folderPath}': {ex.Message}");
            return null;
        }
    }

    private static bool IsHexString(string value)
    {
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
