using TagLib;

namespace DumpTags2;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: DumpTags2 <media-file>");
            return 2;
        }

        try
        {
            using var file = TagLib.File.Create(args[0]);
            Console.WriteLine("File Info:");
            var properties = file.Properties;
            Console.WriteLine("Duration: " + properties.Duration);
            Console.WriteLine("Codec: " + properties.Codecs.First().Description);
            Console.WriteLine("Bitrate: " + properties.AudioBitrate);
            Console.WriteLine("Samplerate: " + properties.AudioSampleRate);
            Console.WriteLine("Channels: " + properties.AudioChannels);
            Console.WriteLine("Bitdepth: " + properties.BitsPerSample);

            Console.WriteLine();
            Console.WriteLine("Metadata:");
            Console.WriteLine();
            foreach (var field in file.Tag.GetAllFields())
            {
                Console.Write(field.Key + ": ");
                foreach (var value in field.Value)
                    Console.Write(value + "; ");
                Console.WriteLine();
            }

            Console.WriteLine();
            Console.WriteLine("Pictures:");
            Console.WriteLine();
            foreach (var picture in file.Tag.Pictures)
            {
                Console.WriteLine("Description: " + picture.Description);
                var image = picture.AsImage();
                Console.WriteLine("Codec: " + image.Properties.Description);
                Console.WriteLine("Dimensions: " + image.Properties.PhotoWidth + "x" + image.Properties.PhotoHeight);
                Console.WriteLine("Size: " + picture.Data.Count);
                Console.WriteLine();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unable to read '{args[0]}': {ex.Message}");
            return 1;
        }
    }
}
