using System.Diagnostics.CodeAnalysis;
using TagLib;

namespace DumpTags2
{
    internal class Program
    {
        static void Main(string[] args)
        {
            using var f = TagLib.File.Create(args[0]);
            Console.WriteLine("File Info:");
            var p = f.Properties;
            Console.WriteLine("Duration: " + p.Duration);
            Console.WriteLine("Codec: " + p.Codecs.First().Description);
            Console.WriteLine("Bitrate: " + p.AudioBitrate);
            Console.WriteLine("Samplerate: " + p.AudioSampleRate);
            Console.WriteLine("Channels: " + p.AudioChannels);
            Console.WriteLine("Bitdepth: " + p.BitsPerSample);

            Console.WriteLine();
            Console.WriteLine("Metadata:");
            Console.WriteLine();
            foreach (var kv in f.Tag.GetAllFields())
            {
                Console.Write(kv.Key + ": ");
                foreach (var v in kv.Value)
                    Console.Write(v + "; ");
                Console.WriteLine();
            }
            Console.WriteLine();
            Console.WriteLine("Pictures:");
            Console.WriteLine();

            foreach (var pic in f.Tag.Pictures)
            {
                Console.WriteLine("Description: " + pic.Description);
                var i = pic.AsImage();
                Console.WriteLine("Codec: " + i.Properties.Description);
                Console.WriteLine("Dimensions: " + i.Properties.PhotoWidth + "x" + i.Properties.PhotoHeight);
                Console.WriteLine("Size: " + pic.Data.Count);
                Console.WriteLine();               
            }
        }
    }
}

