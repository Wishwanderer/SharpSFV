using System;
using System.Collections;
using System.IO;
using System.IO.Hashing; // Requires System.IO.Hashing NuGet package or .NET 6+ built-in
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    /// <summary>
    /// Supported hashing algorithms.
    /// </summary>
    public enum HashType
    {
        XxHash3,
        Crc32,
        MD5,
        SHA1,
        SHA256
    }

    /// <summary>
    /// Represents the data model for a single file within the ListView.
    /// Stored in the ListViewItem.Tag property.
    /// </summary>
    public class FileItemData
    {
        /// <summary>The absolute path to the file on the disk.</summary>
        public string FullPath { get; set; } = "";

        /// <summary>The path relative to the base directory (used for creation).</summary>
        public string RelativePath { get; set; } = "";

        /// <summary>The root directory used to calculate the RelativePath.</summary>
        public string BaseDirectory { get; set; } = "";

        /// <summary>The expected hash value (used only during verification).</summary>
        public string ExpectedHash { get; set; } = "";

        /// <summary>The original index of the item, used for default sorting.</summary>
        public int Index { get; set; }
    }

    /// <summary>
    /// Implements custom sorting logic for the ListView.
    /// Supports sorting by Column (Text) or by original Index.
    /// </summary>
    public class ListViewColumnSorter : IComparer
    {
        public int SortColumn { get; set; } = -1;
        public SortOrder Order { get; set; } = SortOrder.None;
        private CaseInsensitiveComparer ObjectCompare = new CaseInsensitiveComparer();

        public int Compare(object? x, object? y)
        {
            if (x is ListViewItem listviewX && y is ListViewItem listviewY)
            {
                // If no sort order is selected, sort by the original file index (FileItemData.Index)
                if (Order == SortOrder.None)
                {
                    if (listviewX.Tag is FileItemData dataX && listviewY.Tag is FileItemData dataY)
                    {
                        return dataX.Index.CompareTo(dataY.Index);
                    }
                    return 0;
                }

                // Get text from the column being sorted
                string textX = listviewX.SubItems.Count > SortColumn ? listviewX.SubItems[SortColumn].Text : "";
                string textY = listviewY.SubItems.Count > SortColumn ? listviewY.SubItems[SortColumn].Text : "";

                // Compare text
                int compareResult = ObjectCompare.Compare(textX, textY);

                if (Order == SortOrder.Ascending) return compareResult;
                else if (Order == SortOrder.Descending) return (-compareResult);
            }
            return 0;
        }
    }

    /// <summary>
    /// A wrapper around a standard Stream that reports read progress.
    /// Essential for updating the UI progress bar during the hashing of large individual files.
    /// </summary>
    public class ProgressStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly IProgress<double> _progress;
        private readonly long _totalLength;
        private long _bytesRead;

        public ProgressStream(Stream innerStream, IProgress<double> progress)
        {
            _innerStream = innerStream;
            _progress = progress;
            _totalLength = innerStream.Length;
            _bytesRead = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _innerStream.Read(buffer, offset, count);
            _bytesRead += read;
            if (_totalLength > 0)
            {
                // Report progress as a percentage (0-100)
                _progress?.Report((double)_bytesRead / _totalLength * 100);
            }
            return read;
        }

        // Standard Stream overrides delegating to inner stream
        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }
        public override void Flush() => _innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);
    }

    /// <summary>
    /// Static helper methods for computing file hashes.
    /// </summary>
    public static class HashHelper
    {
        private const int BufferSize = 1024 * 1024; // 1MB Buffer for read operations

        /// <summary>
        /// Computes the hash of a file asynchronously, reporting progress.
        /// </summary>
        /// <param name="filePath">Full path to the file.</param>
        /// <param name="type">The hashing algorithm to use.</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <returns>The hexadecimal hash string, or error code.</returns>
        public static Task<string> ComputeHashAsync(string filePath, HashType type, IProgress<double>? progress = null)
        {
            return Task.Run(() =>
            {
                if (!File.Exists(filePath)) return "FILE_NOT_FOUND";

                try
                {
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                    {
                        // Wrap the file stream in our ProgressStream if a reporter was provided
                        Stream streamToRead = (progress != null) ? new ProgressStream(fs, progress) : fs;

                        byte[] buffer = new byte[BufferSize];
                        int bytesRead;

                        switch (type)
                        {
                            case HashType.XxHash3:
                                var xx3 = new XxHash128();
                                while ((bytesRead = streamToRead.Read(buffer, 0, buffer.Length)) > 0)
                                    xx3.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                                return Convert.ToHexString(xx3.GetCurrentHash()).ToLowerInvariant();

                            case HashType.Crc32:
                                var crc = new Crc32();
                                while ((bytesRead = streamToRead.Read(buffer, 0, buffer.Length)) > 0)
                                    crc.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                                return Convert.ToHexString(crc.GetCurrentHash()).ToLowerInvariant();

                            case HashType.MD5:
                                using (var md5 = MD5.Create())
                                    return Convert.ToHexString(md5.ComputeHash(streamToRead)).ToLowerInvariant();

                            case HashType.SHA1:
                                using (var sha1 = SHA1.Create())
                                    return Convert.ToHexString(sha1.ComputeHash(streamToRead)).ToLowerInvariant();

                            case HashType.SHA256:
                                using (var sha256 = SHA256.Create())
                                    return Convert.ToHexString(sha256.ComputeHash(streamToRead)).ToLowerInvariant();

                            default: return "UNKNOWN_ALGO";
                        }
                    }
                }
                catch (IOException) { return "ACCESS_DENIED"; }
                catch (Exception) { return "ERROR"; }
            });
        }

        /// <summary>
        /// Formats byte size into human-readable strings (KB, MB, GB).
        /// </summary>
        public static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
