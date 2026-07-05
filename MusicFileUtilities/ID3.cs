/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/ID3.cs $
 * $Date: 2014-09-27 10:37:30 -0600 (Sat, 27 Sep 2014) $
 * $Revision: 20 $
 * $Author: colin $
 * 
 */

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Net.Mail;

namespace MusicFileUtilities
{

    public enum TagAction { Get, Set, Add, Delete };

    public class ID3v2Util
    {

        public enum ID3Encoding : byte { ISO8859 = 0, MarkedUnicode = 1, BEUnicode = 2, UTF8 = 3 };

        [Serializable]
        public enum APICType : byte
        {
            Other = 0, FileIcon = 1, OtherFileIcon = 2, FrontCover = 3, BackCover = 4, LeafletPage = 5, Media = 6,
            LeadArtist = 7, Arist = 8, Conductor = 9, Band = 10, Composer = 11, Lyricist = 12, RecordingLocation = 13, DuringRecording = 14,
            DuringPerformance = 15, VideoScreenCapture = 16, BrightColoredFish = 17, Illustration = 18, BandLogo = 19, StudioLogo = 20
        };

        public static bool UseUTF8 { get; set; } = false;
        public static bool CoalesceID3v22Values { get; set; } = true;
        public static bool CoalesceID3v23Values { get; set; } = true;
        public static bool CoalesceID3v24Values { get; set; } = false;
        public static bool StrictRules { get; set; } = false;

        public static readonly IList<string> ID3v1Genres = new List<string> {
            "Blues", "Classic Rock", "Country", "Dance", "Disco",
            "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal",
            "New Age", "Oldies", "Other", "Pop", "R&B",
            "Rap", "Reggae", "Rock", "Techno", "Industrial",
            "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack",
            "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk",
            "Fusion", "Trance", "Classical", "Instrumental", "Acid",
            "House", "Game", "Sound Clip", "Gospel", "Noise",
            "AlternRock", "Bass", "Soul", "Punk", "Space",
            "Meditative", "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic",
            "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance",
            "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta",
            "Top 40", "Christian Rap", "Pop/Funk", "Jungle", "Native American",
            "Cabaret", "New Wave", "Psychadelic", "Rave", "Showtunes",
            "Trailer", "Lo-Fi", "Tribal", "Acid Punk", "Acid Jazz",
            "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
            "Folk", "Folk/Rock", "National Folk", "Swing", "Fast Fusion",
            "Bebob", "Latin", "Revival", "Celtic", "Bluegrass",
            "Avantgarde", "Gothic Rock", "Progressive Rock", "Psychedelic Rock", "Symphonic Rock",
            "Slow Rock", "Big Band", "Chorus", "Easy Listening", "Acoustic",
            "Humour", "Speech", "Chanson", "Opera", "Chamber Music", "Sonata",
            "Symphony", "Booty Bass", "Primus", "Porn Groove",
            "Satire", "Slow Jam", "Club", "Tango", "Samba",
            "Folklore", "Ballad", "Power Ballad", "Rhythmic Soul", "Freestyle",
            "Duet", "Punk Rock", "Drum Solo", "A Capella", "Euro-House",
            "Dance Hall" };

        public static readonly Encoding ISO8859Encoding = Encoding.GetEncoding(28591,
            new EncoderExceptionFallback(), new DecoderExceptionFallback()); // iso-8859-1

        public delegate object[] HandleTagAction(ID3v2Tag tag, TagAction action, params object[] values);

        private static object[] GenreMapping(ID3v2Tag tag, string frameid, TagAction action, params object[] values)
        {
            // A missing frame is the overwhelmingly common case on a scan (each mapping is
            // probed for every file), so it must not be signalled by throwing: exceptions as
            // control flow here dominated the whole parse. Absent = empty result.
            var frame = tag.FindFrame(frameid) as TextFrame;
            if (action == TagAction.Get)
            {
                if (frame is null)
                    return Array.Empty<object>();

                var res = new List<object>();

                string refinement = "", reference = "";
                int state = 0;
                foreach (char c in frame.Text)
                {
                    switch (state)
                    {
                        case 0:
                            if (c == '(')
                                state = 1;
                            else
                                refinement += c;
                            break;

                        case 1:
                            if (c == '(')
                            {
                                refinement += "(";
                                state = 2;
                            }
                            else if (c == ')')
                            {
                                int genre;
                                if (int.TryParse(reference, out genre))
                                {
                                    res.Add(ID3v2Util.ID3v1Genres[genre]);
                                }
                                else
                                {
                                    if (reference == "RX")
                                        res.Add("Remix");
                                    if (reference == "CR")
                                        res.Add("Cover");
                                }
                            }
                            else
                                reference += c;
                            break;

                        default:
                            refinement += c;
                            if (c == ')')
                                state = 0;
                            break;
                    }
                }
                if (refinement != "")
                    res.Add(refinement);
                return res.ToArray();
            }
            throw new InvalidOperationException();
        }

        public static object[] UFIDMapping(ID3v2Tag tag, string frameid, string ufidid, TagAction action, params object[] values)
        {
            var frame = tag.FindIdentifierFrame(frameid, ufidid);
            switch (action)
            {
                case TagAction.Get:
                    if (frame is null)
                        return Array.Empty<object>();
                    return new string[] { ISO8859Encoding.GetString(frame.Value) };

                default:
                    throw new InvalidOperationException();
            }
        }

        public static object[] IndexMapping(ID3v2Tag tag, string frameid, TagAction action, params object[] values)
        {
            var frame = tag.FindFrame(frameid) as TextFrame;
            switch (action)
            {
                case TagAction.Get:
                    if (frame is null)
                        return Array.Empty<object>();
                    var splits = frame.Text.Split("/".ToCharArray());
                    if (splits.Length < 1)
                        return Array.Empty<object>();
                    return new string[] { splits[0] };

                default:
                    throw new InvalidOperationException();
            }
        }

        public static object[] TotalMapping(ID3v2Tag tag, string frameid, TagAction action, params object[] values)
        {
            var frame = tag.FindFrame(frameid) as TextFrame;
            switch (action)
            {
                case TagAction.Get:
                    if (frame is null)
                        return Array.Empty<object>();
                    var splits = frame.Text.Split("/".ToCharArray());
                    if (splits.Length < 2)
                        return Array.Empty<object>();
                    return new string[] { splits[1] };

                default:
                    throw new InvalidOperationException();
            }
        }

        public static object[] SimpleMapping(ID3v2Tag tag, string frameid, TagAction action, params object[] values)
        {
            var frame = tag.FindFrame(frameid) as TextFrame;
            switch (action)
            {
                case TagAction.Get:
                    if (frame is null)
                        return Array.Empty<object>();
                    return frame.Values.ToArray();

                default:
                    throw new InvalidOperationException();
            }
        }

        public static object[] UserStringMapping(ID3v2Tag tag, string frameid, string userstringid, TagAction action, params object[] values)
        {
            var frame = tag.FindUserStringFrame(frameid, userstringid);
            switch (action)
            {
                case TagAction.Get:
                    if (frame is null)
                        return Array.Empty<object>();
                    return new string[] { frame.Value };

                default:
                    throw new InvalidOperationException();
            }
        }

        public static object[] DateMapping(ID3v2Tag tag, TagAction action, params object[] values)
        {
            if (tag.Version == 2)
            {
                var tyer = tag.FindFrame("TYE") as TextFrame;
                var tdat = tag.FindFrame("TDA") as TextFrame;
                if (action == TagAction.Get)
                {
                    string res = string.Empty;
                    if (!(tyer is null))
                    {
                        res += tyer.Text;
                        if (!(tdat is null))
                            res += "-" + tdat.Text.Substring(2, 2) + "-" + tdat.Text.Substring(0, 2);
                        return new string[] { res };
                    }
                    return Array.Empty<object>();
                }
                throw new InvalidOperationException();
            }
            if (tag.Version == 3)
            {
                var tyer = tag.FindFrame("TYER") as TextFrame;
                var tdat = tag.FindFrame("TDAT") as TextFrame;
                if (action == TagAction.Get)
                {
                    string res = string.Empty;
                    if (!(tyer is null))
                    {
                        res += tyer.Text;
                        if (!(tdat is null))
                            res += "-" + tdat.Text.Substring(2, 2) + "-" + tdat.Text.Substring(0, 2);
                        return new string[] { res };
                    }
                    return Array.Empty<object>();
                }
                throw new InvalidOperationException();
            }
            if (tag.Version == 4)
            {
                var tdrc = tag.FindFrame("TDRC") as TextFrame;
                if (action == TagAction.Get)
                {
                    if (tdrc is null)
                        return Array.Empty<object>();
                    return tdrc.Values.ToArray();
                }
            }
            throw new InvalidOperationException();
        }

        public static object[] OriginalDateMapping(ID3v2Tag tag, TagAction action, params object[] values)
        {
            if (tag.Version == 2)
            {
                var tory = tag.FindFrame("TOR") as TextFrame;
                if (action == TagAction.Get)
                {
                    if (tory is null)
                        return Array.Empty<object>();
                    return new string[] { tory.Text };
                }
                throw new InvalidOperationException();
            }
            if (tag.Version == 3)
            {
                var tory = tag.FindFrame("TORY") as TextFrame;
                if (action == TagAction.Get)
                {
                    if (tory is null)
                        return Array.Empty<object>();
                    return new string[] { tory.Text };
                }
                throw new InvalidOperationException();
            }
            if (tag.Version == 4)
            {
                var tdor = tag.FindFrame("TDOR") as TextFrame;
                if (action == TagAction.Get)
                {
                    if (tdor is null)
                        return Array.Empty<object>();
                    return tdor.Values.ToArray();
                }
            }
            throw new InvalidOperationException();
        }

        public static readonly IReadOnlyDictionary<TagFields, HandleTagAction> ActionMappingsv22 = new Dictionary<TagFields, HandleTagAction>()
        {
            { TagFields.Album, (tag, action, values) => SimpleMapping(tag, "TAL", action, values) },
            { TagFields.AlbumArtist, (tag, action, values) => SimpleMapping(tag, "TP2", action, values) },
            { TagFields.AlbumArtistSort, (tag, action, values) => SimpleMapping(tag, "TS2", action, values) },
            { TagFields.AlbumSort, (tag, action, values) => SimpleMapping(tag, "TSA", action, values) },
            { TagFields.Artist, (tag, action, values) => SimpleMapping(tag, "TP1", action, values) },
            { TagFields.ArtistSort, (tag, action, values) => SimpleMapping(tag, "TSP", action, values) },
            { TagFields.BPM, (tag, action, values) => SimpleMapping(tag, "TBP", action, values) },
            { TagFields.Compilation, (tag, action, values) => SimpleMapping(tag, "TCP", action, values) },
            { TagFields.Composer, (tag, action, values) => SimpleMapping(tag, "TCM", action, values) },
            { TagFields.ComposerSort, (tag, action, values) => SimpleMapping(tag, "TSC", action, values) },
            { TagFields.Conductor, (tag, action, values) => SimpleMapping(tag, "TP3", action, values) },
            { TagFields.Copyright, (tag, action, values) => SimpleMapping(tag, "TCR", action, values) },
            { TagFields.DiscSubtitle, (tag, action, values) => SimpleMapping(tag, "TPS", action, values) },
            { TagFields.EncodedBy, (tag, action, values) => SimpleMapping(tag, "TEN", action, values) },
           // { TagFields.EncoderSettings, (tag, action, values) => SimpleMapping(tag, "TSsdfsdfsdfsdfSE", action, values) },
            { TagFields.Grouping, (tag, action, values) => SimpleMapping(tag, "GP1", action, values) },
            { TagFields.Key, (tag, action, values) => SimpleMapping(tag, "TKE", action, values) },
            { TagFields.ISRC, (tag, action, values) => SimpleMapping(tag, "TRC", action, values) },
            { TagFields.Language, (tag, action, values) => SimpleMapping(tag, "TLA", action, values) },
            { TagFields.Lyrics, (tag, action, values) => SimpleMapping(tag, "ULT", action, values) },
            { TagFields.Media, (tag, action, values) => SimpleMapping(tag, "TMT", action, values) },
            { TagFields.Mood, (tag, action, values) => UserStringMapping(tag, "TXX", "MOOD", action, values) },
            { TagFields.Movement, (tag, action, values) => SimpleMapping(tag, "MVN", action, values) },
            { TagFields.OriginalAlbum, (tag, action, values) => SimpleMapping(tag, "TOT", action, values) },
            { TagFields.OriginalArtist, (tag, action, values) => SimpleMapping(tag, "TOA", action, values) },
            { TagFields.OriginalFileName, (tag, action, values) => SimpleMapping(tag, "TOF", action, values) },
            { TagFields.Rating, (tag, action, values) => SimpleMapping(tag, "POP", action, values) },
            { TagFields.Label, (tag, action, values) => SimpleMapping(tag, "TPB", action, values) },
            { TagFields.Remixer, (tag, action, values) => SimpleMapping(tag, "TP4", action, values) },
            { TagFields.Subtitle, (tag, action, values) => SimpleMapping(tag, "TT3", action, values) },
            { TagFields.Title, (tag, action, values) => SimpleMapping(tag, "TT2", action, values) },
            { TagFields.TitleSort, (tag, action, values) => SimpleMapping(tag, "TST", action, values) },
            { TagFields.Website, (tag, action, values) => SimpleMapping(tag, "WAR", action, values) },
            { TagFields.AcoustID_ID, (tag, action, values) => UserStringMapping(tag, "TXX", "Acoustid Id", action, values) },
            { TagFields.AcoustID_Fingerprint, (tag, action, values) => UserStringMapping(tag, "TXX", "Acoustid Fingerprint", action, values) },
            { TagFields.Artists, (tag, action, values) => UserStringMapping(tag, "TXX", "Artists", action, values) },
            { TagFields.ASIN, (tag, action, values) => UserStringMapping(tag, "TXX", "ASIN", action, values) },
            { TagFields.Barcode, (tag, action, values) => UserStringMapping(tag, "TXX", "BARCODE", action, values) },
            { TagFields.CatalogNumber, (tag, action, values) => UserStringMapping(tag, "TXX", "CATALOGNUMBER", action, values) },
            { TagFields.MusicBrainz_ArtistID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Artist Id", action, values) },
            { TagFields.MusicBrainz_DiscID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Disc Id", action, values) },
            { TagFields.MusicBrainz_OriginalArtistID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Original Artist Id", action, values) },
            { TagFields.MusicBrainz_OriginalAlbumID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Original Album Id", action, values) },
            { TagFields.MusicBrainz_RecordingID, (tag, action, values) => UFIDMapping(tag, "UFI", "http://musicbrainz.org", action, values) },
            { TagFields.MusicBrainz_AlbumArtistID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Album Artist Id", action, values) },
            { TagFields.MusicBrainz_ReleaseGroupID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Release Group Id", action, values) },
            { TagFields.MusicBrainz_AlbumID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Album Id", action, values) },
            { TagFields.MusicBrainz_TrackID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Release Track Id", action, values) },
            { TagFields.MusicBrainz_WorkID, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Work Id", action, values) },
            { TagFields.ReleaseCountry, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Album Release Country", action, values) },
            { TagFields.ReleaseStatus, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Album Status", action, values) },
            { TagFields.ReleaseType, (tag, action, values) => UserStringMapping(tag, "TXX", "MusicBrainz Album Type", action, values) },
            { TagFields.ReplayGain_Album_Gain, (tag, action, values) => UserStringMapping(tag, "TXX", "REPLAYGAIN_ALBUM_GAIN", action, values) },
            { TagFields.ReplayGain_Album_Peak, (tag, action, values) => UserStringMapping(tag, "TXX", "REPLAYGAIN_ALBUM_PEAK", action, values) },
            { TagFields.ReplayGain_Album_Range, (tag, action, values) => UserStringMapping(tag, "TXX", "REPLAYGAIN_ALBUM_RANGE", action, values) },
            { TagFields.ReplayGain_Reference_Loudness, (tag, action, values) => UserStringMapping(tag, "TXX", "REPLAYGAIN_REFERENCE_LOUDNESS", action, values) },
            { TagFields.ReplayGain_Track_Gain, (tag, action, values) => UserStringMapping(tag, "TXX", "REPLAYGAIN_TRACK_GAIN", action, values) },
            { TagFields.ReplayGain_Track_Peak, (tag, action, values) => UserStringMapping(tag, "TXX", "REPLAYGAIN_TRACK_PEAK", action, values) },
            { TagFields.ReplayGain_Track_Range, (tag, action, values) => UserStringMapping(tag, "TXX", "REPLAYGAIN_TRACK_RANGE", action, values) },
            { TagFields.Script, (tag, action, values) => UserStringMapping(tag, "TXX", "SCRIPT", action, values) },
            { TagFields.ShowMovement, (tag, action, values) => UserStringMapping(tag, "TXX", "SHOWMOVEMENT", action, values) },
            { TagFields.Work, (tag, action, values) => UserStringMapping(tag, "TXX", "WORK", action, values) },
            { TagFields.Writer, (tag, action, values) => UserStringMapping(tag, "TXX", "Writer", action, values) },
            { TagFields.TrackNumber, (tag, action, values) => IndexMapping(tag, "TRK", action, values) },
            { TagFields.TotalTracks, (tag, action, values) => TotalMapping(tag, "TRK", action, values) },
            { TagFields.DiscNumber, (tag, action, values) => IndexMapping(tag, "TPA", action, values) },
            { TagFields.TotalDiscs, (tag, action, values) => TotalMapping(tag, "TPA", action, values) },
            { TagFields.MovementNumber, (tag, action, values) => IndexMapping(tag, "MVI", action, values) },
            { TagFields.MovementTotal, (tag, action, values) => TotalMapping(tag, "MVI", action, values) },
            { TagFields.Genre, (tag, action, values) => GenreMapping(tag, "TCO", action, values) },
            { TagFields.Date, DateMapping },
            { TagFields.OriginalDate, OriginalDateMapping },
        };

        public static readonly IReadOnlyDictionary<TagFields, HandleTagAction> ActionMappingsv23v24 = new Dictionary<TagFields, HandleTagAction>()
        {
            { TagFields.Album, (tag, action, values) => SimpleMapping(tag, "TALB", action, values) },
            { TagFields.AlbumArtist, (tag, action, values) => SimpleMapping(tag, "TPE2", action, values) },
            { TagFields.AlbumArtistSort, (tag, action, values) => SimpleMapping(tag, "TSO2", action, values) },
            { TagFields.AlbumSort, (tag, action, values) => SimpleMapping(tag, "TSOA", action, values) },
            { TagFields.Artist, (tag, action, values) => SimpleMapping(tag, "TPE1", action, values) },
            { TagFields.ArtistSort, (tag, action, values) => SimpleMapping(tag, "TSOP", action, values) },
            { TagFields.BPM, (tag, action, values) => SimpleMapping(tag, "TBPM", action, values) },
            { TagFields.Compilation, (tag, action, values) => SimpleMapping(tag, "TCMP", action, values) },
            { TagFields.Composer, (tag, action, values) => SimpleMapping(tag, "TCOM", action, values) },
            { TagFields.ComposerSort, (tag, action, values) => SimpleMapping(tag, "TSOC", action, values) },
            { TagFields.Conductor, (tag, action, values) => SimpleMapping(tag, "TPE3", action, values) },
            { TagFields.Copyright, (tag, action, values) => SimpleMapping(tag, "TCOP", action, values) },
            { TagFields.DiscSubtitle, (tag, action, values) => SimpleMapping(tag, "TSST", action, values) },
            { TagFields.EncodedBy, (tag, action, values) => SimpleMapping(tag, "TENC", action, values) },
            { TagFields.EncoderSettings, (tag, action, values) => SimpleMapping(tag, "TSSE", action, values) },
            { TagFields.Grouping, (tag, action, values) => SimpleMapping(tag, "TIT1", action, values) },
            { TagFields.Key, (tag, action, values) => SimpleMapping(tag, "TKEY", action, values) },
            { TagFields.ISRC, (tag, action, values) => SimpleMapping(tag, "TSRC", action, values) },
            { TagFields.Language, (tag, action, values) => SimpleMapping(tag, "TLAN", action, values) },
            { TagFields.Lyrics, (tag, action, values) => SimpleMapping(tag, "USLT", action, values) },
            { TagFields.Media, (tag, action, values) => SimpleMapping(tag, "TMED", action, values) },
            { TagFields.Mood, (tag, action, values) => SimpleMapping(tag, "TMOO", action, values) },
            { TagFields.Movement, (tag, action, values) => SimpleMapping(tag, "MVNM", action, values) },
            { TagFields.OriginalAlbum, (tag, action, values) => SimpleMapping(tag, "TOAL", action, values) },
            { TagFields.OriginalArtist, (tag, action, values) => SimpleMapping(tag, "TOPE", action, values) },
            { TagFields.OriginalFileName, (tag, action, values) => SimpleMapping(tag, "TOFN", action, values) },
            { TagFields.Rating, (tag, action, values) => SimpleMapping(tag, "POPM", action, values) },
            { TagFields.Label, (tag, action, values) => SimpleMapping(tag, "TPUB", action, values) },
            { TagFields.Remixer, (tag, action, values) => SimpleMapping(tag, "TPE4", action, values) },
            { TagFields.Subtitle, (tag, action, values) => SimpleMapping(tag, "TIT3", action, values) },
            { TagFields.Title, (tag, action, values) => SimpleMapping(tag, "TIT2", action, values) },
            { TagFields.TitleSort, (tag, action, values) => SimpleMapping(tag, "TSOT", action, values) },
            { TagFields.Website, (tag, action, values) => SimpleMapping(tag, "WOAR", action, values) },
            { TagFields.AcoustID_ID, (tag, action, values) => UserStringMapping(tag, "TXXX", "Acoustid Id", action, values) },
            { TagFields.AcoustID_Fingerprint, (tag, action, values) => UserStringMapping(tag, "TXXX", "Acoustid Fingerprint", action, values) },
            { TagFields.Artists, (tag, action, values) => UserStringMapping(tag, "TXXX", "Artists", action, values) },
            { TagFields.ASIN, (tag, action, values) => UserStringMapping(tag, "TXXX", "ASIN", action, values) },
            { TagFields.Barcode, (tag, action, values) => UserStringMapping(tag, "TXXX", "BARCODE", action, values) },
            { TagFields.CatalogNumber, (tag, action, values) => UserStringMapping(tag, "TXXX", "CATALOGNUMBER", action, values) },
            { TagFields.MusicBrainz_ArtistID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Artist Id", action, values) },
            { TagFields.MusicBrainz_DiscID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Disc Id", action, values) },
            { TagFields.MusicBrainz_OriginalArtistID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Original Artist Id", action, values) },
            { TagFields.MusicBrainz_OriginalAlbumID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Original Album Id", action, values) },
            { TagFields.MusicBrainz_RecordingID, (tag, action, values) => UFIDMapping(tag, "UFID", "http://musicbrainz.org", action, values) },
            { TagFields.MusicBrainz_AlbumArtistID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Album Artist Id", action, values) },
            { TagFields.MusicBrainz_ReleaseGroupID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Release Group Id", action, values) },
            { TagFields.MusicBrainz_AlbumID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Album Id", action, values) },
            { TagFields.MusicBrainz_TrackID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Release Track Id", action, values) },
            { TagFields.MusicBrainz_WorkID, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Work Id", action, values) },
            { TagFields.ReleaseCountry, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Album Release Country", action, values) },
            { TagFields.ReleaseStatus, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Album Status", action, values) },
            { TagFields.ReleaseType, (tag, action, values) => UserStringMapping(tag, "TXXX", "MusicBrainz Album Type", action, values) },
            { TagFields.ReplayGain_Album_Gain, (tag, action, values) => UserStringMapping(tag, "TXXX", "REPLAYGAIN_ALBUM_GAIN", action, values) },
            { TagFields.ReplayGain_Album_Peak, (tag, action, values) => UserStringMapping(tag, "TXXX", "REPLAYGAIN_ALBUM_PEAK", action, values) },
            { TagFields.ReplayGain_Album_Range, (tag, action, values) => UserStringMapping(tag, "TXXX", "REPLAYGAIN_ALBUM_RANGE", action, values) },
            { TagFields.ReplayGain_Reference_Loudness, (tag, action, values) => UserStringMapping(tag, "TXXX", "REPLAYGAIN_REFERENCE_LOUDNESS", action, values) },
            { TagFields.ReplayGain_Track_Gain, (tag, action, values) => UserStringMapping(tag, "TXXX", "REPLAYGAIN_TRACK_GAIN", action, values) },
            { TagFields.ReplayGain_Track_Peak, (tag, action, values) => UserStringMapping(tag, "TXXX", "REPLAYGAIN_TRACK_PEAK", action, values) },
            { TagFields.ReplayGain_Track_Range, (tag, action, values) => UserStringMapping(tag, "TXXX", "REPLAYGAIN_TRACK_RANGE", action, values) },
            { TagFields.Script, (tag, action, values) => UserStringMapping(tag, "TXXX", "SCRIPT", action, values) },
            { TagFields.ShowMovement, (tag, action, values) => UserStringMapping(tag, "TXXX", "SHOWMOVEMENT", action, values) },
            { TagFields.Work, (tag, action, values) => UserStringMapping(tag, "TXXX", "WORK", action, values) },
            { TagFields.Writer, (tag, action, values) => UserStringMapping(tag, "TXXX", "Writer", action, values) },
            { TagFields.TrackNumber, (tag, action, values) => IndexMapping(tag, "TRCK", action, values) },
            { TagFields.TotalTracks, (tag, action, values) => TotalMapping(tag, "TRCK", action, values) },
            { TagFields.DiscNumber, (tag, action, values) => IndexMapping(tag, "TPOS", action, values) },
            { TagFields.TotalDiscs, (tag, action, values) => TotalMapping(tag, "TPOS", action, values) },
            { TagFields.MovementNumber, (tag, action, values) => IndexMapping(tag, "MVIN", action, values) },
            { TagFields.MovementTotal, (tag, action, values) => TotalMapping(tag, "MVIN", action, values) },
            { TagFields.Genre, (tag, action, values) => GenreMapping(tag, "TCON", action, values) },
            { TagFields.Date, DateMapping },
            { TagFields.OriginalDate, OriginalDateMapping },


        };


        static ID3v2Util()
        {
        }
         
    }

    public class ID3v2Frame
    {

        public void Write(FileStream s)
        {
            int datalen = Data.Length;
            if (tag_.Version == 2)
            {
                byte[] header = new byte[6];
                ID3v2Util.ISO8859Encoding.GetBytes(FrameID, 0, 3, header, 0);
                header[3] = (byte)((datalen >> 16) & 0xff);
                header[4] = (byte)((datalen >> 8) & 0xff);
                header[5] = (byte)(datalen & 0xff);
                s.Write(header, 0, 6);
            }
            else
            {
                byte[] header = new byte[10];
                ID3v2Util.ISO8859Encoding.GetBytes(FrameID, 0, 4, header, 0);
                if (tag_.Version >= 4) // SyncSafe
                {
                    header[4] = (byte)((datalen >> 21) & 0x7f);
                    header[5] = (byte)((datalen >> 14) & 0x7f);
                    header[6] = (byte)((datalen >> 7) & 0x7f);
                    header[7] = (byte)(datalen & 0x7f);
                }
                else
                {
                    header[4] = (byte)((datalen >> 24) & 0xff);
                    header[5] = (byte)((datalen >> 16) & 0xff);
                    header[6] = (byte)((datalen >> 8) & 0xff);
                    header[7] = (byte)(datalen & 0xff);
                }
                header[8] = (byte)((Flags >> 8) & 0xff);
                header[9] = (byte)(Flags & 0xff);
                s.Write(header, 0, 10);
            }
            s.Write(Data, 0, datalen);
        }

        public string FrameID = "";
        public int Flags = 0;
        public byte[] Data = new byte[] { };
        protected ID3v2Tag tag_;

        public ID3v2Tag Tag => tag_;

        public ID3v2Frame(ID3v2Frame from)
        {
            FrameID = from.FrameID;
            Flags = from.Flags;
            Data = from.Data;
            tag_ = from.tag_;
        }

        public ID3v2Frame(ID3v2Tag tag)
        {
            tag_ = tag;
        }

        protected string GetStringAt(ID3v2Util.ID3Encoding coding, int offset)
        {
            return GetStringAt(coding, offset, Data.Length - offset);
        }

        protected string GetStringAt(ID3v2Util.ID3Encoding coding, int offset, int length)
        {
            if (coding == ID3v2Util.ID3Encoding.ISO8859)
                return ID3v2Util.ISO8859Encoding.GetString(Data, offset, length);
            if (coding == ID3v2Util.ID3Encoding.MarkedUnicode)
            {
                bool bigendian = ((Data[offset] == 0xfe) && (Data[offset + 1] == 0xff));
                UnicodeEncoding encoding = new UnicodeEncoding(bigendian, false);
                return encoding.GetString(Data, offset + 2, length - 2);
            }

            if (!(tag_.Version >= 4))
                throw new InvalidDataException();

            if (coding == ID3v2Util.ID3Encoding.BEUnicode)
                return Encoding.BigEndianUnicode.GetString(Data, offset, length);
            if (coding == ID3v2Util.ID3Encoding.UTF8)
                return Encoding.UTF8.GetString(Data, offset, length);

            throw new InvalidDataException();
        }

        protected string GetNullTerminatedStringAt(ID3v2Util.ID3Encoding coding, int offset)
        {
            int length = 0;

            if ((coding == ID3v2Util.ID3Encoding.BEUnicode) || (coding == ID3v2Util.ID3Encoding.MarkedUnicode))
            {
                for (; length < (Data.Length - offset); length += 2)
                {
                    if ((Data[offset + length] == 0) && (Data[offset + length + 1] == 0))
                        break;
                }
            }
            else
            {
                for (; length < (Data.Length - offset); length ++)
                {
                    if (Data[offset + length] == 0)
                        break;
                }
            }

            return GetStringAt(coding, offset, length);
        }

        protected byte[] CodeString(ID3v2Util.ID3Encoding coding, string value)
        {
            if (coding == ID3v2Util.ID3Encoding.ISO8859)
                return ID3v2Util.ISO8859Encoding.GetBytes(value);
            if (coding == ID3v2Util.ID3Encoding.MarkedUnicode)
            {
                byte[] res = Encoding.Unicode.GetBytes(value);
                byte[] newres = new byte[res.Length + 2];
                newres[0] = 0xff;
                newres[1] = 0xfe;
                Array.Copy(res, 0, newres, 2, res.Length);
                return newres;
            }

            if (!(tag_.Version >= 4))
                throw new InvalidDataException();
            
            if (coding == ID3v2Util.ID3Encoding.BEUnicode)
                return Encoding.BigEndianUnicode.GetBytes(value);
            if (coding == ID3v2Util.ID3Encoding.UTF8)
                return Encoding.UTF8.GetBytes(value);

            throw new InvalidDataException();
        }

        public virtual void Encode()
        {

        }

        public virtual void Decode()
        {
            if (Tag.Version == 4)
            {
                if ((Flags & 1) == 1)
                    Data = Data.Skip(4).ToArray();
                if (((Flags & 2) == 2)||((Tag.Flags & 0x80) == 0x80))
                {
                    bool lastunsync = false;
                    List<byte> unsync = new List<byte>();
                    for (int i = 0; i < Data.Length - 1; i++)
                    {
                        if ((Data[i] == 0xff) && (Data[i + 1] == 0x00))
                        {
                            unsync.Add(0xff);
                            i++;
                            lastunsync = true;
                        }
                        else
                        {
                            unsync.Add(Data[i]);
                            lastunsync = false;
                        }
                    }
                    if (!lastunsync)
                        unsync.Add(Data[Data.Length - 1]);
                    Data = unsync.ToArray();
                }
            }

        }

    }

    public class TextFrame : ID3v2Frame
    {
        public TextFrame(ID3v2Tag tag) : base(tag)
        {
        }

        private string[] values_ = new string[] { string.Empty };

        public TextFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        public override void Decode()
        {
            base.Decode();
            // A zero-length (or encoding-byte-only) frame is malformed but real taggers emit
            // them; treat it as a single empty value instead of indexing Data[0] out of range.
            if (Data.Length == 0)
            {
                values_ = new[] { string.Empty };
                return;
            }
            if ((tag_.Version >= 4)||(!ID3v2Util.StrictRules))
            {
                if (FrameID[0] == 'W')
                    values_ = GetStringAt(ID3v2Util.ID3Encoding.ISO8859, 0).Split("\0".ToCharArray());
                else
                {
                    var vals = new List<string>();
                    int offset = 1;
                    var encoding = (ID3v2Util.ID3Encoding)Data[0];
                    while (offset < Data.Length)
                    {
                        string val = GetNullTerminatedStringAt(encoding, offset);
                        offset += CodeString(encoding, val).Length;
                        while ((offset < Data.Length) && (Data[offset] == 0))
                            offset++;
                        vals.Add(val);
                    }
                    values_ = vals.Count > 0 ? vals.ToArray() : new[] { string.Empty };
                }
            }
            else
            {
                if (FrameID[0] == 'W')
                    values_ = new [] { GetStringAt(ID3v2Util.ID3Encoding.ISO8859, 0).Split("\0".ToCharArray()).First() };
                else
                    values_ = new [] { GetStringAt((ID3v2Util.ID3Encoding)Data[0], 1).Split("\0".ToCharArray()).First() };
            }
        }

        public override void Encode()
        {
            if (FrameID.Length > 0 && FrameID[0] == 'W')
            {
                Data = ID3v2Util.ISO8859Encoding.GetBytes(values_[0]);
                return;
            }

            ID3v2Util.ID3Encoding enc;
            byte[] encoded;
            try
            {
                enc = ID3v2Util.ID3Encoding.ISO8859;
                encoded = CodeString(enc, string.Join("\0", values_));
            }
            catch
            {
                enc = (ID3v2Util.UseUTF8 && tag_.Version >= 4)
                    ? ID3v2Util.ID3Encoding.UTF8
                    : ID3v2Util.ID3Encoding.MarkedUnicode;
                encoded = CodeString(enc, string.Join("\0", values_));
            }

            Data = new byte[1 + encoded.Length];
            Data[0] = (byte)enc;
            Array.Copy(encoded, 0, Data, 1, encoded.Length);
        }

        public string Text
        {
            get
            {
                return values_[0];
            }
            set
            {
                Array.Resize(ref values_, 1);
                values_[0] = value;
                Encode();
            }
        }

        public IEnumerable<string> Values
        {
            get
            {
                return values_;
            }
            set
            {
                values_ = value.ToArray();
            }
        }

        /*public string Text
        {
            get
            {
                if (FrameID[0] == 'W')
                    return GetStringAt(ID3v2Util.ID3Encoding.ISO8859, 0).Split("\0".ToCharArray())[0];
                else
                    return GetStringAt((ID3v2Util.ID3Encoding)Data[0], 1).Split("\0".ToCharArray())[0];
            }
            set
            {
                byte[] data;
                if (FrameID[0] == 'W')
                {
                    data = ID3v2Util.ISO8859Encoding.GetBytes(value);
                }
                else
                {
                    try
                    {
                        data = CodeString(ID3v2Util.ID3Encoding.ISO8859, value);
                    }
                    catch
                    {
                        if (tag_.Version >= 4)
                            data = CodeString(ID3v2Util.ID3Encoding.UTF8, value);
                        else
                            data = CodeString(ID3v2Util.ID3Encoding.MarkedUnicode, value);
                    }
                }
                Data = data;
            }
        }*/

    }

    public class IdentifierFrame : ID3v2Frame
    {
        public IdentifierFrame(ID3v2Tag tag) : base(tag)
        {
            FrameID = "UFID";
        }

        public IdentifierFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        private string _key = "";
        private byte [] _value = new byte[0];

        public override void Decode()
        {
            base.Decode();
            try
            {
                _key = GetNullTerminatedStringAt(ID3v2Util.ID3Encoding.ISO8859, 0);
                int start = CodeString(ID3v2Util.ID3Encoding.ISO8859, _key).Length + 1;
                _value = new byte[Data.Length - start];
                Array.Copy(Data, start, _value, 0, Data.Length - start);
            }
            catch
            {
                _key = "";
                _value = new byte[0];
            }
        }

        public override void Encode()
        {
            byte[] k;
            k = CodeString(ID3v2Util.ID3Encoding.ISO8859, _key);
            Data = new byte[k.Length + 1 + _value.Length];
            Array.Copy(k, 0, Data, 0, k.Length);
            Array.Copy(_value, 0, Data, k.Length+1, _value.Length);
        }

        public string Key
        {
            get
            {
                return _key;
            }
            set
            {
                _key = value;
                Encode();
            }
        }

        public byte[] Value
        {
            get
            {
                return _value;
            }
            set
            {
                _value = value;
                Encode();
            }
        }

    }

    public class UserStringFrame : ID3v2Frame
    {
        public UserStringFrame(ID3v2Tag tag) : base(tag)
        {
            FrameID = "TXXX";
        }

        public UserStringFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        private string _key = "";
        private string _value = "";

        public override void Decode()
        {
            base.Decode();
            try
            {
                _key = GetNullTerminatedStringAt((ID3v2Util.ID3Encoding)Data[0], 1);
                if (FrameID[0] == 'W')
                    _value = GetStringAt(ID3v2Util.ID3Encoding.ISO8859,
                        CodeString((ID3v2Util.ID3Encoding)Data[0], _key + "\0").Length + 1).Split("\0".ToCharArray()).First();
                else
                    _value = GetStringAt((ID3v2Util.ID3Encoding)Data[0],
                        CodeString((ID3v2Util.ID3Encoding)Data[0], _key + "\0").Length + 1).Split("\0".ToCharArray()).First();
            }
            catch
            {
                _key = "";
                _value = "";
            }
        }

        public override void Encode()
        {
            // TBD: WXXX ISO8859-1 URL Encoding
            byte [] k, v;
            byte enc;
            try
            {
                k = CodeString(ID3v2Util.ID3Encoding.ISO8859, _key);
                v = CodeString(ID3v2Util.ID3Encoding.ISO8859, _value);
                enc = (byte)ID3v2Util.ID3Encoding.ISO8859;
            }
            catch
            {
                if ((ID3v2Util.UseUTF8) && (tag_.Version >= 4))
                {
                    k = CodeString(ID3v2Util.ID3Encoding.UTF8, _key);
                    v = CodeString(ID3v2Util.ID3Encoding.UTF8, _value);
                    enc = (byte)ID3v2Util.ID3Encoding.UTF8;
                }
                else
                {
                    k = CodeString(ID3v2Util.ID3Encoding.MarkedUnicode, _key);
                    v = CodeString(ID3v2Util.ID3Encoding.MarkedUnicode, _value);
                    enc = (byte)ID3v2Util.ID3Encoding.MarkedUnicode;
                }
            }

            // Layout: [encoding byte][key][null separator][value].
            // 16-bit encodings use a 2-byte null; ISO8859/UTF8 use a 1-byte null.
            int sep = (enc == (byte)ID3v2Util.ID3Encoding.MarkedUnicode
                    || enc == (byte)ID3v2Util.ID3Encoding.BEUnicode) ? 2 : 1;
            Data = new byte[1 + k.Length + sep + v.Length];
            Data[0] = enc;
            Array.Copy(k, 0, Data, 1, k.Length);
            Array.Copy(v, 0, Data, 1 + k.Length + sep, v.Length);
        }

        public string Key
        {
            get
            {
                return _key;
            }
            set
            {
                _key = value;
                Encode();
            }
        }

        public string Value
        {
            get
            {
                return _value;
            }
            set
            {
                _value = value;
                Encode();
            }
        }

    }

    public class CommentFrame : ID3v2Frame
    {

        public CommentFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        public CommentFrame(ID3v2Tag tag) : base(tag)
        {
            FrameID = "COMM";
        }

        private string _lang = "";
        private string _key = "";
        private string _value = "";

        public override void Encode()
        {
            throw new NotImplementedException();
        }

        public override void Decode()
        {
            base.Decode();
            try
            {
                _lang = ID3v2Util.ISO8859Encoding.GetString(Data, 1, 3);
                _key = GetNullTerminatedStringAt((ID3v2Util.ID3Encoding)Data[0], 4);
                _value = GetStringAt((ID3v2Util.ID3Encoding)Data[0],
                    CodeString((ID3v2Util.ID3Encoding)Data[0], _key + "\0").Length + 4).Split("\0".ToCharArray()).First();
            }
            catch
            {
                _lang = "";
                _key = "";
                _value = "";
            }
        }

        public string Language
        {
            get
            {
                 return _lang;
            }
            set
            {
                _lang = value;
                Encode();
            }
        }

        public string Key
        {
            get
            {
                return _key;
            }
            set
            {
                _key = value;
                Encode();
            }
        }

        public string Value
        {
            get
            {
                return _value;
            }
            set
            {
                _value = value;
                Encode();
            }
        }
            
    }

    public class PictureFrame : ID3v2Frame, IMetadataImage
    {
        private ID3v2Util.APICType _type;
        private string _mimetype;
        private string _description;
        private byte[] _picdata;
        private int _picdataoffset = -1; // offset of the picture payload within Data; -1 = none
        private int _width;
        private int _height;
        private bool _dimsComputed;

        // The picture payload is copied out of Data lazily: the eager copy doubled the memory
        // traffic of every cover-art frame on a scan even when nothing looked at the image.
        private byte[] PicData
        {
            get
            {
                if (_picdata == null && _picdataoffset >= 0)
                {
                    _picdata = new byte[Data.Length - _picdataoffset];
                    Array.Copy(Data, _picdataoffset, _picdata, 0, _picdata.Length);
                }
                return _picdata;
            }
        }

        // Image dimensions are parsed lazily (a library scan that only wants text tags
        // shouldn't pay to decode every embedded cover) and probed in place so they don't
        // force the payload copy either.
        private void EnsureDimensions()
        {
            if (_dimsComputed) return;
            _dimsComputed = true;
            ReadOnlySpan<byte> picdata = _picdata != null
                ? _picdata
                : (_picdataoffset >= 0 ? Data.AsSpan(_picdataoffset) : default);
            if (!picdata.IsEmpty)
            {
                var img = ImageFile.GetImageDimensions(picdata);
                _width = img.Width;
                _height = img.Height;
            }
        }

        string IMetadataImage.Description => string.IsNullOrWhiteSpace(_description) ? _type.ToString() : _description;
        string IMetadataImage.Category => _type.ToString();
        string IMetadataImage.ImageType => _mimetype;
        int IMetadataImage.Width { get { EnsureDimensions(); return _width; } }
        int IMetadataImage.Height { get { EnsureDimensions(); return _height; } }
        int IMetadataImage.Size => _picdata?.Length ?? (_picdataoffset >= 0 ? Data.Length - _picdataoffset : 0);
        byte[] IMetadataImage.Data => PicData;
      
        public PictureFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        public PictureFrame(ID3v2Tag tag) : base(tag)
        {
            FrameID = "APIC";
        }

        public override void Decode()
        {
            base.Decode();
            if (Data.Length == 0)
                return;
            ID3v2Util.ID3Encoding encoding = (ID3v2Util.ID3Encoding)Data[0];
            int codelen;
            if (tag_.Version == 2)
            {
                _mimetype = Encoding.ASCII.GetString(Data, 1, 3);
                codelen = 3;
            }
            else
            {
                _mimetype = GetNullTerminatedStringAt(ID3v2Util.ID3Encoding.ISO8859, 1);
                codelen = CodeString(ID3v2Util.ID3Encoding.ISO8859, _mimetype + "\0").Length;
            }
            if ((_mimetype.ToLower() == "jpeg") || (_mimetype.ToLower() == "jpg"))
                _mimetype = "image/jpeg";
            if (_mimetype.ToLower() == "png")
                _mimetype = "image/png";
            if (_mimetype.ToLower() == "bmp")
                _mimetype = "image/bmp";
            if (_mimetype.ToLower() == "gif")
                _mimetype = "image/gif";
            _type = (ID3v2Util.APICType)Data[codelen + 1];
            _description = GetNullTerminatedStringAt(encoding, codelen + 2);
            int codelen2 = CodeString(encoding, _description + "\0").Length;
            _picdataoffset = Math.Min(codelen + codelen2 + 2, Data.Length);
            _picdata = null;
            _dimsComputed = false;
        }

        public override void Encode()
        {
            // Materialize the payload before Data is rebuilt underneath it.
            byte[] picdata = PicData;
            if (picdata == null) return;
            byte[] mime = ID3v2Util.ISO8859Encoding.GetBytes(_mimetype ?? "image/jpeg");
            byte[] desc = ID3v2Util.ISO8859Encoding.GetBytes(_description ?? "");
            int totalLen = 1 + mime.Length + 1 + 1 + desc.Length + 1 + picdata.Length;
            Data = new byte[totalLen];
            int pos = 0;
            Data[pos++] = (byte)ID3v2Util.ID3Encoding.ISO8859;
            Array.Copy(mime, 0, Data, pos, mime.Length); pos += mime.Length;
            Data[pos++] = 0;
            Data[pos++] = (byte)_type;
            Array.Copy(desc, 0, Data, pos, desc.Length); pos += desc.Length;
            Data[pos++] = 0;
            Array.Copy(picdata, 0, Data, pos, picdata.Length);
        }

        public ID3v2Util.APICType Type
        {
            get => _type;
            set { _type = value; Encode(); }
        }

        public string MimeType
        {
            get => _mimetype;
            set { _mimetype = value; Encode(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; Encode(); }
        }

        public byte[] PictureData
        {
            get => PicData;
            set { _picdata = value; _picdataoffset = -1; _dimsComputed = false; Encode(); }
        }

        public string Hash
        {
            get;
            protected set;
        }

        public void HashImage(System.Security.Cryptography.HashAlgorithm hash)
        {
            // Hash straight over the payload slice of Data: materializing PicData just to
            // hash it would copy every cover on every scan.
            Hash = Convert.ToBase64String(_picdata != null
                ? hash.ComputeHash(_picdata)
                : hash.ComputeHash(Data, _picdataoffset, Data.Length - _picdataoffset));
        }

    }

    public class ID3v2Tag : TagBase, IArtworkWriter
    {

        protected int _headerversion = 0;
        private int _flags = 0;
        protected int _tagsize = 0;
        private List<ID3v2Frame> _frames = new List<ID3v2Frame>();
        protected string _filename = null;
 
        public List<ID3v2Frame> Frames
        {
            get
            {
                return _frames;
            }
        }

        public override string TagType
        {
            get
            {
                return "ID3v2" + _headerversion.ToString();
            }
        }

        public int Version => _headerversion;
        public int Flags => _flags;

        protected void WriteHeader(FileStream s)
        {
            byte[] header = new byte[10];
            header[0] = 0x49;
            header[1] = 0x44;
            header[2] = 0x33;
            header[3] = (byte)_headerversion;
            header[4] = 0x00;
            header[5] = 0x00;
            header[6] = (byte)((_tagsize >> 21) & 0x7f);
            header[7] = (byte)((_tagsize >> 14) & 0x7f);
            header[8] = (byte)((_tagsize >> 7) & 0x7f);
            header[9] = (byte)(_tagsize & 0x7f);
            s.Write(header, 0, 10);
        }

        #region IMetadataProvider Properties

        public override IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata()
        {
            BuildFrameIndex();
            try
            {
                foreach (var mapping in (Version == 2) ? ID3v2Util.ActionMappingsv22 : ID3v2Util.ActionMappingsv23v24)
                {
                    object[] values = Array.Empty<object>();
                    // Absent frames return empty (the common case); the catch only guards
                    // against genuinely malformed frame data.
                    try
                    {
                        values = mapping.Value(this, TagAction.Get);
                    }
                    catch
                    {
                        // No value
                    }
                    foreach (var v in values)
                        yield return KeyValuePair.Create(mapping.Key, v.ToString());
                }
            }
            finally
            {
                ClearFrameIndex();
            }
        }

        public override IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var field in GetKnownMetadata())
                yield return KeyValuePair.Create(field.Key.ToString(), field.Value);
        }

        public override IEnumerable<IMetadataImage> GetImageMetadata()
        {
            foreach (var frame in _frames)
                if (frame is PictureFrame)
                    yield return frame as IMetadataImage;
        }

        #endregion

        public void SetString(string frameId, string value)
        {
            var existing = _frames.OfType<TextFrame>().FirstOrDefault(f => f.FrameID == frameId);
            if (existing != null)
            {
                existing.Text = value;
                return;
            }
            _frames.RemoveAll(f => f.FrameID == frameId);
            var frame = new TextFrame(this) { FrameID = frameId };
            frame.Text = value;
            _frames.Add(frame);
        }

        public void SetUserString(string key, string value)
        {
            string txxx = (_headerversion == 2) ? "TXX" : "TXXX";
            var existing = _frames.OfType<UserStringFrame>()
                                  .FirstOrDefault(f => f.FrameID == txxx && f.Key == key);
            if (existing != null)
            {
                existing.Value = value;
                return;
            }
            var frame = new UserStringFrame(this) { FrameID = txxx };
            frame.Key = key;
            frame.Value = value;
            _frames.Add(frame);
        }

        public void SetAttachedImage(ID3v2Util.APICType pictureType, string mimeType,
                                     string description, byte[] data)
        {
            string apic = (_headerversion == 2) ? "PIC" : "APIC";
            var existing = _frames.OfType<PictureFrame>()
                                  .FirstOrDefault(f => f.FrameID == apic && f.Type == pictureType);
            if (existing != null)
            {
                existing.MimeType = mimeType;
                existing.Description = description;
                existing.PictureData = data;
                return;
            }
            var frame = new PictureFrame(this) { FrameID = apic };
            frame.Type = pictureType;
            frame.MimeType = mimeType;
            frame.Description = description;
            frame.PictureData = data;
            _frames.Add(frame);
        }

        // IArtworkWriter: uniform front-cover write across formats. Delegates to SetAttachedImage
        // (which replaces an existing front cover in place) / removes all picture frames.
        public void SetFrontCover(byte[] imageData, string mimeType)
        {
            if (imageData == null || imageData.Length == 0)
            {
                RemoveImages();
                return;
            }
            SetAttachedImage(ID3v2Util.APICType.FrontCover, mimeType, "", imageData);
        }

        public void RemoveImages()
        {
            _frames.RemoveAll(f => f is PictureFrame);
        }

        public void SetImages(IReadOnlyList<ArtworkImage> images)
        {
            RemoveImages();
            foreach (var img in images)
                SetAttachedImage(img.Type, img.MimeType, img.Description ?? "", img.Data);
        }

        public void SetField(TagFields field, string value)
        {
            if (!ID3v2Util.ActionMappingsv23v24.ContainsKey(field))
                throw new ArgumentException($"Unsupported tag field for ID3: {field}");

            // Special compound fields stored as "N/total" in a single frame
            if (field == TagFields.TrackNumber || field == TagFields.TotalTracks)
            {
                var tf = _frames.OfType<TextFrame>().FirstOrDefault(f => f.FrameID == "TRCK");
                string[] parts = (tf?.Text ?? "").Split('/');
                string num = parts.Length >= 1 ? parts[0] : "";
                string tot = parts.Length >= 2 ? parts[1] : "";
                if (field == TagFields.TrackNumber) num = value ?? "";
                else tot = value ?? "";
                string combined = string.IsNullOrEmpty(tot) ? num : num + "/" + tot;
                if (string.IsNullOrEmpty(combined))
                    _frames.RemoveAll(f => f.FrameID == "TRCK");
                else
                    SetString("TRCK", combined);
                return;
            }
            if (field == TagFields.DiscNumber || field == TagFields.TotalDiscs)
            {
                var tf = _frames.OfType<TextFrame>().FirstOrDefault(f => f.FrameID == "TPOS");
                string[] parts = (tf?.Text ?? "").Split('/');
                string num = parts.Length >= 1 ? parts[0] : "";
                string tot = parts.Length >= 2 ? parts[1] : "";
                if (field == TagFields.DiscNumber) num = value ?? "";
                else tot = value ?? "";
                string combined = string.IsNullOrEmpty(tot) ? num : num + "/" + tot;
                if (string.IsNullOrEmpty(combined))
                    _frames.RemoveAll(f => f.FrameID == "TPOS");
                else
                    SetString("TPOS", combined);
                return;
            }
            if (field == TagFields.MovementNumber || field == TagFields.MovementTotal)
            {
                var tf = _frames.OfType<TextFrame>().FirstOrDefault(f => f.FrameID == "MVIN");
                string[] parts = (tf?.Text ?? "").Split('/');
                string num = parts.Length >= 1 ? parts[0] : "";
                string tot = parts.Length >= 2 ? parts[1] : "";
                if (field == TagFields.MovementNumber) num = value ?? "";
                else tot = value ?? "";
                string combined = string.IsNullOrEmpty(tot) ? num : num + "/" + tot;
                if (string.IsNullOrEmpty(combined))
                    _frames.RemoveAll(f => f.FrameID == "MVIN");
                else
                    SetString("MVIN", combined);
                return;
            }
            if (field == TagFields.Date)
            {
                if (value == null) { _frames.RemoveAll(f => f.FrameID == "TDRC" || f.FrameID == "TYER"); return; }
                if (_headerversion >= 4) SetString("TDRC", value);
                else SetString("TYER", value.Length >= 4 ? value.Substring(0, 4) : value);
                return;
            }

            switch (field)
            {
                case TagFields.Album:            if (value == null) _frames.RemoveAll(f => f.FrameID == "TALB"); else SetString("TALB", value); break;
                case TagFields.AlbumArtist:      if (value == null) _frames.RemoveAll(f => f.FrameID == "TPE2"); else SetString("TPE2", value); break;
                case TagFields.AlbumArtistSort:  if (value == null) _frames.RemoveAll(f => f.FrameID == "TSO2"); else SetString("TSO2", value); break;
                case TagFields.AlbumSort:        if (value == null) _frames.RemoveAll(f => f.FrameID == "TSOA"); else SetString("TSOA", value); break;
                case TagFields.Artist:           if (value == null) _frames.RemoveAll(f => f.FrameID == "TPE1"); else SetString("TPE1", value); break;
                case TagFields.ArtistSort:       if (value == null) _frames.RemoveAll(f => f.FrameID == "TSOP"); else SetString("TSOP", value); break;
                case TagFields.BPM:              if (value == null) _frames.RemoveAll(f => f.FrameID == "TBPM"); else SetString("TBPM", value); break;
                case TagFields.Compilation:      if (value == null) _frames.RemoveAll(f => f.FrameID == "TCMP"); else SetString("TCMP", value); break;
                case TagFields.Composer:         if (value == null) _frames.RemoveAll(f => f.FrameID == "TCOM"); else SetString("TCOM", value); break;
                case TagFields.ComposerSort:     if (value == null) _frames.RemoveAll(f => f.FrameID == "TSOC"); else SetString("TSOC", value); break;
                case TagFields.Conductor:        if (value == null) _frames.RemoveAll(f => f.FrameID == "TPE3"); else SetString("TPE3", value); break;
                case TagFields.Copyright:        if (value == null) _frames.RemoveAll(f => f.FrameID == "TCOP"); else SetString("TCOP", value); break;
                case TagFields.DiscSubtitle:     if (value == null) _frames.RemoveAll(f => f.FrameID == "TSST"); else SetString("TSST", value); break;
                case TagFields.EncodedBy:        if (value == null) _frames.RemoveAll(f => f.FrameID == "TENC"); else SetString("TENC", value); break;
                case TagFields.EncoderSettings:  if (value == null) _frames.RemoveAll(f => f.FrameID == "TSSE"); else SetString("TSSE", value); break;
                case TagFields.Genre:            if (value == null) _frames.RemoveAll(f => f.FrameID == "TCON"); else SetString("TCON", value); break;
                case TagFields.Grouping:         if (value == null) _frames.RemoveAll(f => f.FrameID == "TIT1"); else SetString("TIT1", value); break;
                case TagFields.Key:              if (value == null) _frames.RemoveAll(f => f.FrameID == "TKEY"); else SetString("TKEY", value); break;
                case TagFields.ISRC:             if (value == null) _frames.RemoveAll(f => f.FrameID == "TSRC"); else SetString("TSRC", value); break;
                case TagFields.Language:         if (value == null) _frames.RemoveAll(f => f.FrameID == "TLAN"); else SetString("TLAN", value); break;
                case TagFields.Lyrics:           if (value == null) _frames.RemoveAll(f => f.FrameID == "USLT"); else SetString("USLT", value); break;
                case TagFields.Media:            if (value == null) _frames.RemoveAll(f => f.FrameID == "TMED"); else SetString("TMED", value); break;
                case TagFields.Mood:             if (value == null) _frames.RemoveAll(f => f.FrameID == "TMOO"); else SetString("TMOO", value); break;
                case TagFields.Movement:         if (value == null) _frames.RemoveAll(f => f.FrameID == "MVNM"); else SetString("MVNM", value); break;
                case TagFields.OriginalAlbum:    if (value == null) _frames.RemoveAll(f => f.FrameID == "TOAL"); else SetString("TOAL", value); break;
                case TagFields.OriginalArtist:   if (value == null) _frames.RemoveAll(f => f.FrameID == "TOPE"); else SetString("TOPE", value); break;
                case TagFields.OriginalFileName: if (value == null) _frames.RemoveAll(f => f.FrameID == "TOFN"); else SetString("TOFN", value); break;
                case TagFields.Rating:           if (value == null) _frames.RemoveAll(f => f.FrameID == "POPM"); else SetString("POPM", value); break;
                case TagFields.Label:            if (value == null) _frames.RemoveAll(f => f.FrameID == "TPUB"); else SetString("TPUB", value); break;
                case TagFields.Remixer:          if (value == null) _frames.RemoveAll(f => f.FrameID == "TPE4"); else SetString("TPE4", value); break;
                case TagFields.Subtitle:         if (value == null) _frames.RemoveAll(f => f.FrameID == "TIT3"); else SetString("TIT3", value); break;
                case TagFields.Title:            if (value == null) _frames.RemoveAll(f => f.FrameID == "TIT2"); else SetString("TIT2", value); break;
                case TagFields.TitleSort:        if (value == null) _frames.RemoveAll(f => f.FrameID == "TSOT"); else SetString("TSOT", value); break;
                case TagFields.Website:          if (value == null) _frames.RemoveAll(f => f.FrameID == "WOAR"); else SetString("WOAR", value); break;
                case TagFields.AcoustID_ID:             if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "Acoustid Id"); else SetUserString("Acoustid Id", value); break;
                case TagFields.AcoustID_Fingerprint:    if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "Acoustid Fingerprint"); else SetUserString("Acoustid Fingerprint", value); break;
                case TagFields.Artists:                 if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "Artists"); else SetUserString("Artists", value); break;
                case TagFields.ASIN:                    if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "ASIN"); else SetUserString("ASIN", value); break;
                case TagFields.Barcode:                 if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "BARCODE"); else SetUserString("BARCODE", value); break;
                case TagFields.CatalogNumber:           if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "CATALOGNUMBER"); else SetUserString("CATALOGNUMBER", value); break;
                case TagFields.MusicBrainz_ArtistID:    if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Artist Id"); else SetUserString("MusicBrainz Artist Id", value); break;
                case TagFields.MusicBrainz_DiscID:      if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Disc Id"); else SetUserString("MusicBrainz Disc Id", value); break;
                case TagFields.MusicBrainz_OriginalArtistID: if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Original Artist Id"); else SetUserString("MusicBrainz Original Artist Id", value); break;
                case TagFields.MusicBrainz_OriginalAlbumID:  if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Original Album Id"); else SetUserString("MusicBrainz Original Album Id", value); break;
                case TagFields.MusicBrainz_AlbumArtistID:    if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Album Artist Id"); else SetUserString("MusicBrainz Album Artist Id", value); break;
                case TagFields.MusicBrainz_ReleaseGroupID:   if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Release Group Id"); else SetUserString("MusicBrainz Release Group Id", value); break;
                case TagFields.MusicBrainz_AlbumID:     if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Album Id"); else SetUserString("MusicBrainz Album Id", value); break;
                case TagFields.MusicBrainz_TrackID:     if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Release Track Id"); else SetUserString("MusicBrainz Release Track Id", value); break;
                case TagFields.MusicBrainz_WorkID:      if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Work Id"); else SetUserString("MusicBrainz Work Id", value); break;
                case TagFields.ReleaseCountry:          if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Album Release Country"); else SetUserString("MusicBrainz Album Release Country", value); break;
                case TagFields.ReleaseStatus:           if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Album Status"); else SetUserString("MusicBrainz Album Status", value); break;
                case TagFields.ReleaseType:             if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "MusicBrainz Album Type"); else SetUserString("MusicBrainz Album Type", value); break;
                case TagFields.ReplayGain_Album_Gain:   if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "REPLAYGAIN_ALBUM_GAIN"); else SetUserString("REPLAYGAIN_ALBUM_GAIN", value); break;
                case TagFields.ReplayGain_Album_Peak:   if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "REPLAYGAIN_ALBUM_PEAK"); else SetUserString("REPLAYGAIN_ALBUM_PEAK", value); break;
                case TagFields.ReplayGain_Album_Range:  if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "REPLAYGAIN_ALBUM_RANGE"); else SetUserString("REPLAYGAIN_ALBUM_RANGE", value); break;
                case TagFields.ReplayGain_Reference_Loudness: if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "REPLAYGAIN_REFERENCE_LOUDNESS"); else SetUserString("REPLAYGAIN_REFERENCE_LOUDNESS", value); break;
                case TagFields.ReplayGain_Track_Gain:   if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "REPLAYGAIN_TRACK_GAIN"); else SetUserString("REPLAYGAIN_TRACK_GAIN", value); break;
                case TagFields.ReplayGain_Track_Peak:   if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "REPLAYGAIN_TRACK_PEAK"); else SetUserString("REPLAYGAIN_TRACK_PEAK", value); break;
                case TagFields.ReplayGain_Track_Range:  if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "REPLAYGAIN_TRACK_RANGE"); else SetUserString("REPLAYGAIN_TRACK_RANGE", value); break;
                case TagFields.Script:                  if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "SCRIPT"); else SetUserString("SCRIPT", value); break;
                case TagFields.ShowMovement:            if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "SHOWMOVEMENT"); else SetUserString("SHOWMOVEMENT", value); break;
                case TagFields.Work:                    if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "WORK"); else SetUserString("WORK", value); break;
                case TagFields.Writer:                  if (value == null) _frames.RemoveAll(f => f is UserStringFrame u && u.Key == "Writer"); else SetUserString("Writer", value); break;
            }
        }

        public void RemoveField(TagFields field) => SetField(field, null);

        public void Save(string outputPath = null)
        {
            string target = outputPath ?? _filename
                ?? throw new InvalidOperationException("No filename associated with this tag.");

            int frameHeaderSize = (_headerversion == 2) ? 6 : 10;
            int size = 0;
            foreach (ID3v2Frame f in _frames)
                size += frameHeaderSize + f.Data.Length;

            int padSize = (size <= _tagsize) ? (_tagsize - size) : 1024;
            byte[] pad = new byte[padSize];

            if (size <= _tagsize && target == _filename)
            {
                using FileStream s = new FileStream(target, FileMode.Open, FileAccess.ReadWrite);
                s.Seek(0, SeekOrigin.Begin);
                WriteHeader(s);
                foreach (ID3v2Frame f in _frames)
                    f.Write(s);
                s.Write(pad, 0, pad.Length);
            }
            else
            {
                string tempPath = target + ".tmp~";
                try
                {
                    string sourcePath = _filename ?? target;
                    using FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
                    using FileStream dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write);

                    long oldTagEnd = (_tagsize == 0) ? 0 : (_tagsize + 10);
                    source.Seek(oldTagEnd, SeekOrigin.Begin);

                    _tagsize = size + padSize;
                    if (_headerversion < 3) _headerversion = 3;
                    WriteHeader(dest);
                    foreach (ID3v2Frame f in _frames)
                        f.Write(dest);
                    dest.Write(pad, 0, pad.Length);

                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        dest.Write(buffer, 0, read);
                }
                catch
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    throw;
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tempPath, target);
                _filename = target;
            }
        }

        // Transient lookup tables populated for the duration of a GetKnownMetadata() call so the
        // ~70 field mappings don't each rescan every frame (was O(fields x frames)). They are
        // null outside that call; mutation happens only via SetField, never during enumeration.
        private Dictionary<string, List<ID3v2Frame>> _frameIndex;
        private Dictionary<(string FrameID, string Key), UserStringFrame> _userIndex;
        private Dictionary<(string FrameID, string Key), IdentifierFrame> _identIndex;

        private void BuildFrameIndex()
        {
            _frameIndex = new Dictionary<string, List<ID3v2Frame>>();
            _userIndex = new Dictionary<(string, string), UserStringFrame>();
            _identIndex = new Dictionary<(string, string), IdentifierFrame>();
            foreach (var f in _frames)
            {
                if (!_frameIndex.TryGetValue(f.FrameID, out var list))
                    _frameIndex[f.FrameID] = list = new List<ID3v2Frame>();
                list.Add(f);
                if (f is UserStringFrame u)
                    _userIndex.TryAdd((f.FrameID, u.Key), u);
                else if (f is IdentifierFrame id)
                    _identIndex.TryAdd((f.FrameID, id.Key), id);
            }
        }

        private void ClearFrameIndex()
        {
            _frameIndex = null;
            _userIndex = null;
            _identIndex = null;
        }

        public ID3v2Frame FindFrame(string frame)
        {
            // First match (not Single): duplicate frame IDs shouldn't throw and silently drop the field.
            if (_frameIndex != null)
                return _frameIndex.TryGetValue(frame, out var l) ? l[0] : null;
            return _frames.FirstOrDefault(frm => frm.FrameID == frame);
        }

        internal UserStringFrame FindUserStringFrame(string frameId, string key)
        {
            if (_userIndex != null)
                return _userIndex.TryGetValue((frameId, key), out var f) ? f : null;
            return _frames.OfType<UserStringFrame>().FirstOrDefault(f => f.FrameID == frameId && f.Key == key);
        }

        internal IdentifierFrame FindIdentifierFrame(string frameId, string key)
        {
            if (_identIndex != null)
                return _identIndex.TryGetValue((frameId, key), out var f) ? f : null;
            return _frames.OfType<IdentifierFrame>().FirstOrDefault(f => f.FrameID == frameId && f.Key == key);
        }

        protected void ReadTag(Stream s)
        {
            if (s is FileStream fs)
                _filename = fs.Name;
            bool doclose = false;
            BinaryReader r = new BinaryReader(s, Encoding.ASCII, true);
            byte[] header = r.ReadBytes(10);
            _headerversion = header[3];

            if (Encoding.ASCII.GetString(header, 0, 3) == "ID3")
            {
                _flags = header[5];
                _tagsize = header[6];
                _tagsize = (_tagsize * 128) + header[7];
                _tagsize = (_tagsize * 128) + header[8];
                _tagsize = (_tagsize * 128) + header[9];
                if ((header[3] == 0x03) || (header[3] == 0x04))
                {
                    if (header[5] != 0)
                    {
                        if (((header[5] & 0x80) == 0x80) && (header[3] == 0x03)) // Unsync whole tag if v23, frame data v24
                        {
                            // Unsync
                            List<byte> unsync = new List<byte>();
                            byte[] b = r.ReadBytes(_tagsize);
                            for (int i = 0; i < b.Length - 1; i++)
                            {
                                if ((b[i] == 0xff) && (b[i + 1] == 0x00))
                                {
                                    unsync.Add(0xff);
                                    i++;
                                }
                                else
                                    unsync.Add(b[i]);
                            }
                            unsync.Add(b[b.Length - 1]);
                            unsync.Add(0);
                            MemoryStream ms = new MemoryStream(unsync.ToArray());
                            r = new BinaryReader(ms);
                            doclose = true;
                        }
                        else if (((header[5] & 0x80) == 0x80) && (header[3] == 0x04))
#pragma warning disable CS0642 // Possible mistaken empty statement
                            ;
#pragma warning restore CS0642 // Possible mistaken empty statement
                        else
                            throw new Exception("Unsupported ID3v2 Header Features");
                    }

                    byte[] frame = r.ReadBytes(10);
                    int offset = 10;
                    while ((frame[0] != 0) && (offset < _tagsize))
                    {
                        ID3v2Frame f = new ID3v2Frame(this);
                        f.FrameID = ID3v2Util.ISO8859Encoding.GetString(frame, 0, 4);
                        int framesize = frame[4];
                        if (_headerversion >= 4) // SyncSafe 2.4 only
                        {
                            framesize = (framesize * 128) + frame[5];
                            framesize = (framesize * 128) + frame[6];
                            framesize = (framesize * 128) + frame[7];
                        }
                        else
                        {
                            framesize = (framesize * 256) + frame[5];
                            framesize = (framesize * 256) + frame[6];
                            framesize = (framesize * 256) + frame[7];
                        }
                        f.Flags = (((int)frame[8]) << 8) + (int)frame[9];
                        f.Data = r.ReadBytes(framesize);

                        if ((f.FrameID == "TXXX") || (f.FrameID == "WXXX"))
                            _frames.Add(new UserStringFrame(f));
                        else if ((f.FrameID == "UFID") || (f.FrameID == "PRIV"))
                            _frames.Add(new IdentifierFrame(f));
                        else if (f.FrameID == "APIC")
                            _frames.Add(new PictureFrame(f));
                        else if (f.FrameID == "COMM")
                            _frames.Add(new CommentFrame(f));
                        else if (f.FrameID == "MCDI")
                            _frames.Add(f);
                        else if ((f.FrameID[0] == 'T') || (f.FrameID[0] == 'W') || (f.FrameID[0] == 'M'))
                            _frames.Add(new TextFrame(f));
                        else
                            _frames.Add(f);

                        offset += framesize;
                        if (offset < _tagsize)
                        {
                            frame = r.ReadBytes(10);
                            offset += 10;
                        }
                    }
                }
                else
                {
                    if (_headerversion != 0x02)
                        throw new Exception("Invalid ID3v2 Version");

                    // Load Legacy V2 Header
                    byte[] frame = r.ReadBytes(6);
                    int offset = 6;
                    while ((frame[0] != 0) && (offset < _tagsize))
                    {
                        ID3v2Frame f = new ID3v2Frame(this);
                        f.FrameID = ID3v2Util.ISO8859Encoding.GetString(frame, 0, 3);
                        int framesize = frame[3];
                        framesize = (framesize * 256) + frame[4];
                        framesize = (framesize * 256) + frame[5];
                        f.Data = r.ReadBytes(framesize);

                        if ((f.FrameID == "TXX") || (f.FrameID == "WXX"))
                            _frames.Add(new UserStringFrame(f));
                        else if (f.FrameID == "PIC")
                            _frames.Add(new PictureFrame(f));
                        else if ((f.FrameID == "UFI") || (f.FrameID == "PRI"))
                            _frames.Add(new IdentifierFrame(f));
                        else if (f.FrameID == "COM")
                            _frames.Add(new CommentFrame(f));
                        else if ((f.FrameID[0] == 'T') || (f.FrameID[0] == 'W'))
                            _frames.Add(new TextFrame(f));
                        else
                            _frames.Add(f);

                        offset += framesize;
                        if (offset < _tagsize)
                        {
                            frame = r.ReadBytes(6);
                            offset += 6;
                        }
                    }

                }
            }
            if (doclose)
                r.Close();
            ParseStandardFields();
        }

        public ID3v2Tag()
        {
        }

    }

    public class MP3File : ID3v2Tag, ICodecProvider, IMediaFile, IMetadataWriter
    {
        private static readonly uint[,,] _bitrates = {
            { { 0, 32000, 64000, 96000, 128000, 160000, 192000, 224000, 256000, 288000, 320000, 352000, 384000, 416000, 448000, 0 },
            { 0, 32000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 160000, 192000, 224000, 256000, 320000, 384000, 0 },
            { 0, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 160000, 192000, 224000, 256000, 320000, 0 } },
            { { 0, 32000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 144000, 160000, 176000, 192000, 224000, 256000, 0 },
            { 0, 8000, 16000, 24000, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 144000, 160000, 0 },
            { 0, 8000, 16000, 24000, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 144000, 160000, 0 } },
            { { 0, 32000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 144000, 160000, 176000, 192000, 224000, 256000, 0 },
            { 0, 8000, 16000, 24000, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 144000, 160000, 0 },
            { 0, 8000, 16000, 24000, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 144000, 160000, 0 } } };
        
        private static readonly uint[,] _samplerates = { { 44100, 48000, 32000, 0 }, { 22050, 24000, 16000, 0 }, { 11025, 12000, 8000, 0 } };
        private static readonly int[,] _samplesperframe = { { 384, 1152, 1152 }, { 384, 1152, 576 }, { 384, 1152, 576 } };
        private static readonly uint[] _channels = { 2, 2, 2, 1 };
        private static readonly int[] _sideinfolen = { 32, 32, 32, 17 };

        public IEnumerable<ICodecProvider> Codecs
        {
            get
            {
                yield return this;
            }
        }

        public IEnumerable<IMetadataProvider> Tags
        {
            get
            {
                yield return this;
            }
        }


        public MP3File(string filename)
        {
            using (FileStream s = Tools.OpenReadSequential(filename))
            {
                ReadTag(s);
                // FileStream.Length is a syscall; don't pay it once per scanned byte while
                // hunting for the first frame sync.
                long length = s.Length;
                for (; ; )
                {
                    int b0 = -1, b1 = -1, b5 = -1;
                    while (s.Position < length)
                    {
                        b1 = b5 = s.ReadByte();
                        if ((b0 == 0xff) && ((b1 & 0xe0) == 0xe0))
                            break;
                        b0 = b1;
                    }
                    if (s.Position >= length)
                        return;
                    int ver = (b1 & 0x8) == 0x8 ? 0 : 1;
                    if ((b1 & 0x10) == 0x00)
                    {
                        if (ver == 0)
                            ver = 2;
                        else
                        {
                            s.Seek(-1, SeekOrigin.Current);
                            b0 = b1 = -1;
                            continue;
                        }
                    }
                    int layer = -1;
                    if ((b1 & 0x6) == 0x2)
                        layer = 2;
                    else if ((b1 & 0x6) == 0x4)
                        layer = 1;
                    else if ((b1 & 0x6) == 0x6)
                        layer = 0;
                    if (layer == -1)
                    {
                        s.Seek(-1, SeekOrigin.Current);
                        b0 = b1 = -1;
                        continue;
                    }
                    long datalength = length - s.Position + 2;
                    int b2 = s.ReadByte();
                    int b3 = s.ReadByte();
                    uint bitrate = _bitrates[ver, layer, b2 >> 4];
                    AverageBitrate = bitrate;
                    Samplerate = _samplerates[ver, (b2 >> 2) & 3];
                    Channels = _channels[(b3 >> 6) & 3];
                    int samplesperframe = _samplesperframe[ver, layer];

                    if ((Samplerate == 0) || (AverageBitrate == 0))
                    {
                        s.Seek(-3, SeekOrigin.Current);
                        b0 = b1 = -1;
                        continue;
                    }
                    int sideinfolen = _sideinfolen[(b3 >> 6) & 3];
                    int framesize = (int)((double)samplesperframe * (double)bitrate / (double)Samplerate / 8.0);
                    if ((b2 & 8) == 8)
                        framesize += 1;
                    if (layer == 0)
                        framesize *= 4;

                    if (framesize < 4)
                    {
                        s.Seek(-3, SeekOrigin.Current);
                        b0 = b1 = -1;
                        continue;
                    }

                    byte[] frame = new byte[framesize - 4];
                    s.ReadExactly(frame);

                    try
                    {
                        int offset = sideinfolen;
                        string id = Encoding.ASCII.GetString(frame, offset, 4);
                        if ((id == "Xing" || (id == "Info")))
                        {
                            uint frames = 0;
                            uint bytes = 0;
                            offset += 4;
                            uint flags = Tools.UInt32AtBE(frame, offset);
                            offset += 4;
                            if ((flags & 1) == 1)
                            {
                                frames = Tools.UInt32AtBE(frame, offset);
                                offset += 4;
                                AverageBitrate = (uint)(datalength / (frames * samplesperframe / Samplerate) * 8);
                            }
                            if ((flags & 2) == 2)
                            {
                                bytes = Tools.UInt32AtBE(frame, offset);
                                offset += 4;
                            }
                            int decoderdelay = 0;
                            int endpadding = 0;
                            try
                            {
                                decoderdelay = (frame[141 + sideinfolen] << 4) | (frame[142 + sideinfolen] >> 4);
                                endpadding = ((frame[142 + sideinfolen] & 0xf) << 8) | frame[143 + sideinfolen];
                            }
                            catch
                            {
                                // Short frame, no pad information
                            }
                            DurationInFrames = (uint)((samplesperframe * frames - decoderdelay - endpadding) / (Samplerate / 75));
                        }
                        else if (Encoding.ASCII.GetString(frame, 32, 4) == "VBRI")
                        {
                            offset += 10;
                            uint frames = Tools.UInt32AtBE(frame, offset);
                            AverageBitrate = (uint)(datalength / (frames * 1152 / Samplerate) * 8);
                            DurationInFrames = (uint)((samplesperframe * frames) / (Samplerate / 75));
                        }
                        else
                        {
                            // CBR
                            DurationInFrames = (uint)((samplesperframe * datalength / framesize) / (Samplerate / 75));
                        }
                    }
                    catch
                    {
                        DurationInFrames = (uint)((samplesperframe * datalength / framesize) / (Samplerate / 75));
                    }

                    b0 = s.ReadByte();
                    b1 = s.ReadByte();
                    if ((b0 == -1) || (b1 == -1))
                        return;

                    if ((b0 != 0xff)||((b1 & 0xe0) != 0xe0))
                    {
                        s.Seek(-framesize - 1, SeekOrigin.Current);
                        continue;
                    }

                    return;
                }
            }

        }

        public string CodecName => "MP3";

        public CodecType CodecType => CodecType.Lossy;

        public uint AverageBitrate
        {
            get;
            protected set;
        }

        public uint DurationInFrames
        {
            get;
            protected set;
        }

        public uint DurationInSeconds => DurationInFrames / 75;

        public uint MaxBitrate => AverageBitrate;

        public uint BitsPerSample => 16;

        public uint Samplerate
        {
            get;
            protected set;
        }

        public uint Channels
        {
            get;
            protected set;
        }

        public void SaveTags(string outputPath = null) => Save(outputPath);

    }

    public class DSFFile : ID3v2Tag, ICodecProvider, IMediaFile, IMetadataWriter
    {
        private long _tagoffset = 0;

        public IEnumerable<ICodecProvider> Codecs
        {
            get
            {
                yield return this;
            }
        }

        public IEnumerable<IMetadataProvider> Tags
        {
            get
            {
                yield return this;
            }
        }
        public DSFFile(string filename)
        {
            // ReadTag only runs when a metadata pointer is present, so set the filename here
            // too — otherwise a DSF with no existing tag can't be saved in place.
            _filename = filename;
            using (FileStream s = Tools.OpenReadSequential(filename))
            {
                byte[] header = new byte[4];
                s.ReadExactly(header);
                if (Encoding.ASCII.GetString(header, 0, 4) != "DSD ")
                    return;
                Array.Resize(ref header, 28);
                s.ReadExactly(header, 4, 24);
                _tagoffset = BitConverter.ToInt64(header, 20);
                if (_tagoffset != 0)
                {
                    s.Seek(_tagoffset, SeekOrigin.Begin);
                    ReadTag(s);
                    s.Seek(28, SeekOrigin.Begin);
                }
                // Read only the 4-byte "fmt " chunk id; the BinaryReader below continues from
                // the chunk-size field. (Reusing the 28-byte buffer here consumed too much and
                // misaligned every codec field.)
                byte[] fmtId = new byte[4];
                s.ReadExactly(fmtId);
                if (Encoding.ASCII.GetString(fmtId, 0, 4) != "fmt ")
                    return;
                using (BinaryReader r = new BinaryReader(s, Encoding.ASCII, true))
                {
                    ulong chunksize = r.ReadUInt64();
                    uint formatversion = r.ReadUInt32();
                    uint formatid = r.ReadUInt32();
                    uint channeltype = r.ReadUInt32();
                    Channels = r.ReadUInt32();
                    Samplerate = r.ReadUInt32();
                    BitsPerSample = r.ReadUInt32();
                    DurationInFrames = (uint)(75 * r.ReadUInt64() / Samplerate);
                }
            }

        }

        public uint DurationInFrames
        {
            get;
            protected set;
        }

        public uint DurationInSeconds => DurationInFrames / 75;

        public string CodecName => "DSD";

        public CodecType CodecType => CodecType.Lossless;

        public uint AverageBitrate
        {
            get
            {
                return BitsPerSample * Samplerate * Channels;
            }
        }

        public uint MaxBitrate => AverageBitrate;

        public uint BitsPerSample
        {
            protected set;
            get;
        }

        public uint Samplerate
        {
            protected set;
            get;
        }

        public uint Channels
        {
            protected set;
            get;
        }

        public void SaveTags(string outputPath = null)
        {
            if (_filename == null && outputPath == null)
                throw new InvalidOperationException("No filename associated with this file.");

            int frameHeaderSize = (_headerversion == 2) ? 6 : 10;
            int newTagBodySize = 0;
            foreach (var f in Frames)
                newTagBodySize += frameHeaderSize + f.Data.Length;
            _tagsize = newTagBodySize;
            if (_headerversion < 3) _headerversion = 3;

            if (outputPath == null)
            {
                // In-place: truncate at audio end, append new tag, patch DSD header
                long audioEnd = _tagoffset > 0 ? _tagoffset : new FileInfo(_filename).Length;
                using FileStream fs = new FileStream(_filename, FileMode.Open, FileAccess.ReadWrite);
                fs.SetLength(audioEnd);
                fs.Seek(0, SeekOrigin.End);
                WriteHeader(fs);
                foreach (var f in Frames)
                    f.Write(fs);
                long newTotalSize = fs.Position;
                fs.Seek(12, SeekOrigin.Begin);
                fs.Write(BitConverter.GetBytes(newTotalSize), 0, 8);
                fs.Write(BitConverter.GetBytes(audioEnd), 0, 8);
                _tagoffset = audioEnd;
            }
            else
            {
                // Write to new path: copy audio, append tag, patch DSD header
                string sourcePath = _filename ?? outputPath;
                long audioEnd = _tagoffset > 0 ? _tagoffset : new FileInfo(sourcePath).Length;
                using FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
                using FileStream dest = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                byte[] buffer = new byte[65536];
                long remaining = audioEnd;
                int read;
                while (remaining > 0 && (read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                {
                    dest.Write(buffer, 0, read);
                    remaining -= read;
                }
                WriteHeader(dest);
                foreach (var f in Frames)
                    f.Write(dest);
                long newTotalSize = dest.Position;
                dest.Seek(12, SeekOrigin.Begin);
                dest.Write(BitConverter.GetBytes(newTotalSize), 0, 8);
                dest.Write(BitConverter.GetBytes(audioEnd), 0, 8);
                _filename = outputPath;
                _tagoffset = audioEnd;
            }
        }

    }

}