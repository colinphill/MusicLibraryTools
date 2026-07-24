using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class Id3VersionConversionTests
{
    public static TheoryData<ID3v2Version, ID3v2Version> ConversionDirections => new()
    {
        { ID3v2Version.V22, ID3v2Version.V23 },
        { ID3v2Version.V22, ID3v2Version.V24 },
        { ID3v2Version.V23, ID3v2Version.V22 },
        { ID3v2Version.V23, ID3v2Version.V24 },
        { ID3v2Version.V24, ID3v2Version.V22 },
        { ID3v2Version.V24, ID3v2Version.V23 },
    };

    [Fact]
    public void NewTagsDefaultToV23()
    {
        var tag = new ID3v2Tag();

        Assert.Equal(3, tag.Version);
        Assert.Equal("ID3v23", tag.TagType);
    }

    [Fact]
    public void ConvertingToCurrentVersionIsANoOp()
    {
        var tag = new ID3v2Tag();
        tag.SetField(TagFields.Title, "Unchanged");
        ID3v2Frame original = Assert.Single(tag.Frames);

        ID3VersionConversionResult result = tag.ChangeVersion(ID3v2Version.V23);

        Assert.Equal(0, result.ConvertedFrameCount);
        Assert.Empty(result.Issues);
        Assert.Same(original, Assert.Single(tag.Frames));
    }

    [Theory]
    [MemberData(nameof(ConversionDirections))]
    public void StandardUserDateIdentifierCommentAndPictureFramesConvert(
        ID3v2Version source,
        ID3v2Version target)
    {
        ID3v2Tag tag = CreatePopulatedTag(source);

        ID3VersionConversionResult result = tag.ChangeVersion(target);

        Assert.Equal(source, result.SourceVersion);
        Assert.Equal(target, result.TargetVersion);
        Assert.Empty(result.Issues);
        Assert.Equal((int)target, tag.Version);
        Assert.All(tag.Frames, frame =>
            Assert.Equal(target == ID3v2Version.V22 ? 3 : 4, frame.FrameID.Length));

        var known = tag.GetKnownMetadata().ToLookup(item => item.Key, item => item.Value);
        Assert.Contains("Converted Title", known[TagFields.Title]);
        Assert.Contains("Converted Artist", known[TagFields.Artist]);
        Assert.Contains("2024-05-06", known[TagFields.Date]);
        Assert.Contains("1999", known[TagFields.OriginalDate]);
        Assert.Contains("artist-id", known[TagFields.MusicBrainz_ArtistID]);
        Assert.Contains("converted lyrics", known[TagFields.Lyrics]);

        IdentifierFrame identifier = Assert.Single(tag.Frames.OfType<IdentifierFrame>());
        Assert.Equal(target == ID3v2Version.V22 ? "UFI" : "UFID", identifier.FrameID);
        Assert.Equal("owner", identifier.Key);
        Assert.Equal([1, 2, 3, 4], identifier.Value);

        CommentFrame comment = Assert.Single(tag.Frames.OfType<CommentFrame>(),
            frame => frame.FrameID == (target == ID3v2Version.V22 ? "COM" : "COMM"));
        Assert.Equal(target == ID3v2Version.V22 ? "COM" : "COMM", comment.FrameID);
        Assert.Equal("eng", comment.Language);
        Assert.Equal("note", comment.Key);
        Assert.Equal("comment value", comment.Value);
        CommentFrame lyrics = Assert.Single(tag.Frames.OfType<CommentFrame>(),
            frame => frame.FrameID == (target == ID3v2Version.V22 ? "ULT" : "USLT"));
        Assert.Equal("converted lyrics", lyrics.Value);

        PictureFrame picture = Assert.Single(tag.Frames.OfType<PictureFrame>());
        Assert.Equal(target == ID3v2Version.V22 ? "PIC" : "APIC", picture.FrameID);
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal([9, 8, 7, 6], picture.PictureData);
    }

    [Theory]
    [MemberData(nameof(ConversionDirections))]
    public void SerializedMp3ConvertsInEveryDirection(
        ID3v2Version sourceVersion,
        ID3v2Version targetVersion)
    {
        string path = CreateUntaggedMp3();
        using var temp = new MediaFixtures.TempMedia(path);
        byte[] audio = File.ReadAllBytes(path);

        var source = (MP3File)MediaFile.GetFile(path);
        source.ChangeVersion(sourceVersion);
        source.SetField(TagFields.Title, "Serialized Title");
        source.SetField(TagFields.Artist, "Serialized Artist");
        source.SetField(TagFields.Date, "2024-05-06");
        source.SetField(TagFields.OriginalDate, "1999");
        source.SetField(TagFields.Lyrics, "Serialized lyrics");
        source.SetField(TagFields.MusicBrainz_ArtistID, "serialized-id");
        source.SetAttachedImage(
            ID3v2Util.APICType.FrontCover, "image/png", "cover", [4, 3, 2, 1]);
        source.Save();

        var parsedSource = (MP3File)MediaFile.GetFile(path);
        Assert.Equal((int)sourceVersion, parsedSource.Version);
        parsedSource.ChangeVersion(targetVersion);
        parsedSource.Save();

        var reopened = (MP3File)MediaFile.GetFile(path);
        Assert.Equal((int)targetVersion, reopened.Version);
        Assert.Equal("Serialized Title", reopened.Title);
        Assert.Equal("Serialized Artist", reopened.Artist);
        Assert.Equal("2024-05-06", reopened.ReleaseDate);
        var known = reopened.GetKnownMetadata().ToLookup(item => item.Key, item => item.Value);
        Assert.Contains("1999", known[TagFields.OriginalDate]);
        Assert.Contains("Serialized lyrics", known[TagFields.Lyrics]);
        Assert.Contains("serialized-id", known[TagFields.MusicBrainz_ArtistID]);
        Assert.Equal([4, 3, 2, 1], Assert.Single(reopened.GetImageMetadata()).Data);
        Assert.Equal(audio, StripLeadingId3(File.ReadAllBytes(path)));
    }

    [Fact]
    public void UnsupportedFrameFailsAtomicallyByDefault()
    {
        var tag = new ID3v2Tag();
        tag.ChangeVersion(ID3v2Version.V24);
        tag.SetField(TagFields.Title, "Still Here");
        tag.Frames.Add(new ID3v2Frame(tag)
        {
            FrameID = "SIGN",
            Data = [1, 2, 3],
        });
        string[] originalIds = tag.Frames.Select(frame => frame.FrameID).ToArray();

        ID3VersionConversionException error = Assert.Throws<ID3VersionConversionException>(
            () => tag.ChangeVersion(ID3v2Version.V23));

        Assert.Contains(error.Issues, issue => issue.FrameID == "SIGN");
        Assert.Equal(4, tag.Version);
        Assert.Equal(originalIds, tag.Frames.Select(frame => frame.FrameID));
        Assert.Contains(tag.GetKnownMetadata(),
            item => item.Key == TagFields.Title && item.Value == "Still Here");
    }

    [Fact]
    public void UnsupportedFrameCanBeDroppedExplicitly()
    {
        var tag = new ID3v2Tag();
        tag.ChangeVersion(ID3v2Version.V24);
        tag.SetField(TagFields.Title, "Kept");
        tag.Frames.Add(new ID3v2Frame(tag)
        {
            FrameID = "SIGN",
            Data = [1],
        });

        ID3VersionConversionResult result = tag.ChangeVersion(
            ID3v2Version.V23,
            new ID3VersionConversionOptions { DropUnsupportedFrames = true });

        Assert.Equal(3, tag.Version);
        Assert.DoesNotContain(tag.Frames, frame => frame.FrameID == "SIGN");
        Assert.Contains(result.Issues, issue => issue.FrameID == "SIGN" && issue.Dropped);
        Assert.Contains(tag.GetKnownMetadata(),
            item => item.Key == TagFields.Title && item.Value == "Kept");
    }

    [Fact]
    public void EncodedFramePayloadFailsAtomicallyOrCanBeDropped()
    {
        var tag = new ID3v2Tag();
        tag.SetField(TagFields.Title, "Encoded");
        ID3v2Frame encoded = Assert.Single(tag.Frames);
        encoded.Flags = 0x0080;

        Assert.Throws<ID3VersionConversionException>(
            () => tag.ChangeVersion(ID3v2Version.V24));
        Assert.Equal(3, tag.Version);
        Assert.Same(encoded, Assert.Single(tag.Frames));

        ID3VersionConversionResult result = tag.ChangeVersion(
            ID3v2Version.V24,
            new ID3VersionConversionOptions { DropUnsupportedFrames = true });

        Assert.Equal(4, tag.Version);
        Assert.Empty(tag.Frames);
        Assert.Contains(result.Issues,
            issue => issue.FrameID == "TIT2" && issue.Dropped);
    }

    [Fact]
    public void V24MultipleTextValuesRequireExplicitCoalescingWhenDowngrading()
    {
        var tag = new ID3v2Tag();
        tag.ChangeVersion(ID3v2Version.V24);
        var artists = new TextFrame(tag) { FrameID = "TPE1" };
        artists.Values = ["First", "Second"];
        tag.Frames.Add(artists);

        Assert.Throws<ID3VersionConversionException>(
            () => tag.ChangeVersion(ID3v2Version.V23));
        Assert.Equal(4, tag.Version);

        ID3VersionConversionResult result = tag.ChangeVersion(
            ID3v2Version.V23,
            new ID3VersionConversionOptions
            {
                CoalesceTextValues = true,
                MultiValueSeparator = " / ",
            });

        Assert.Contains(tag.GetKnownMetadata(),
            item => item.Key == TagFields.Artist && item.Value == "First / Second");
        Assert.Contains(result.Issues, issue => issue.FrameID == "TPE1");
    }

    [Fact]
    public void DowngradeRejectsTimestampPrecisionThatCannotBeRepresented()
    {
        var tag = new ID3v2Tag();
        tag.ChangeVersion(ID3v2Version.V24);
        tag.SetField(TagFields.Date, "2024-05-06T12:34:56Z");

        ID3VersionConversionException error = Assert.Throws<ID3VersionConversionException>(
            () => tag.ChangeVersion(ID3v2Version.V23));

        Assert.Contains(error.Issues, issue => issue.FrameID == "TDRC");
        Assert.Equal(4, tag.Version);
        Assert.Contains(tag.GetKnownMetadata(),
            item => item.Key == TagFields.Date && item.Value == "2024-05-06T12:34:56Z");
    }

    [Fact]
    public void InvalidLegacyDateDoesNotEraseExistingDate()
    {
        var tag = new ID3v2Tag();
        tag.SetField(TagFields.Date, "2024-05-06");
        tag.SetField(TagFields.OriginalDate, "1999");

        Assert.Throws<ArgumentException>(
            () => tag.SetField(TagFields.Date, "2024-99-99"));
        Assert.Throws<ArgumentException>(
            () => tag.SetField(TagFields.OriginalDate, "1999-01-01"));

        var known = tag.GetKnownMetadata().ToLookup(item => item.Key, item => item.Value);
        Assert.Contains("2024-05-06", known[TagFields.Date]);
        Assert.Contains("1999", known[TagFields.OriginalDate]);
    }

    [Fact]
    public void ConversionClearsVersionSpecificFrameFlags()
    {
        var tag = new ID3v2Tag();
        tag.Frames.Add(new ID3v2Frame(tag)
        {
            FrameID = "PRIV",
            Flags = 0x6000,
            Data = [1, 2, 3],
        });

        tag.ChangeVersion(ID3v2Version.V24);

        Assert.Equal(0, Assert.Single(tag.Frames).Flags);
    }

    [Fact]
    public void LinkedFramePayloadIdentifierChangesWidthWithTagVersion()
    {
        var tag = new ID3v2Tag();
        tag.ChangeVersion(ID3v2Version.V22);
        tag.Frames.Add(new ID3v2Frame(tag)
        {
            FrameID = "LNK",
            Data = Encoding.GetEncoding(28591).GetBytes("TT2\0https://example.invalid\0"),
        });

        tag.ChangeVersion(ID3v2Version.V23);
        ID3v2Frame v23 = Assert.Single(tag.Frames);
        Assert.Equal("LINK", v23.FrameID);
        Assert.Equal("TIT2", Encoding.GetEncoding(28591).GetString(v23.Data, 0, 4));

        tag.ChangeVersion(ID3v2Version.V22);
        ID3v2Frame v22 = Assert.Single(tag.Frames);
        Assert.Equal("LNK", v22.FrameID);
        Assert.Equal("TT2", Encoding.GetEncoding(28591).GetString(v22.Data, 0, 3));
    }

    [Fact]
    public void V24RawStructuredFrameWithUtf8EncodingCannotBeDowngraded()
    {
        var tag = new ID3v2Tag();
        tag.ChangeVersion(ID3v2Version.V24);
        tag.Frames.Add(new ID3v2Frame(tag)
        {
            FrameID = "GEOB",
            Data = [3, 0, 0, 0],
        });

        ID3VersionConversionException error = Assert.Throws<ID3VersionConversionException>(
            () => tag.ChangeVersion(ID3v2Version.V23));

        Assert.Contains(error.Issues, issue => issue.FrameID == "GEOB");
        Assert.Equal(4, tag.Version);
    }

    [Fact]
    public void V24Utf8TextIsReencodedForV23()
    {
        var tag = new ID3v2Tag();
        tag.ChangeVersion(ID3v2Version.V24);
        byte[] text = Encoding.UTF8.GetBytes("日本語");
        var raw = new ID3v2Frame(tag)
        {
            FrameID = "TIT2",
            Data = [3, .. text],
        };
        tag.Frames.Add(new TextFrame(raw));

        tag.ChangeVersion(ID3v2Version.V23);

        TextFrame converted = Assert.IsType<TextFrame>(Assert.Single(tag.Frames));
        Assert.Equal((byte)ID3v2Util.ID3Encoding.MarkedUnicode, converted.Data[0]);
        Assert.Contains(tag.GetKnownMetadata(),
            item => item.Key == TagFields.Title && item.Value == "日本語");
    }

    [Theory]
    [InlineData(ID3v2Version.V22, 3, 0, 0, 201)]
    [InlineData(ID3v2Version.V23, 4, 0, 0, 201)]
    [InlineData(ID3v2Version.V24, 4, 0, 1, 73)]
    public void FrameSizeUsesTargetVersionEncoding(
        ID3v2Version version,
        int idLength,
        byte sizeByte1,
        byte sizeByte2,
        byte sizeByte3)
    {
        string path = CreateUntaggedMp3();
        using var temp = new MediaFixtures.TempMedia(path);
        var mp3 = (MP3File)MediaFile.GetFile(path);
        mp3.ChangeVersion(version);
        mp3.SetField(TagFields.Title, new string('A', 200));
        mp3.Save();

        byte[] saved = File.ReadAllBytes(path);
        int sizeOffset = 10 + idLength;
        if (version == ID3v2Version.V22)
        {
            Assert.Equal(sizeByte1, saved[sizeOffset]);
            Assert.Equal(sizeByte2, saved[sizeOffset + 1]);
            Assert.Equal(sizeByte3, saved[sizeOffset + 2]);
        }
        else
        {
            Assert.Equal(0, saved[sizeOffset]);
            Assert.Equal(sizeByte1, saved[sizeOffset + 1]);
            Assert.Equal(sizeByte2, saved[sizeOffset + 2]);
            Assert.Equal(sizeByte3, saved[sizeOffset + 3]);
        }
    }

    [Theory]
    [InlineData(ID3v2Version.V22)]
    [InlineData(ID3v2Version.V23)]
    [InlineData(ID3v2Version.V24)]
    public void UnicodePictureDescriptionSurvivesSerialization(ID3v2Version version)
    {
        string path = CreateUntaggedMp3();
        using var temp = new MediaFixtures.TempMedia(path);
        var mp3 = (MP3File)MediaFile.GetFile(path);
        mp3.ChangeVersion(version);
        mp3.SetAttachedImage(
            ID3v2Util.APICType.FrontCover,
            "image/png",
            "封面",
            [1, 2, 3, 4]);
        mp3.Save();

        PictureFrame picture = Assert.IsType<PictureFrame>(
            Assert.Single(((MP3File)MediaFile.GetFile(path)).Frames));
        Assert.Equal("封面", picture.Description);
        Assert.Equal([1, 2, 3, 4], picture.PictureData);
    }

    [Theory]
    [InlineData(ID3v2Version.V22, "TT2")]
    [InlineData(ID3v2Version.V23, "TIT2")]
    [InlineData(ID3v2Version.V24, "TIT2")]
    public void Mp3SavePersistsSelectedVersionAndPreservesAudio(
        ID3v2Version version,
        string titleFrameId)
    {
        byte[] source = File.ReadAllBytes(MediaFixtures.Path_("sample.mp3"));
        byte[] audio = StripLeadingId3(source);
        string path = Path.Combine(Path.GetTempPath(), $"id3-version-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, audio);
        using var temp = new MediaFixtures.TempMedia(path);

        var mp3 = (MP3File)MediaFile.GetFile(path);
        mp3.ChangeVersion(version);
        mp3.SetField(TagFields.Title, "Saved Version");
        mp3.SetField(TagFields.Date, "2024-05-06");
        mp3.SetAttachedImage(ID3v2Util.APICType.FrontCover, "image/png", "", [1, 2, 3]);
        mp3.Save();

        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal("ID3", Encoding.ASCII.GetString(saved, 0, 3));
        Assert.Equal((byte)version, saved[3]);
        Assert.Equal(titleFrameId, Encoding.ASCII.GetString(
            saved, 10, version == ID3v2Version.V22 ? 3 : 4));
        Assert.Equal(audio, StripLeadingId3(saved));

        var reopened = (MP3File)MediaFile.GetFile(path);
        Assert.Equal((int)version, reopened.Version);
        Assert.Equal("Saved Version", reopened.Title);
        Assert.Equal("2024-05-06", reopened.ReleaseDate);
        Assert.Equal([1, 2, 3],
            Assert.Single(reopened.GetImageMetadata()).Data);
        Assert.Equal(44_100u, reopened.Samplerate);
    }

    [Fact]
    public void MetadataEditPreservesOpaqueFrameCustomFieldArtworkId3v1AndAudio()
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        byte[] audio = StripTagLayers(File.ReadAllBytes(media.Path));
        byte[] opaquePayload = [0xde, 0xad, 0xbe, 0xef, 0x00, 0xff];
        byte[] cover = [1, 3, 3, 7];

        var seeded = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        seeded.SetUserString("X-OPAQUE-CUSTOM", "keep me");
        seeded.SetAttachedImage(
            ID3v2Util.APICType.FrontCover,
            "image/png",
            "preserved cover",
            cover);
        seeded.Frames.Add(new ID3v2Frame(seeded)
        {
            FrameID = "XRAW",
            Data = opaquePayload.ToArray(),
        });
        seeded.AddTagLayer(
            TagLayerKind.Id3v1,
            TagLayerCopyMode.CopyPrimary);
        seeded.Save();

        var editable = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        editable.SetField(TagFields.Title, "Only this field changed");
        editable.Save();

        var reloaded = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        Assert.Equal("Only this field changed", reloaded.Title);
        Assert.Contains(
            reloaded.GetUserStrings(),
            value => value.Key == "X-OPAQUE-CUSTOM" &&
                value.Value == "keep me");
        Assert.Equal(
            opaquePayload,
            Assert.Single(
                reloaded.Frames,
                frame => frame.FrameID == "XRAW").Data);
        Assert.Equal(
            cover,
            Assert.Single(reloaded.GetImageMetadata()).Data);
        Assert.Equal(
            ["ID3v23", "ID3v1"],
            reloaded.Tags.Select(tag => tag.TagType));
        Assert.Equal("TestTitle", reloaded.Tags.Last().Title);
        Assert.Equal(
            audio,
            StripTagLayers(File.ReadAllBytes(media.Path)));
    }

    [Fact]
    public void ConvertedMp3CanBeSavedAgainInPlace()
    {
        byte[] source = File.ReadAllBytes(MediaFixtures.Path_("sample.mp3"));
        byte[] audio = StripLeadingId3(source);
        string path = Path.Combine(Path.GetTempPath(), $"id3-in-place-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, audio);
        using var temp = new MediaFixtures.TempMedia(path);

        var first = (MP3File)MediaFile.GetFile(path);
        first.SetField(TagFields.Title, "First");
        first.Save();

        var converted = (MP3File)MediaFile.GetFile(path);
        converted.ChangeVersion(ID3v2Version.V24);
        converted.SetField(TagFields.Title, "Second");
        converted.Save();

        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal(4, saved[3]);
        Assert.Equal(audio, StripLeadingId3(saved));
        Assert.Equal("Second", MediaFile.GetFile(path).Tags.Single().Title);
    }

    [Fact]
    public void ConvertedMp3CanBeSavedToASeparatePath()
    {
        using var source = MediaFixtures.Copy("sample.mp3");
        string outputPath = Path.Combine(
            Path.GetTempPath(), $"id3-output-{Guid.NewGuid():N}.mp3");
        using var output = new MediaFixtures.TempMedia(outputPath);
        byte[] sourceAudio = StripLeadingId3(File.ReadAllBytes(source.Path));

        var mp3 = (MP3File)MediaFile.GetFile(source.Path);
        mp3.ChangeVersion(ID3v2Version.V22);
        mp3.SetField(TagFields.Title, "Separate Output");
        mp3.Save(outputPath);

        var reopened = (MP3File)MediaFile.GetFile(outputPath);
        Assert.Equal(2, reopened.Version);
        Assert.Equal("Separate Output", reopened.Title);
        Assert.Equal(sourceAudio, StripLeadingId3(File.ReadAllBytes(outputPath)));
        Assert.NotEqual("Separate Output",
            MediaFile.GetFile(source.Path).Tags.Single().Title);
    }

    [Fact]
    public void DsfSavePersistsSelectedV22Tag()
    {
        using var temp = MediaFixtures.Copy("sample.dsf");
        var dsf = (DSFFile)MediaFile.GetFile(temp.Path);
        dsf.ChangeVersion(ID3v2Version.V22);
        dsf.SetField(TagFields.Title, "V22 DSD");
        dsf.SaveTags();

        var reopened = (DSFFile)MediaFile.GetFile(temp.Path);
        Assert.Equal(2, reopened.Version);
        Assert.Equal("V22 DSD", reopened.Title);
        Assert.Equal(2_822_400u, reopened.Samplerate);
    }

    [Fact]
    public void ExistingCommonV23TagConvertsAndReopensAsV24()
    {
        using var temp = MediaFixtures.Copy("sample.mp3");
        var mp3 = (MP3File)MediaFile.GetFile(temp.Path);

        mp3.ChangeVersion(ID3v2Version.V24);
        mp3.Save();

        var reopened = (MP3File)MediaFile.GetFile(temp.Path);
        Assert.Equal(4, reopened.Version);
        Assert.Equal("TestTitle", reopened.Title);
        Assert.Equal("TestArtist", reopened.Artist);
        Assert.Equal(44_100u, reopened.Samplerate);
    }

    private static ID3v2Tag CreatePopulatedTag(ID3v2Version version)
    {
        var tag = new ID3v2Tag();
        if (version != ID3v2Version.V23)
            tag.ChangeVersion(version);
        tag.SetField(TagFields.Title, "Converted Title");
        tag.SetField(TagFields.Artist, "Converted Artist");
        tag.SetField(TagFields.Date, "2024-05-06");
        tag.SetField(TagFields.OriginalDate, "1999");
        tag.SetField(TagFields.MusicBrainz_ArtistID, "artist-id");
        tag.SetField(TagFields.Lyrics, "converted lyrics");
        var identifier = new IdentifierFrame(tag)
        {
            FrameID = version == ID3v2Version.V22 ? "UFI" : "UFID",
            Key = "owner",
            Value = [1, 2, 3, 4],
        };
        tag.Frames.Add(identifier);
        var comment = new CommentFrame(tag)
        {
            FrameID = version == ID3v2Version.V22 ? "COM" : "COMM",
            Language = "eng",
            Key = "note",
            Value = "comment value",
        };
        tag.Frames.Add(comment);
        tag.SetAttachedImage(
            ID3v2Util.APICType.FrontCover,
            "image/png",
            "cover",
            [9, 8, 7, 6]);
        return tag;
    }

    private static byte[] StripLeadingId3(byte[] file)
    {
        if (file.Length < 10 || Encoding.ASCII.GetString(file, 0, 3) != "ID3")
            return file;
        int size = (file[6] << 21) | (file[7] << 14) | (file[8] << 7) | file[9];
        int footer = file[3] == 4 && (file[5] & 0x10) != 0 ? 10 : 0;
        return file[(10 + size + footer)..];
    }

    private static byte[] StripTagLayers(byte[] file)
    {
        byte[] withoutLeading = StripLeadingId3(file);
        return withoutLeading.Length >= 128 &&
            Encoding.ASCII.GetString(
                withoutLeading,
                withoutLeading.Length - 128,
                3) == "TAG"
            ? withoutLeading[..^128]
            : withoutLeading;
    }

    private static string CreateUntaggedMp3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"id3-untagged-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, StripLeadingId3(
            File.ReadAllBytes(MediaFixtures.Path_("sample.mp3"))));
        return path;
    }
}
