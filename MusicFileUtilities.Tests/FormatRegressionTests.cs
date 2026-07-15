using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class FormatRegressionTests
    {
        private static byte[] SyncSafe32(int value) =>
            new[]
            {
                (byte)((value >> 21) & 0x7f),
                (byte)((value >> 14) & 0x7f),
                (byte)((value >> 7) & 0x7f),
                (byte)(value & 0x7f),
            };

        private static void WriteBe32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static void WriteBe64(byte[] bytes, int offset, ulong value)
        {
            WriteBe32(bytes, offset, (uint)(value >> 32));
            WriteBe32(bytes, offset + 4, (uint)value);
        }

        private static uint ReadBe32(byte[] bytes, int offset) =>
            ((uint)bytes[offset] << 24) |
            ((uint)bytes[offset + 1] << 16) |
            ((uint)bytes[offset + 2] << 8) |
            bytes[offset + 3];

        private static ulong ReadBe64(byte[] bytes, int offset) =>
            ((ulong)ReadBe32(bytes, offset) << 32) | ReadBe32(bytes, offset + 4);

        [Fact]
        public void TruncatedRecognizedImagesReturnZeroDimensions()
        {
            byte[] gif = Encoding.ASCII.GetBytes("GIF89a");
            byte[] png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 };
            byte[] bmp = new byte[18];
            Encoding.ASCII.GetBytes("BM").CopyTo(bmp, 0);
            new byte[] { 0x28, 0, 0, 0 }.CopyTo(bmp, 14);

            Assert.Equal((0, 0), ImageFile.GetImageDimensions(gif));
            Assert.Equal((0, 0), ImageFile.GetImageDimensions(png));
            Assert.Equal((0, 0), ImageFile.GetImageDimensions(bmp));
            Assert.Equal(ImageFile.ImageFormat.Unknown, ImageFile.DetectImageFormat(bmp.AsSpan(0, 17)));
        }

        [Fact]
        public void MvhdVersionOneUses32BitTimescaleAnd64BitDuration()
        {
            byte[] atomBytes = new byte[40];
            WriteBe32(atomBytes, 0, (uint)atomBytes.Length);
            Encoding.ASCII.GetBytes("mvhd").CopyTo(atomBytes, 4);
            atomBytes[8] = 1;               // version 1, followed by three flag bytes
            WriteBe32(atomBytes, 28, 1000); // payload offset 20: timescale
            WriteBe64(atomBytes, 32, 5000); // payload offset 24: duration

            using var stream = new MemoryStream(atomBytes);
            var header = new Atom(stream);
            var mvhd = new Atom_mvhd(header, stream);

            Assert.Equal(375u, mvhd.DurationInFrames);
        }

        [Fact]
        public void StcoOverflowIsRejectedWithoutWrappingTheOffset()
        {
            byte[] atomBytes = new byte[20];
            WriteBe32(atomBytes, 0, (uint)atomBytes.Length);
            Encoding.ASCII.GetBytes("stco").CopyTo(atomBytes, 4);
            WriteBe32(atomBytes, 12, 1);              // one chunk offset
            WriteBe32(atomBytes, 16, uint.MaxValue);

            using var input = new MemoryStream(atomBytes);
            var header = new Atom(input);
            var stco = new Atom_stco(header, input);
            Assert.Throws<InvalidOperationException>(() => stco.AdjustOffset(1));

            using var output = new MemoryStream();
            stco.WriteAtom(output);
            Assert.Equal(atomBytes, output.ToArray());
        }

        [Fact]
        public void Co64RoundTripsWithoutWritingAnExtraCount()
        {
            byte[] atomBytes = ChunkOffsetBox("co64", 0x01020304, 17, (ulong)uint.MaxValue + 9);

            using var input = new MemoryStream(atomBytes);
            var header = new Atom(input);
            var co64 = new Atom_co64(header, input);
            using var output = new MemoryStream();
            co64.WriteAtom(output);

            Assert.Equal(atomBytes.Length, output.Length);
            Assert.Equal(atomBytes, output.ToArray());
        }

        [Fact]
        public void FullRewritePromotesStcoAndIncludesItsGrowthInFinalOffsets()
        {
            byte[] file = Box("free", Array.Empty<byte>())
                .Concat(SyntheticMoov(ChunkOffsetBox("stco", 0, uint.MaxValue)))
                .Concat(Box("mdat", new byte[] { 0x5a }))
                .ToArray();
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            var root = new RootAtom(path);
            Assert.IsType<Atom_free>(root.Children[0]).NonRecursiveTouch(1); // initial mdat shift: +1
            long oldMdatOffset = root.Children.Single(atom => atom.Type == "mdat").FileOffset;
            root.WriteFile(path);

            var rewritten = new RootAtom(path);
            var co64 = Assert.IsType<Atom_co64>(
                rewritten.FindPath("moov.trak.mdia.minf.stbl.co64"));
            byte[] serialized = Serialize(co64);
            Assert.Equal(24, co64.Size);
            Assert.Equal(1u, ReadBe32(serialized, 12));
            // +1 from free growth and +4 from widening the one-entry table.
            Assert.Equal((ulong)uint.MaxValue + 5, ReadBe64(serialized, 16));
            Assert.Equal(oldMdatOffset + 5,
                rewritten.Children.Single(atom => atom.Type == "mdat").FileOffset);
        }

        [Fact]
        public void FullRewriteDemotesCo64WhenFinalOffsetsFitStco()
        {
            // The nested container headers make the initial moov 64 bytes, so mdat payload starts
            // at byte 72. Demoting the one-entry table shrinks moov and that offset by four bytes.
            byte[] file = SyntheticMoov(ChunkOffsetBox("co64", 0, 72))
                .Concat(Box("mdat", new byte[] { 0x5a }))
                .ToArray();
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            var root = new RootAtom(path);
            root.WriteFile(path);

            var rewritten = new RootAtom(path);
            var stco = Assert.IsType<Atom_stco>(
                rewritten.FindPath("moov.trak.mdia.minf.stbl.stco"));
            Assert.IsNotType<Atom_co64>(stco);
            byte[] serialized = Serialize(stco);
            Assert.Equal(20, stco.Size);
            Assert.Equal(1u, ReadBe32(serialized, 12));
            Assert.Equal(68u, ReadBe32(serialized, 16));
            Assert.Equal(60, rewritten.Children.Single(atom => atom.Type == "mdat").FileOffset);
        }

        [Fact]
        public void Co64CanDemoteWhenItsOwnShrinkMakesTheOffsetFit()
        {
            byte[] file = SyntheticMoov(
                    ChunkOffsetBox("co64", 0, (ulong)uint.MaxValue + 2))
                .Concat(Box("mdat", new byte[] { 0x5a }))
                .ToArray();
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            new RootAtom(path).WriteFile(path);

            var rewritten = new RootAtom(path);
            var stco = Assert.IsType<Atom_stco>(
                rewritten.FindPath("moov.trak.mdia.minf.stbl.stco"));
            Assert.IsNotType<Atom_co64>(stco);
            Assert.Equal(uint.MaxValue - 2, ReadBe32(Serialize(stco), 16));
        }

        [Fact]
        public void Co64DemotionRepeatsUntilEarlierTablesBecomeEligible()
        {
            byte[] file = SyntheticMoov(
                    ChunkOffsetBox("co64", 0, (ulong)uint.MaxValue + 6),
                    ChunkOffsetBox("co64", 0, 100, 200))
                .Concat(Box("mdat", new byte[] { 0x5a }))
                .ToArray();
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            new RootAtom(path).WriteFile(path);

            var rewritten = new RootAtom(path);
            List<Atom_stco> tables = EnumerateAtoms(rewritten).OfType<Atom_stco>().ToList();
            Assert.Equal(2, tables.Count);
            Assert.All(tables, table => Assert.IsNotType<Atom_co64>(table));
            // The later two-entry table shrinks by eight bytes; revisiting the first table then
            // saves four more, for a final media delta of -12.
            Assert.Equal(uint.MaxValue - 6, ReadBe32(Serialize(tables[0]), 16));
        }

        [Fact]
        public void FullRewriteKeepsCo64WhenAnyFinalOffsetIsTooLarge()
        {
            byte[] file = SyntheticMoov(
                    ChunkOffsetBox("co64", 0, 80, (ulong)uint.MaxValue + 9))
                .Concat(Box("mdat", new byte[] { 0x5a }))
                .ToArray();
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            new RootAtom(path).WriteFile(path);

            var rewritten = new RootAtom(path);
            var co64 = Assert.IsType<Atom_co64>(
                rewritten.FindPath("moov.trak.mdia.minf.stbl.co64"));
            byte[] serialized = Serialize(co64);
            Assert.Equal(2u, ReadBe32(serialized, 12));
            Assert.Equal((ulong)uint.MaxValue + 9, ReadBe64(serialized, 24));
        }

        [Fact]
        public void FailedRewriteRestoresPromotedTableForSafeRetry()
        {
            byte[] file = Box("free", Array.Empty<byte>())
                .Concat(SyntheticMoov(ChunkOffsetBox("stco", 0, uint.MaxValue)))
                .Concat(Box("mdat", new byte[] { 0x5a }))
                .ToArray();
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            var root = new RootAtom(path);
            Assert.IsType<Atom_free>(root.Children[0]).NonRecursiveTouch(1);
            string missingTarget = Path.Combine(
                Path.GetTempPath(), $"mlt_missing_{Guid.NewGuid():N}", "output.m4a");
            Assert.Throws<DirectoryNotFoundException>(() => root.WriteFile(missingTarget));

            var restored = Assert.IsType<Atom_stco>(
                root.FindPath("moov.trak.mdia.minf.stbl.stco"));
            Assert.IsNotType<Atom_co64>(restored);
            Assert.Equal(uint.MaxValue, ReadBe32(Serialize(restored), 16));

            root.WriteFile(path);
            var rewritten = new RootAtom(path);
            var co64 = Assert.IsType<Atom_co64>(
                rewritten.FindPath("moov.trak.mdia.minf.stbl.co64"));
            Assert.Equal((ulong)uint.MaxValue + 5, ReadBe64(Serialize(co64), 16));
        }

        [Fact]
        public void FailedRewriteRestoresDemotedTableForSafeRetry()
        {
            byte[] file = SyntheticMoov(
                    ChunkOffsetBox("co64", 0, (ulong)uint.MaxValue + 2))
                .Concat(Box("mdat", new byte[] { 0x5a }))
                .ToArray();
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            var root = new RootAtom(path);
            string missingTarget = Path.Combine(
                Path.GetTempPath(), $"mlt_missing_{Guid.NewGuid():N}", "output.m4a");
            Assert.Throws<DirectoryNotFoundException>(() => root.WriteFile(missingTarget));

            var restored = Assert.IsType<Atom_co64>(
                root.FindPath("moov.trak.mdia.minf.stbl.co64"));
            Assert.Equal((ulong)uint.MaxValue + 2, ReadBe64(Serialize(restored), 16));

            root.WriteFile(path);
            var rewritten = new RootAtom(path);
            var stco = Assert.IsType<Atom_stco>(
                rewritten.FindPath("moov.trak.mdia.minf.stbl.stco"));
            Assert.IsNotType<Atom_co64>(stco);
            Assert.Equal(uint.MaxValue - 2, ReadBe32(Serialize(stco), 16));
        }

        [Fact]
        public void WavPackCustomSampleRateUsesAllThreeLittleEndianBytes()
        {
            const uint sampleRate = 0x012345;
            byte[] file = new byte[38];
            Encoding.ASCII.GetBytes("wvpk").CopyTo(file, 0);
            BitConverter.GetBytes(30).CopyTo(file, 4);       // ckSize: 24 + six metadata bytes
            BitConverter.GetBytes((short)0x410).CopyTo(file, 8);
            BitConverter.GetBytes(sampleRate).CopyTo(file, 12); // total samples
            BitConverter.GetBytes(sampleRate).CopyTo(file, 20); // block samples
            BitConverter.GetBytes((15u << 23) | (1u << 11) | (1u << 12)).CopyTo(file, 24);
            file[32] = 0x67; // optional custom-rate metadata (0x27), odd byte length
            file[33] = 2;    // two words, three data bytes plus padding
            file[34] = 0x45;
            file[35] = 0x23;
            file[36] = 0x01;

            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.wv");
            File.WriteAllBytes(path, file);
            using var temp = new MediaFixtures.TempMedia(path);

            Assert.Equal(sampleRate, MediaFile.GetFile(path).Codecs.Single().Samplerate);
        }

        [Fact]
        public void FlacMetadataHeaderRejectsLengthsLargerThan24Bits()
        {
            using var stream = new MemoryStream();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FLACFile.WriteMetaBlockHeader(stream, 4, 0x1000000, isLast: false));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void PreviouslyUntaggedFlacCanCreateFirstVorbisComment()
        {
            byte[] stripped = RemoveFlacComment(File.ReadAllBytes(MediaFixtures.Path_("sample.flac")));
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.flac");
            File.WriteAllBytes(path, stripped);
            using var temp = new MediaFixtures.TempMedia(path);

            var flac = (FLACFile)MediaFile.GetFile(path);
            flac.SetField(TagFields.Title, "First Tag");
            flac.Save();

            var reopened = MediaFile.GetFile(path);
            Assert.Equal("First Tag", reopened.Tags.Single().Title);
            Assert.Equal(44100u, reopened.Codecs.Single().Samplerate);
        }

        [Fact]
        public void FlacCommentGrowthConsumesAdjacentPaddingInPlace()
        {
            byte[] padded = AddFlacPaddingAfterComment(
                File.ReadAllBytes(MediaFixtures.Path_("sample.flac")), 4096);
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.flac");
            File.WriteAllBytes(path, padded);
            using var temp = new MediaFixtures.TempMedia(path);
            long originalLength = new FileInfo(path).Length;

            var flac = (FLACFile)MediaFile.GetFile(path);
            string title = new string('T', 2048);
            flac.SetField(TagFields.Title, title);
            flac.Save();

            Assert.True(flac.LastSaveWasInPlace);
            Assert.Equal(originalLength, new FileInfo(path).Length);
            Assert.Equal(title, MediaFile.GetFile(path).Tags.Single().Title);

            // The parser keeps only the padding length, so also exercise a later full rewrite
            // and verify that regenerated zero padding still yields a valid FLAC.
            string rewrittenPath = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.flac");
            using var rewritten = new MediaFixtures.TempMedia(rewrittenPath);
            flac.Save(rewrittenPath);
            Assert.False(flac.LastSaveWasInPlace);
            Assert.Equal(title, MediaFile.GetFile(rewrittenPath).Tags.Single().Title);
            Assert.Equal(44100u, MediaFile.GetFile(rewrittenPath).Codecs.Single().Samplerate);
        }

        [Fact]
        public void FlacConsecutiveFullRewritesUseUpdatedAudioOffset()
        {
            using var source = MediaFixtures.Copy("sample.flac");
            string firstPath = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.flac");
            string secondPath = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.flac");
            using var first = new MediaFixtures.TempMedia(firstPath);
            using var second = new MediaFixtures.TempMedia(secondPath);
            var flac = (FLACFile)MediaFile.GetFile(source.Path);
            flac.SetField(TagFields.Title, "First full rewrite");

            flac.Save(firstPath);
            flac.SetField(TagFields.Title, "Second full rewrite");
            flac.Save(secondPath);

            var reopened = MediaFile.GetFile(secondPath);
            Assert.Equal("Second full rewrite", reopened.Tags.Single().Title);
            Assert.Equal(44_100u, reopened.Codecs.Single().Samplerate);
        }

        [Fact]
        public void UntaggedMp3SaveCreatesId3v23AndPreservesAudio()
        {
            byte[] tagged = File.ReadAllBytes(MediaFixtures.Path_("sample.mp3"));
            Assert.Equal("ID3", Encoding.ASCII.GetString(tagged, 0, 3));
            int oldTagSize = (tagged[6] << 21) | (tagged[7] << 14) | (tagged[8] << 7) | tagged[9];
            byte[] audio = tagged[(10 + oldTagSize)..];

            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, audio);
            using var temp = new MediaFixtures.TempMedia(path);

            var mp3 = (MP3File)MediaFile.GetFile(path);
            mp3.SetField(TagFields.Title, "Created Tag");
            mp3.Save();

            byte[] saved = File.ReadAllBytes(path);
            Assert.Equal("ID3", Encoding.ASCII.GetString(saved, 0, 3));
            Assert.Equal(3, saved[3]);
            Assert.Equal("Created Tag", MediaFile.GetFile(path).Tags.Single().Title);
            Assert.Equal(44100u, MediaFile.GetFile(path).Codecs.Single().Samplerate);
        }

        [Fact]
        public void Id3v24DecodedDliAndUnsyncFlagsAreNotWrittenBack()
        {
            byte[] decodedPayload = new byte[] { 0, (byte)'A', 0xff, 0xe1 };
            byte[] storedPayload = new byte[] { 0, 0, 0, 4, 0, (byte)'A', 0xff, 0, 0xe1 };
            var body = new List<byte>();
            body.AddRange(Encoding.ASCII.GetBytes("TIT2"));
            body.AddRange(SyncSafe32(storedPayload.Length));
            body.AddRange(new byte[] { 0, 3 }); // DLI + per-frame unsynchronization
            body.AddRange(storedPayload);

            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("ID3"));
            file.AddRange(new byte[] { 4, 0, 0 });
            file.AddRange(SyncSafe32(body.Count));
            file.AddRange(body);

            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, file.ToArray());
            using var temp = new MediaFixtures.TempMedia(path);

            var mp3 = (MP3File)MediaFile.GetFile(path);
            string title = Encoding.GetEncoding(28591).GetString(decodedPayload, 1, decodedPayload.Length - 1);
            Assert.Equal(title, mp3.Title);
            mp3.Save();

            byte[] saved = File.ReadAllBytes(path);
            Assert.Equal(0, saved[18]);
            Assert.Equal(0, saved[19]);
            Assert.Equal(4, (saved[14] << 21) | (saved[15] << 14) | (saved[16] << 7) | saved[17]);
            Assert.Equal(title, MediaFile.GetFile(path).Tags.Single().Title);
        }

        private static byte[] RemoveFlacComment(byte[] source)
        {
            Assert.Equal("fLaC", Encoding.ASCII.GetString(source, 0, 4));
            var blocks = new List<(byte Type, byte[] Data)>();
            int offset = 4;
            bool last;
            do
            {
                byte header = source[offset++];
                int length = (source[offset] << 16) | (source[offset + 1] << 8) | source[offset + 2];
                offset += 3;
                byte type = (byte)(header & 0x7f);
                byte[] data = source.AsSpan(offset, length).ToArray();
                offset += length;
                last = (header & 0x80) != 0;
                if (type != 4)
                    blocks.Add((type, data));
            }
            while (!last);

            using var output = new MemoryStream();
            output.Write("fLaC"u8);
            for (int i = 0; i < blocks.Count; i++)
            {
                FLACFile.WriteMetaBlockHeader(output, blocks[i].Type, blocks[i].Data.Length, i == blocks.Count - 1);
                output.Write(blocks[i].Data);
            }
            output.Write(source, offset, source.Length - offset);
            return output.ToArray();
        }

        private static byte[] AddFlacPaddingAfterComment(byte[] source, int paddingLength)
        {
            Assert.Equal("fLaC", Encoding.ASCII.GetString(source, 0, 4));
            var blocks = new List<(byte Type, byte[] Data)>();
            int offset = 4;
            bool last;
            do
            {
                byte header = source[offset++];
                int length = (source[offset] << 16) | (source[offset + 1] << 8) | source[offset + 2];
                offset += 3;
                byte type = (byte)(header & 0x7f);
                blocks.Add((type, source.AsSpan(offset, length).ToArray()));
                offset += length;
                last = (header & 0x80) != 0;
            }
            while (!last);

            int commentIndex = blocks.FindIndex(b => b.Type == 4);
            Assert.True(commentIndex >= 0);
            blocks.Insert(commentIndex + 1, (1, new byte[paddingLength]));

            using var output = new MemoryStream(source.Length + paddingLength + 4);
            output.Write("fLaC"u8);
            for (int i = 0; i < blocks.Count; i++)
            {
                FLACFile.WriteMetaBlockHeader(output, blocks[i].Type, blocks[i].Data.Length, i == blocks.Count - 1);
                output.Write(blocks[i].Data);
            }
            output.Write(source, offset, source.Length - offset);
            return output.ToArray();
        }

        private static byte[] ChunkOffsetBox(string type, uint versionAndFlags, params ulong[] offsets)
        {
            int entryWidth = type == "co64" ? 8 : 4;
            byte[] payload = new byte[8 + offsets.Length * entryWidth];
            WriteBe32(payload, 0, versionAndFlags);
            WriteBe32(payload, 4, (uint)offsets.Length);
            for (int i = 0; i < offsets.Length; i++)
            {
                if (entryWidth == 8)
                    WriteBe64(payload, 8 + i * entryWidth, offsets[i]);
                else
                    WriteBe32(payload, 8 + i * entryWidth, checked((uint)offsets[i]));
            }
            return Box(type, payload);
        }

        private static byte[] SyntheticMoov(params byte[][] chunkOffsetTables)
        {
            byte[] tracks = chunkOffsetTables
                .SelectMany(chunkOffsets =>
                    Box("trak", Box("mdia", Box("minf", Box("stbl", chunkOffsets)))))
                .ToArray();
            return Box("moov", tracks);
        }

        private static byte[] Box(string type, byte[] payload)
        {
            byte[] result = new byte[8 + payload.Length];
            WriteBe32(result, 0, (uint)result.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
            payload.CopyTo(result, 8);
            return result;
        }

        private static byte[] Serialize(Atom atom)
        {
            using var stream = new MemoryStream();
            atom.WriteAtom(stream);
            return stream.ToArray();
        }

        private static IEnumerable<Atom> EnumerateAtoms(ContainerAtom container)
        {
            foreach (Atom atom in container.Children)
            {
                yield return atom;
                if (atom is ContainerAtom child)
                    foreach (Atom nested in EnumerateAtoms(child))
                        yield return nested;
            }
        }
    }
}
