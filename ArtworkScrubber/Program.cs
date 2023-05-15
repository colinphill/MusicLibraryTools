using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Imaging;
using MusicFileUtilities;

namespace ArtworkScrubber
{
    class Program
    {

        private static readonly IReadOnlyDictionary<string, string> mimemapping_ = new Dictionary<string, string>()
        {
            { "image/png", ".png" },
            { "image/bmp", ".bmp" },
            { "image/gif", ".gif" },
            { "image/jpeg", ".jpg" }
        };

        private static IEnumerable<string> SplitCSV(string input)
        {
            Regex csvSplit = new Regex("(?:^|,)(\"(?:[^\"]+|\"\")*\"|[^,]*)", RegexOptions.Compiled);

            foreach (Match match in csvSplit.Matches(input))
            {
                yield return match.Value.TrimStart(',').TrimStart('"').TrimEnd('"');
            }
        }

        const long THRESHOLD = 225 * 1024;
        const int SIZE = 600;

        static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }


        static void Main(string[] args)
        {
            var files = File.ReadAllLines(@"c:\projects\tempresults2.csv").Skip(1).Select(l => Path.Combine(SplitCSV(l).Skip(2).Take(2).ToArray()));

            ImageCodecInfo jpgenc = GetEncoder(ImageFormat.Jpeg);
            EncoderParameters encparms = new EncoderParameters(1);
            encparms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);

            foreach (var file in files)
            {
                var path = Path.GetDirectoryName(file);
                var provider = MediaFile.GetFile(file).Tags.First();
                foreach (var artwork in provider.GetImageMetadata())
                {
                    string extension = mimemapping_[artwork.ImageType];
                    string origimage = Path.Combine(path, provider.Album.FixPath() + extension);
                    File.WriteAllBytes(origimage, artwork.Data);
                    Bitmap im = new Bitmap(origimage);

                    bool resize = false;

                    int newwidth = im.Width, newheight = im.Height;
                    if ((im.Width > SIZE) && (im.Width > im.Height))
                    {
                        newwidth = SIZE;
                        newheight = im.Height * SIZE / im.Width;
                        resize = true;
                    }
                    else if (im.Height > SIZE)
                    {
                        newheight = SIZE;
                        newwidth = im.Width * SIZE / im.Height;
                        resize = true;
                    }

                    if (resize)
                    {
                        Bitmap b = new Bitmap(newwidth, newheight);
                        Graphics g = Graphics.FromImage(b);
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(im, new Rectangle(0, 0, newwidth, newheight));
                        g.Dispose();
                        im.Dispose();
                        im = b;
                    }

                    im.Save(Path.Combine(path, "folder.jpg"), jpgenc, encparms);
                    im.Dispose();



                }
            }
            Console.WriteLine();
        }
    }
}
