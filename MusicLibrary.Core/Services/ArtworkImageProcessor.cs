using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace MusicLibrary.Core.Services;

/// <summary>Shared image transforms for every artwork-optimization workflow.</summary>
internal static class ArtworkImageProcessor
{
    /// <summary>
    /// Shrinks an image to fit inside a square bounding box without cropping, stretching, or
    /// enlarging it. <see cref="ResizeMode.Max"/> derives the unconstrained dimension from the
    /// source aspect ratio, so landscape and portrait artwork retain their original proportions.
    /// </summary>
    public static void ResizeToFit(Image image, int maximumDimension)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (maximumDimension <= 0 ||
            (image.Width <= maximumDimension && image.Height <= maximumDimension))
            return;

        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(maximumDimension, maximumDimension),
        }));
    }
}
