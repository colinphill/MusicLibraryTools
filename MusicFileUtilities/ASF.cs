/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/ASF.cs $
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

namespace MusicFileUtilities
{

    public class WMPicture
    {
        public byte[] Data;

        public WMPicture()
        {
        }

        public WMPicture(byte[] data)
            : this()
        {
            Data = data;
            Decode();
        }

        private ID3v2Util.APICType _type;
        private string _mimetype;
        private string _description;
        private byte [] _picdata;

        private void Decode()
        {
            _type = (ID3v2Util.APICType)Data[0];
            int datalen = Tools.Int32AtLE(Data, 1);

            int start = 5;
            int end = start;
            for (; end < Data.Length; end += 2)
            {
                if ((Data[end] == 0) && (Data[end + 1] == 0))
                    break;
            }
            _mimetype = Encoding.Unicode.GetString(Data, start, end - start);
            
            start = end + 2;
            end = start;
            for (; end < Data.Length; end += 2)
            {
                if ((Data[end] == 0) && (Data[end + 1] == 0))
                    break;
            }

            _description = Encoding.Unicode.GetString(Data, start, end - start);

            _picdata = new byte[datalen];
            Array.Copy(Data, end + 2, _picdata, 0, datalen);
        }

        public ID3v2Util.APICType Type
        {
            get
            {
                return _type;
            }
        }

        public string MimeType
        {
            get
            {
                return _mimetype;
            }
        }

        public string Description
        {
            get
            {
                return _description;
            }
        }

        public byte[] PictureData
        {
            get
            {
                return _picdata;
            }
        }
       
    }

    public class ASFFile : IMetadataProvider
    {

        #region IMetadataProvider Properties
        public string Title
        {
            get
            {
                try
                {
                    return TextFields.First(kv => kv.Key == "TITLE").Value;
                }
                catch
                {
                    throw new NoMetadataException("Title");
                }
            }
        }

        public string Album
        {
            get
            {
                try
                {
                    return TextFields.First(kv => kv.Key == "WM/AlbumTitle").Value;
                }
                catch
                {
                    throw new NoMetadataException("Album");
                }
            }
        }

        public string Artist
        {
            get
            {
                try
                {
                    return TextFields.First(kv => kv.Key == "ARTIST").Value;
                }
                catch
                {
                    throw new NoMetadataException("Artist");
                }
            }
        }

        public string AlbumArtist
        {
            get
            {
                try
                {
                    return TextFields.First(kv => kv.Key == "WM/AlbumArtist").Value;
                }
                catch
                {
                    throw new NoMetadataException("AlbumArtist");
                }
            }
        }

        public int TrackNumber
        {
            get
            {
                try
                {
                    return int.Parse(TextFields.First(kv => kv.Key == "WM/TrackNumber").Value);
                }
                catch
                {
                    throw new NoMetadataException("TrackNumber");
                }
            }
        }

        public bool Compilation
        {
            get
            {
                try
                {
                    return int.Parse(TextFields.First(kv => kv.Key == "Compilation").Value) != 0;
                }
                catch
                {
                    throw new NoMetadataException("TrackNumber");
                }
            }
        }

        public IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var kv in TextFields)
                yield return kv;
        }

        public IEnumerable<IMetadataImage> GetImageMetadata()
        {
            yield break;
        }


        #endregion

        public string Filename
        {
            get;
            set;
        }

        public List<KeyValuePair<string, string>> TextFields = new List<KeyValuePair<string, string>>();
        public List<WMPicture> Pictures = new List<WMPicture>();

        private static Guid ASF_Header_Object = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C");
        private static Guid ASF_Metadata_Object = new Guid("C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA");
        private static Guid ASF_Extended_Content_Description_Object = new Guid("D2D0A440-E307-11D2-97F0-00A0C95EA850");
        private static Guid ASF_Header_Extension_Object = new Guid("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
        private static Guid ASF_Reserved_1 = new Guid("ABD3D211-A9BA-11cf-8EE6-00C00C205365");
        private static Guid ASF_Content_Description_Object = new Guid("75B22633-668E-11CF-A6D9-00AA0062CE6C");
        private static Guid ASF_Metadata_Library_Object = new Guid("44231c94-9498-49d1-a141-1d134e457054");

        private void ReadMetadata(Stream s)
        {
            byte[] b = new byte[4];
            s.Read(b, 0, 2);

            int count = Tools.UInt16AtLE(b, 0);

            for (int i = 0; i < count; i++)
            {
                s.Read(b, 0, 2); // Reserved (Language List Index for Library)
                s.Read(b, 0, 2); // Stream Number

                s.Read(b, 0, 2);
                int namelen = Tools.UInt16AtLE(b, 0);
                s.Read(b, 0, 2);
                int desctype = Tools.UInt16AtLE(b, 0);
                s.Read(b, 0, 4);
                int vallen = Tools.Int32AtLE(b, 0);

                byte[] bname = new byte[namelen];
                s.Read(bname, 0, namelen);
                string name = Encoding.Unicode.GetString(bname).Split("\0".ToCharArray()).First();

                byte[] value = new byte[vallen];
                s.Read(value, 0, vallen);
                string svalue = "";
               
                if (desctype == 0)
                {
                    svalue = Encoding.Unicode.GetString(value).Split("\0".ToCharArray()).First();
                    //TextFields.Add(new KeyValuePair<string, string>(name, svalue));
                }

                if (name == "WM/Picture")
                {
                    WMPicture p = new WMPicture(value);
                    Pictures.Add(p);
                }

            }

        }
        
        private void ReadHeaderExtension(Stream s)
        {
            byte[] b = new byte[24];
            s.Read(b, 0, 22);

            if ((new Guid(b.Take(16).ToArray()) != ASF_Reserved_1) || (Tools.UInt16AtLE(b, 16) != 6))
                throw new InvalidDataException();

            int size = Tools.Int32AtLE(b, 18);

            long endpos = s.Position + size;

            while (s.Position < endpos)
            {
                s.Read(b, 0, 24);
                Guid t = new Guid(b.Take(16).ToArray());
                long len = Tools.Int64AtLE(b, 16);

                if (/*(t == ASF_Metadata_Object)||*/(t == ASF_Metadata_Library_Object)) // Only WM/Pictures
                    ReadMetadata(s);
                else
                    s.Seek(len - 24, SeekOrigin.Current);
            }
                        
        }

        private void ReadContentDescription(Stream s)
        {
            byte[] b = new byte[10];
            s.Read(b, 0, 10);

            int titlelen = Tools.UInt16AtLE(b, 0);
            int authorlen = Tools.UInt16AtLE(b, 2);
            int copyrightlen = Tools.UInt16AtLE(b, 4);
            int descriptionlen = Tools.UInt16AtLE(b, 6);
            int ratinglen = Tools.UInt16AtLE(b, 8);

            byte[] title = new byte[titlelen];
            s.Read(title, 0, titlelen);
            if (titlelen != 0)
                TextFields.Add(new KeyValuePair<string, string>("TITLE", Encoding.Unicode.GetString(title).Split("\0".ToCharArray()).First()));

            byte[] author = new byte[authorlen];
            s.Read(author, 0, authorlen);
            if (authorlen != 0)
                TextFields.Add(new KeyValuePair<string, string>("ARTIST", Encoding.Unicode.GetString(author).Split("\0".ToCharArray()).First()));

            byte[] copyright = new byte[copyrightlen];
            s.Read(copyright, 0, copyrightlen);
            if (copyrightlen != 0)
                TextFields.Add(new KeyValuePair<string, string>("COPYRIGHT", Encoding.Unicode.GetString(copyright).Split("\0".ToCharArray()).First()));
            
            byte[] description = new byte[descriptionlen];
            s.Read(description, 0, descriptionlen);
            if (descriptionlen != 0)
                TextFields.Add(new KeyValuePair<string, string>("DESCRIPTION", Encoding.Unicode.GetString(description).Split("\0".ToCharArray()).First()));
            
            byte[] rating = new byte[ratinglen];
            s.Read(rating, 0, ratinglen);
            if (ratinglen != 0)
                TextFields.Add(new KeyValuePair<string, string>("RATING", Encoding.Unicode.GetString(rating).Split("\0".ToCharArray()).First()));
            

        }

        private void ReadExtendedContentDescription(Stream s)
        {
            byte[] b = new byte[2];
            s.Read(b, 0, 2);

            int count = Tools.UInt16AtLE(b, 0);

            for (int i = 0; i < count; i++)
            {
                s.Read(b, 0, 2);
                int namelen = Tools.UInt16AtLE(b, 0);
                byte[] bname = new byte[namelen];
                s.Read(bname, 0, namelen);
                string name = Encoding.Unicode.GetString(bname).Split("\0".ToCharArray()).First();
                s.Read(b, 0, 2);
                int desctype = Tools.UInt16AtLE(b, 0);
                s.Read(b, 0, 2);
                int vallen = Tools.UInt16AtLE(b, 0);
                byte[] value = new byte[vallen];
                s.Read(value, 0, vallen);
                string svalue = "";
                if (desctype == 0)
                {
                    svalue = Encoding.Unicode.GetString(value).Split("\0".ToCharArray()).First();
                    TextFields.Add(new KeyValuePair<string, string>(name, svalue));
                }
            }
        }

        private void ReadHeader(Stream s)
        {
            byte[] obj = new byte[30];
            s.Read(obj, 0, 30);

            Guid t = new Guid(obj.Take(16).ToArray());
            long len = Tools.Int64AtLE(obj, 16);
            int count = Tools.Int32AtLE(obj, 24);

            if ((obj[28] != 1) || (obj[29] != 2) || (t != ASF_Header_Object))
                throw new InvalidDataException();

            for (int i = 0; i < count; i++)
            {
                s.Read(obj, 0, 24);
                t = new Guid(obj.Take(16).ToArray());
                len = Tools.Int64AtLE(obj, 16);
                if (t == ASF_Header_Extension_Object)
                    ReadHeaderExtension(s);
                else if (t == ASF_Content_Description_Object)
                    ReadContentDescription(s);
                else if (t == ASF_Extended_Content_Description_Object)
                    ReadExtendedContentDescription(s);
                else
                    s.Seek(len - 24, SeekOrigin.Current);
            }

        }

        public ASFFile(string filename)
        {
            Filename = filename;

            FileStream s = new FileStream(filename, FileMode.Open, FileAccess.Read);

            ReadHeader(s);

            while (s.Position < s.Length)
            {
                byte[] obj = new byte[24];
                s.Read(obj, 0, 24);

                long len = Tools.Int64AtLE(obj, 16);

                s.Seek(len, SeekOrigin.Current);
            }


            s.Close();

        }


    }

}
