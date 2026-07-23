using System;
using System.Collections.Generic;
using System.IO;

namespace MusicFileUtilities
{
    /// <summary>
    /// Shared persistence surface for audio formats whose native metadata is a trailing APEv2 tag.
    /// Codec-specific implementations parse their own stream headers, then call
    /// <see cref="LoadApeTag"/> to expose a writable metadata and artwork layer.
    /// </summary>
    public abstract class ApeTaggedAudioFile :
        IMediaFile,
        IMetadataWriter,
        IMultiValueMetadataWriter,
        IUserStringMetadata,
        IMultiValueUserStringMetadata,
        IArtworkWriter
    {
        private readonly OwnedApeTag _tag;
        private string _filename;
        private long _audioEnd;

        protected ApeTaggedAudioFile(string filename)
        {
            _filename = filename ??
                throw new ArgumentNullException(nameof(filename));
            _tag = new OwnedApeTag(this);
        }

        public abstract IEnumerable<ICodecProvider> Codecs { get; }

        public IEnumerable<IMetadataProvider> Tags
        {
            get { yield return _tag; }
        }

        protected APETag ApeTag => _tag;

        protected long AudioEndOffset => _audioEnd;

        protected void LoadApeTag(
            Stream stream,
            bool readArtwork,
            long knownLength)
        {
            if (!_tag.ReadTag(
                    stream,
                    onlyAtEnd: true,
                    readArtwork: readArtwork,
                    knownLength: knownLength))
                _audioEnd = knownLength;
            else
                _audioEnd = _tag.AudioEndOffset;
        }

        public void SetField(TagFields field, string value) =>
            _tag.SetField(field, value);

        public void RemoveField(TagFields field) =>
            _tag.RemoveField(field);

        public bool SupportsMultipleValues(TagFields field) =>
            _tag.SupportsMultipleValues(field);

        public void SetFieldValues(
            TagFields field,
            IReadOnlyList<string> values) =>
            _tag.SetFieldValues(field, values);

        public IEnumerable<KeyValuePair<string, string>> GetUserStrings() =>
            _tag.GetUserStrings();

        public void SetUserString(string key, string value) =>
            _tag.SetUserString(key, value);

        public void RemoveUserString(string key) =>
            _tag.RemoveUserString(key);

        public void SetUserStringValues(
            string key,
            IReadOnlyList<string> values) =>
            _tag.SetUserStringValues(key, values);

        public void SetFrontCover(byte[] imageData, string mimeType) =>
            _tag.SetFrontCover(imageData, mimeType);

        public void RemoveImages() =>
            _tag.RemoveImages();

        public void SetImages(IReadOnlyList<ArtworkImage> images) =>
            _tag.SetImages(images);

        public void SaveTags(string outputPath = null) =>
            Save(outputPath);

        public void Save(string outputPath = null)
        {
            string target = outputPath ?? _filename ??
                throw new InvalidOperationException(
                    "No filename associated with this file.");
            string sourcePath = _filename;
            byte[] tagBytes = _tag.ToByteArray();
            long sourceLength = new FileInfo(sourcePath).Length;
            long copyLength = _audioEnd >= 0 && _audioEnd <= sourceLength
                ? _audioEnd
                : sourceLength;
            string tempPath = Tools.CreateSiblingTempPath(target);
            try
            {
                using (FileStream source = Tools.OpenReadSequential(sourcePath))
                using (FileStream destination =
                       Tools.CreateWriteSequential(tempPath))
                {
                    Tools.CopyExactly(source, destination, copyLength);
                    destination.Write(tagBytes);
                    destination.Flush(flushToDisk: true);
                }

                Tools.AtomicReplace(tempPath, target);
            }
            catch
            {
                Tools.DeleteIfExists(tempPath);
                throw;
            }

            _filename = target;
            _audioEnd = copyLength;
        }

        private sealed class OwnedApeTag :
            APETag,
            IMetadataWriter
        {
            private readonly ApeTaggedAudioFile _owner;

            public OwnedApeTag(ApeTaggedAudioFile owner) =>
                _owner = owner;

            public void Save(string outputPath = null) =>
                _owner.Save(outputPath);
        }
    }
}
