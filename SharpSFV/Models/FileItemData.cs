using System;
using System.Drawing;
using System.IO;

namespace SharpSFV
{
    public class FileItemData
    {
        // MEMORY OPTIMIZATION: Remove backing field for FullPath. 
        // Reconstruct it on demand.
        public string FullPath => Path.Combine(BaseDirectory, RelativePath);

        public string FileName { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string BaseDirectory { get; set; } = "";

        // MEMORY OPTIMIZATION: Store raw bytes.
        public byte[]? ExpectedHash { get; set; }

        private byte[]? _calculatedHash;
        private string? _cachedHashString; // CACHE OPTIMIZATION: Avoid repetitive hex conversion

        public byte[]? CalculatedHash
        {
            get => _calculatedHash;
            set
            {
                _calculatedHash = value;
                _cachedHashString = null; // Invalidate cache
            }
        }

        public int OriginalIndex { get; set; }
        public ItemStatus Status { get; set; } = ItemStatus.Queued;
        public string TimeStr { get; set; } = "";

        // Flags
        public bool IsSummaryRow { get; set; } = false;

        // Visuals
        public Color ForeColor { get; set; } = SystemColors.ControlText;
        public Color BackColor { get; set; } = SystemColors.Window;
        public FontStyle FontStyle { get; set; } = FontStyle.Regular;

        // Helper for UI/Export
        public string GetCalculatedHashString()
        {
            if (_cachedHashString != null) return _cachedHashString;

            if (CalculatedHash != null)
            {
                _cachedHashString = Convert.ToHexString(CalculatedHash);
                return _cachedHashString;
            }

            return (Status == ItemStatus.Queued || Status == ItemStatus.Pending) ? "Pending" : "";
        }

        public string GetExpectedHashString() =>
            ExpectedHash != null ? Convert.ToHexString(ExpectedHash) : "";

        public string GetStatusString() => Status.ToString();
    }
}