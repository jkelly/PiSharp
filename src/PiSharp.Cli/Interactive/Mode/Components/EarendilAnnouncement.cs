// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/earendil-announcement.ts.
// config.ts getBundledInteractiveAssetPath is not ported yet: hosts set EarendilAnnouncementComponent.GetBundledInteractiveAssetPath.
// Without it (or when the file cannot be read) the image is left out, as upstream does when the read fails.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal sealed class EarendilAnnouncementComponent : Container
{
    private const string BlogUrl = "https://mariozechner.at/posts/2026-04-08-ive-sold-out/";
    private const string ImageFilename = "clankolas.png";

    private static string? cachedImageBase64;
    private static bool attemptedImageLoad;

    /// <summary>config.ts getBundledInteractiveAssetPath(name): the path of a bundled interactive asset.</summary>
    public static Func<string, string>? GetBundledInteractiveAssetPath { get; set; }

    /// <summary>Test hook: forget the cached image so the next component reads it again.</summary>
    internal static void ResetImageCacheForTests()
    {
        cachedImageBase64 = null;
        attemptedImageLoad = false;
    }

    private static string? LoadImageBase64()
    {
        if (attemptedImageLoad) return cachedImageBase64;

        attemptedImageLoad = true;
        try
        {
            var resolve = GetBundledInteractiveAssetPath ?? throw new InvalidOperationException("No bundled interactive asset path.");
            cachedImageBase64 = Convert.ToBase64String(File.ReadAllBytes(resolve(ImageFilename)));
        }
        catch
        {
            cachedImageBase64 = null;
        }
        return cachedImageBase64;
    }

    public EarendilAnnouncementComponent()
    {
        AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
        AddChild(new Text(theme.Bold(theme.Fg("accent", "pi has joined Earendil")), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("muted", "Read the blog post:"), 1, 0));
        AddChild(new Text(theme.Fg("mdLink", BlogUrl), 1, 0));
        AddChild(new Spacer(1));

        var imageBase64 = LoadImageBase64();
        if (imageBase64 is not null)
        {
            AddChild(new Image(imageBase64, "image/png", new ImageTheme(text => theme.Fg("muted", text)), new ImageOptions(MaxWidthCells: 56, Filename: ImageFilename)));
            AddChild(new Spacer(1));
        }

        AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
    }
}
