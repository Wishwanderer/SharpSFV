using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Hashing; // Requires System.IO.Hashing NuGet
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SharpSFV
{
    /// <summary>
    /// Supported hashing algorithms.
    /// </summary>
    public enum HashType
    {
        XxHash3, // Fastest, non-cryptographic (Default)
        Crc32,   // Standard for SFV files
        MD5,     // Legacy standard
        SHA1,
        SHA256
    }

    /// <summary>
    /// Represents a single file entry in the UI.
    /// Used by Virtual Mode ListView to avoid creating heavy ListViewItems for every row.
    /// </summary>
    public class FileItemData
    {
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string BaseDirectory { get; set; } = "";
        public string ExpectedHash { get; set; } = "";

        // Used to maintain stable sort order when no specific column is selected
        public int OriginalIndex { get; set; }

        public string CalculatedHash { get; set; } = "Pending";
        public string Status { get; set; } = "Queued";
        public string TimeStr { get; set; } = "";

        // --- VISUAL PINNING OPTIMIZATION ---
        // If true, this item is temporarily moved to the top of the list 
        // so the user can see progress on large files without scrolling.
        public bool IsPinned { get; set; } = false;

        public Color ForeColor { get; set; } = SystemColors.ControlText;
        public Color BackColor { get; set; } = SystemColors.Window;
        public FontStyle FontStyle { get; set; } = FontStyle.Regular;
    }

    /// <summary>
    /// Custom comparer for sorting the ListView.
    /// Critical Logic: Pinned items (Active Large Files) ALWAYS float to the top.
    /// </summary>
    public class FileListSorter : IComparer<FileItemData>
    {
        public int SortColumn { get; set; } = -1;
        public SortOrder Order { get; set; } = SortOrder.None;
        private CaseInsensitiveComparer _objectCompare = new CaseInsensitiveComparer();

        public int Compare(FileItemData? x, FileItemData? y)
        {
            if (x == null || y == null) return 0;

            // --- PRIORITY LOGIC ---
            // If an item is Pinned (Processing Large File), it must appear at the top
            // regardless of the current column sort order.
            if (x.IsPinned && !y.IsPinned) return -1;
            if (!x.IsPinned && y.IsPinned) return 1;

            // Default stable sort based on insertion order
            if (Order == SortOrder.None) return x.OriginalIndex.CompareTo(y.OriginalIndex);

            int compareResult = 0;
            switch (SortColumn)
            {
                case 0: compareResult = _objectCompare.Compare(x.FileName, y.FileName); break;
                case 1: compareResult = _objectCompare.Compare(x.CalculatedHash, y.CalculatedHash); break;
                case 2: compareResult = _objectCompare.Compare(x.Status, y.Status); break;
                case 3:
                case 4:
                    // Sorts either Expected Hash OR Time depending on what data is populated
                    string valX = !string.IsNullOrEmpty(x.ExpectedHash) ? x.ExpectedHash : x.TimeStr;
                    string valY = !string.IsNullOrEmpty(y.ExpectedHash) ? y.ExpectedHash : y.TimeStr;
                    compareResult = _objectCompare.Compare(valX, valY);
                    break;
                default: compareResult = 0; break;
            }
            if (Order == SortOrder.Descending) compareResult = -compareResult;
            return compareResult;
        }
    }

    /// <summary>
    /// A wrapper around FileStream to report progress without checking stream position constantly.
    /// Includes throttling to prevent UI event flooding.
    /// </summary>
    public class ProgressStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly IProgress<double> _progress;
        private readonly long _totalLength;
        private long _bytesRead;
        private double _lastReported = 0;

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
                // PERFORMANCE: Only report progress if it has changed by at least 0.1%.
                // Reporting every byte read would freeze the UI thread.
                double newPercent = (double)_bytesRead / _totalLength * 100;
                if (newPercent - _lastReported >= 0.1)
                {
                    _progress?.Report(newPercent);
                    _lastReported = newPercent;
                }
            }
            return read;
        }

        // Boilerplate Stream implementation
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

    public static class HashHelper
    {
        private const int BufferSize = 1024 * 1024; // 1MB Buffer for optimal I/O

        /// <summary>
        /// Computes the hash of a file asynchronously on a worker thread.
        /// </summary>
        public static Task<string> ComputeHashAsync(string filePath, HashType type, IProgress<double>? progress = null, CancellationToken token = default)
        {
            return Task.Run(() =>
            {
                if (!File.Exists(filePath)) return "FILE_NOT_FOUND";

                try
                {
                    // SequentialScan hint tells Windows to cache aggressively for forward reading
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                    {
                        // Wrap in ProgressStream only if a progress reporter was actually passed
                        Stream streamToRead = (progress != null) ? new ProgressStream(fs, progress) : fs;

                        byte[] buffer = new byte[BufferSize];
                        int bytesRead;

                        switch (type)
                        {
                            // Optimized non-allocating loops for System.IO.Hashing (XxHash3, Crc32)
                            case HashType.XxHash3:
                                var xx3 = new XxHash128();
                                while ((bytesRead = streamToRead.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    if (token.IsCancellationRequested) return "CANCELLED";
                                    xx3.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                                }
                                return Convert.ToHexString(xx3.GetCurrentHash()).ToLowerInvariant();

                            case HashType.Crc32:
                                var crc = new Crc32();
                                while ((bytesRead = streamToRead.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    if (token.IsCancellationRequested) return "CANCELLED";
                                    crc.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                                }
                                return Convert.ToHexString(crc.GetCurrentHash()).ToLowerInvariant();

                            // Standard .NET Cryptography wrappers
                            case HashType.MD5:
                                using (var md5 = MD5.Create()) return Convert.ToHexString(md5.ComputeHash(streamToRead)).ToLowerInvariant();
                            case HashType.SHA1:
                                using (var sha1 = SHA1.Create()) return Convert.ToHexString(sha1.ComputeHash(streamToRead)).ToLowerInvariant();
                            case HashType.SHA256:
                                using (var sha256 = SHA256.Create()) return Convert.ToHexString(sha256.ComputeHash(streamToRead)).ToLowerInvariant();

                            default: return "UNKNOWN_ALGO";
                        }
                    }
                }
                catch (IOException) { return "ACCESS_DENIED"; }
                catch (Exception) { return "ERROR"; }
            });
        }
    }
}