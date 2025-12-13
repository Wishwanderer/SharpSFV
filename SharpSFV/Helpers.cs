using System;
using System.Collections;
using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    public enum HashType
    {
        XxHash3,
        Crc32,
        MD5,
        SHA1,
        SHA256
    }

    // 1. Data Model
    public class FileItemData
    {
        public string FullPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string BaseDirectory { get; set; } = "";
        public string ExpectedHash { get; set; } = "";
        public int Index { get; set; }
    }

    // 2. Sorting Logic (Unchanged)
    public class ListViewColumnSorter : IComparer
    {
        public int SortColumn { get; set; } = -1;
        public SortOrder Order { get; set; } = SortOrder.None;
        private CaseInsensitiveComparer ObjectCompare = new CaseInsensitiveComparer();

        public int Compare(object? x, object? y)
        {
            if (x is ListViewItem listviewX && y is ListViewItem listviewY)
            {
                if (Order == SortOrder.None)
                {
                    if (listviewX.Tag is FileItemData dataX && listviewY.Tag is FileItemData dataY)
                    {
                        return dataX.Index.CompareTo(dataY.Index);
                    }
                    return 0;
                }

                string textX = listviewX.SubItems.Count > SortColumn ? listviewX.SubItems[SortColumn].Text : "";
                string textY = listviewY.SubItems.Count > SortColumn ? listviewY.SubItems[SortColumn].Text : "";

                int compareResult = ObjectCompare.Compare(textX, textY);

                if (Order == SortOrder.Ascending) return compareResult;
                else if (Order == SortOrder.Descending) return (-compareResult);
            }
            return 0;
        }
    }

    // 3. New Helper: Progress Stream Wrapper
    // This allows us to hook into the read process of ANY algorithm (MD5.ComputeHash, etc.)
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
                _progress?.Report((double)_bytesRead / _totalLength * 100);
            }
            return read;
        }

        // Mandatory overrides for Stream class
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

    // 4. Hash Logic
    public static class HashHelper
    {
        private const int BufferSize = 1024 * 1024; // 1MB Buffer

        // Update signature to accept IProgress
        public static Task<string> ComputeHashAsync(string filePath, HashType type, IProgress<double>? progress = null)
        {
            return Task.Run(() =>
            {
                if (!File.Exists(filePath)) return "FILE_NOT_FOUND";

                try
                {
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                    {
                        // Wrap the FileStream in our ProgressStream if progress is requested
                        Stream streamToRead = (progress != null) ? new ProgressStream(fs, progress) : fs;

                        // NOTE: If using ProgressStream, we don't need to manually calculate loop progress
                        // because the ProgressStream does it inside .Read()

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