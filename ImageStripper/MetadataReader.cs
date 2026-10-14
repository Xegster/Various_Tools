using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace ImageStripper
{
    /// <summary>A single metadata entry found in an image.</summary>
    public sealed class MetadataTag
    {
        public MetadataTag(string query, string group, string name, string value, bool isContainer)
        {
            Query = query;
            Group = group;
            Name = name;
            Value = value;
            IsContainer = isContainer;
        }

        /// <summary>Full WIC query path, e.g. "/app1/ifd/gps/{ushort=2}".</summary>
        public string Query { get; }

        public string Group { get; }

        public string Name { get; }

        public string Value { get; }

        /// <summary>True when this entry is a block (IFD, XMP packet, ...) rather than a leaf value.</summary>
        public bool IsContainer { get; }
    }

    /// <summary>Everything ImageStripper learned about one image file.</summary>
    public sealed class ImageScanResult
    {
        public List<MetadataTag> Tags { get; } = new();

        public string? Error { get; set; }

        public string Format { get; set; } = "";

        public int PixelWidth { get; set; }

        public int PixelHeight { get; set; }
    }

    /// <summary>
    /// Reads image metadata through the Windows Imaging Component. Every value is
    /// flattened into plain strings before it leaves this class, so the results can be
    /// handed back to the UI thread safely (WIC objects have thread affinity).
    /// </summary>
    public static class MetadataReader
    {
        private const int MaxDepth = 12;
        private const int MaxValueLength = 400;

        public static ImageScanResult Read(string filePath)
        {
            var result = new ImageScanResult();

            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);

                result.Format = decoder.CodecInfo?.FriendlyName ?? "Unknown";

                if (decoder.Frames.Count == 0)
                {
                    result.Error = "The file contains no image frames.";
                    return result;
                }

                BitmapFrame frame = decoder.Frames[0];

                try
                {
                    result.PixelWidth = frame.PixelWidth;
                    result.PixelHeight = frame.PixelHeight;
                }
                catch (Exception)
                {
                    // Dimensions are cosmetic; a codec that will not report them is not fatal.
                }

                if (frame.Metadata is BitmapMetadata metadata)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    Walk(metadata, string.Empty, result.Tags, seen, 0);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private static void Walk(BitmapMetadata metadata, string prefix, List<MetadataTag> tags, HashSet<string> seen, int depth)
        {
            if (depth > MaxDepth) return;

            IEnumerable<string> relativeQueries;
            try
            {
                // Materialise the list first: some codecs dislike being enumerated lazily.
                relativeQueries = new List<string>(metadata);
            }
            catch (Exception)
            {
                return;
            }

            foreach (string relative in relativeQueries)
            {
                string query = prefix + relative;
                if (!seen.Add(query)) continue;

                object? value;
                try
                {
                    value = metadata.GetQuery(relative);
                }
                catch (Exception)
                {
                    continue;
                }

                if (value is BitmapMetadata child)
                {
                    var (containerGroup, containerName) = TagCatalog.Describe(query, true);
                    tags.Add(new MetadataTag(query, containerGroup, containerName, "", true));
                    Walk(child, query, tags, seen, depth + 1);
                }
                else
                {
                    var (group, name) = TagCatalog.Describe(query, false);
                    tags.Add(new MetadataTag(query, group, name, FormatValue(value), false));
                }
            }
        }

        private static string FormatValue(object? value)
        {
            switch (value)
            {
                case null:
                    return "";
                case string text:
                    return Sanitize(text);
                case byte[] bytes:
                    return Describe(bytes, "bytes");
                case BitmapMetadataBlob blob:
                    try
                    {
                        return Describe(blob.GetBlobValue(), "blob");
                    }
                    catch (Exception)
                    {
                        return "[blob]";
                    }
                case DateTime date:
                    return date.ToString("yyyy-MM-dd HH:mm:ss");
                case bool flag:
                    return flag ? "True" : "False";
            }

            if (value is IEnumerable sequence)
            {
                var parts = new List<string>();
                foreach (object? item in sequence)
                {
                    parts.Add(FormatValue(item));
                    if (parts.Count >= 24)
                    {
                        parts.Add("...");
                        break;
                    }
                }

                return Sanitize(string.Join("; ", parts));
            }

            return Sanitize(value.ToString() ?? "");
        }

        private static string Describe(byte[] bytes, string label)
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(bytes.Length).Append(' ').Append(label).Append(']');

            int previewLength = Math.Min(bytes.Length, 16);
            for (int i = 0; i < previewLength; i++)
            {
                builder.Append(' ').Append(bytes[i].ToString("X2"));
            }

            if (bytes.Length > previewLength) builder.Append(" ...");
            return builder.ToString();
        }

        private static string Sanitize(string text)
        {
            var builder = new StringBuilder(Math.Min(text.Length, MaxValueLength + 1));

            foreach (char c in text)
            {
                if (builder.Length >= MaxValueLength)
                {
                    builder.Append('…');
                    break;
                }

                builder.Append(char.IsControl(c) ? ' ' : c);
            }

            return builder.ToString().Trim();
        }
    }
}
