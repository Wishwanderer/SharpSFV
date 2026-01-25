using SharpSFV.Models;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SharpSFV
{
    /// <summary>
    /// Implements Indirect Sorting for the Virtual ListView.
    /// <para>
    /// <b>Architecture Choice:</b>
    /// Since our data is stored in parallel arrays (SoA) within <see cref="FileStore"/>, we cannot use standard object sorting.
    /// Instead, we sort a <c>List&lt;int&gt;</c> of indices.
    /// This comparer takes two indices (<paramref name="x"/>, <paramref name="y"/>), looks up the actual data 
    /// in the <see cref="FileStore"/>, and decides their order.
    /// </para>
    /// <para>
    /// <b>Performance:</b> 
    /// Avoids moving heavy data in memory. Only integer pointers are swapped during the sort operation.
    /// </para>
    /// </summary>
    public class FileListSorter : IComparer<int>
    {
        private readonly FileStore _store;

        public int SortColumn { get; set; } = -1;
        public SortOrder Order { get; set; } = SortOrder.None;

        public FileListSorter(FileStore store)
        {
            _store = store;
        }

        /// <summary>
        /// Compares two items via their indices.
        /// </summary>
        /// <param name="x">The index of the first item in the FileStore.</param>
        /// <param name="y">The index of the second item in the FileStore.</param>
        public int Compare(int x, int y)
        {
            // Safety checks: Ensure indices are within bounds of the arrays
            if (x < 0 || x >= _store.Count) return 0;
            if (y < 0 || y >= _store.Count) return 0;

            // Priority Logic: Summary rows (e.g., "Total Time") must always stay at the bottom.
            bool xSummary = _store.IsSummaryRows[x];
            bool ySummary = _store.IsSummaryRows[y];

            if (xSummary && !ySummary) return 1;
            if (!xSummary && ySummary) return -1;
            if (xSummary && ySummary) return 0;

            // Default sort: Original Index (Load order)
            if (Order == SortOrder.None)
            {
                return _store.OriginalIndices[x].CompareTo(_store.OriginalIndices[y]);
            }

            int result = 0;

            // Switch based on the currently clicked column index
            switch (SortColumn)
            {
                case 0: // File Name
                    // Note: Sorting by Name uses the FileName array, not FullPath, for speed.
                    result = string.Compare(_store.FileNames[x], _store.FileNames[y], StringComparison.OrdinalIgnoreCase);
                    break;
                case 1: // Hash
                    // Custom byte[] comparison required (Arrays don't implement IComparable naturally)
                    result = CompareHashes(_store.CalculatedHashes[x], _store.CalculatedHashes[y]);
                    break;
                case 2: // Status
                    // Enum comparison is effectively an integer comparison (very fast)
                    result = _store.Statuses[x].CompareTo(_store.Statuses[y]);
                    break;
                case 3: // Expected Hash
                    result = CompareHashes(_store.ExpectedHashes[x], _store.ExpectedHashes[y]);
                    break;
                case 4: // Time
                    // Lexical sort for time string is generally sufficient for visual sorting.
                    // (e.g., "100 ms" vs "20 ms" might sort oddly, but usually acceptable for this view).
                    result = string.Compare(_store.TimeStrs[x], _store.TimeStrs[y], StringComparison.OrdinalIgnoreCase);
                    break;
            }

            return (Order == SortOrder.Descending) ? -result : result;
        }

        /// <summary>
        /// Performs a lexicographical comparison of two byte arrays.
        /// (e.g., 0x00 comes before 0xFF).
        /// </summary>
        private int CompareHashes(byte[]? h1, byte[]? h2)
        {
            if (h1 == null && h2 == null) return 0;
            if (h1 == null) return -1;
            if (h2 == null) return 1;

            // Compare byte by byte
            int len = Math.Min(h1.Length, h2.Length);
            for (int i = 0; i < len; i++)
            {
                int c = h1[i].CompareTo(h2[i]);
                if (c != 0) return c;
            }

            // If bytes match, shorter array comes first
            return h1.Length.CompareTo(h2.Length);
        }
    }
}