using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ImageStripper
{
    /// <summary>Outcome of stripping tags from a single file.</summary>
    public sealed class RemovalResult
    {
        public bool Success { get; set; }

        /// <summary>True when the file on disk was actually rewritten.</summary>
        public bool Changed { get; set; }

        public int RemovedCount { get; set; }

        /// <summary>"in-place" (lossless) or "re-encoded".</summary>
        public string Method { get; set; } = "";

        public string? BackupPath { get; set; }

        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Removes metadata queries from image files. The in-place writer is tried first
    /// because it rewrites only the metadata blocks and leaves the compressed pixel
    /// data untouched; re-encoding is the fallback for codecs that do not support it.
    /// </summary>
    public static class MetadataRemover
    {
        private const string BackupFolderName = "Backup";
        private const string TempSuffix = ".imagestripper.tmp";

        public static RemovalResult Remove(string filePath, IEnumerable<string> queries, bool createBackup, bool preserveDates)
        {
            var result = new RemovalResult();
            IReadOnlyList<string> ordered = Order(queries);

            if (ordered.Count == 0)
            {
                result.Success = true;
                result.Message = "No tags selected.";
                return result;
            }

            DateTime originalCreation = File.GetCreationTimeUtc(filePath);
            DateTime originalWrite = File.GetLastWriteTimeUtc(filePath);

            string? backupPath = null;
            bool backupExistedAlready = false;
            try
            {
                if (createBackup) backupPath = CreateBackup(filePath, out backupExistedAlready);
                result.BackupPath = backupPath;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Could not create backup: {ex.Message}";
                return result;
            }

            try
            {
                if (TryRemoveInPlace(filePath, ordered, out int removedInPlace, out string? inPlaceError))
                {
                    result.Success = true;
                    result.RemovedCount = removedInPlace;
                    result.Changed = removedInPlace > 0;
                    result.Method = "in-place";
                }
                else
                {
                    int removed = RemoveByReencoding(filePath, ordered);
                    result.Success = true;
                    result.RemovedCount = removed;
                    result.Changed = removed > 0;
                    result.Method = "re-encoded";
                    result.Message = inPlaceError ?? "";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }

            if (result.Success && result.Changed && preserveDates)
            {
                try
                {
                    File.SetCreationTimeUtc(filePath, originalCreation);
                    File.SetLastWriteTimeUtc(filePath, originalWrite);
                }
                catch (Exception)
                {
                    // Keeping the original timestamps is a nicety, not a requirement.
                }
            }

            // A backup is only worth keeping if the file really changed.
            if (result.Success && !result.Changed && backupPath != null && !backupExistedAlready)
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch (Exception)
                {
                    // Leave the stray backup rather than failing the operation.
                }

                result.BackupPath = null;
            }

            return result;
        }

        private static string CreateBackup(string filePath, out bool existedAlready)
        {
            string directory = Path.GetDirectoryName(filePath) ?? "";
            string backupDirectory = Path.Combine(directory, BackupFolderName);
            Directory.CreateDirectory(backupDirectory);

            string backupPath = Path.Combine(backupDirectory, Path.GetFileName(filePath));

            // Never overwrite an existing backup: the first copy is the pristine one.
            existedAlready = File.Exists(backupPath);
            if (!existedAlready) File.Copy(filePath, backupPath);

            return backupPath;
        }

        /// <summary>
        /// Rewrites the metadata inside the existing file. This does not recompress the
        /// image, so it is the preferred path for JPEG and TIFF.
        /// </summary>
        private static bool TryRemoveInPlace(string filePath, IReadOnlyList<string> queries, out int removed, out string? error)
        {
            removed = 0;
            error = null;

            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);
                if (decoder.Frames.Count == 0)
                {
                    error = "The file contains no image frames.";
                    return false;
                }

                InPlaceBitmapMetadataWriter writer = decoder.Frames[0].CreateInPlaceBitmapMetadataWriter();

                int count = RemoveQueries(writer, queries);
                if (count == 0) return true;

                if (writer.TrySave())
                {
                    removed = count;
                    return true;
                }

                error = "In-place metadata write was rejected; falling back to re-encoding.";
                return false;
            }
            catch (Exception ex)
            {
                error = $"In-place edit unavailable ({ex.Message}); falling back to re-encoding.";
                return false;
            }
        }

        /// <summary>
        /// Decodes the image and writes it back out without the selected tags. Lossless for
        /// PNG/TIFF/BMP/GIF, but it recompresses JPEG.
        /// </summary>
        private static int RemoveByReencoding(string filePath, IReadOnlyList<string> queries)
        {
            string directory = Path.GetDirectoryName(filePath) ?? ".";
            string tempPath = Path.Combine(directory, Path.GetFileName(filePath) + TempSuffix);
            int removed = 0;

            try
            {
                BitmapEncoder encoder;

                using (var input = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                    BitmapEncoder? created = CreateEncoderFor(decoder);
                    if (created == null)
                    {
                        throw new NotSupportedException(
                            $"No encoder is available for {decoder.CodecInfo?.FriendlyName ?? "this format"}.");
                    }

                    encoder = created;

                    foreach (BitmapFrame frame in decoder.Frames)
                    {
                        BitmapMetadata? metadata = null;

                        if (frame.Metadata is BitmapMetadata source)
                        {
                            metadata = (BitmapMetadata)source.Clone();
                            removed += RemoveQueries(metadata, queries);
                        }

                        encoder.Frames.Add(BitmapFrame.Create(frame, frame.Thumbnail, metadata, frame.ColorContexts));
                    }
                }

                if (removed == 0) return 0;

                using (var output = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(output);
                }

                File.Move(tempPath, filePath, true);
                return removed;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception)
                    {
                        // Nothing useful to do if the temp file is locked.
                    }
                }
            }
        }

        private static int RemoveQueries(BitmapMetadata target, IReadOnlyList<string> queries)
        {
            int count = 0;

            foreach (string query in queries)
            {
                try
                {
                    // A parent block may already have taken this entry with it.
                    if (target.GetQuery(query) == null) continue;

                    target.RemoveQuery(query);
                    count++;
                }
                catch (Exception)
                {
                    // Tag absent in this file, or the codec refuses to remove it.
                }
            }

            return count;
        }

        /// <summary>Shallow queries first, so removing a block does not orphan its children.</summary>
        private static IReadOnlyList<string> Order(IEnumerable<string> queries)
        {
            return queries
                .Distinct(StringComparer.Ordinal)
                .OrderBy(query => query.Count(c => c == '/'))
                .ThenBy(query => query, StringComparer.Ordinal)
                .ToList();
        }

        private static BitmapEncoder? CreateEncoderFor(BitmapDecoder decoder)
        {
            return decoder switch
            {
                JpegBitmapDecoder => new JpegBitmapEncoder { QualityLevel = 95 },
                PngBitmapDecoder => new PngBitmapEncoder(),
                TiffBitmapDecoder => new TiffBitmapEncoder(),
                GifBitmapDecoder => new GifBitmapEncoder(),
                BmpBitmapDecoder => new BmpBitmapEncoder(),
                WmpBitmapDecoder => new WmpBitmapEncoder(),
                _ => null
            };
        }
    }
}
