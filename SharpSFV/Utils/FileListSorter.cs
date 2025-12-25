using SharpSFV.Models;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SharpSFV
{
    /// <summary>
    /// OPTIMIZATION #2: Index-Based Sorter.
    /// Implements IComparer<int> to sort indices based on data in the FileStore arrays.
    /// Eliminates object pointer chasing during sort operations.
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

        public int Compare(int x, int y)
        {
            // Safety checks
            if (x < 0 || x >= _store.Count) return 0;
            if (y < 0 || y >= _store.Count) return 0;

            // Summary rows always go to the bottom
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
            switch (SortColumn)
            {
                case 0: // File Name
                    // Note: Sorting by Name usually implies Full Path if verified, 
                    // but the column usually shows just Name. 
                    // To keep it fast, we sort by the FileName array.
                    result = string.Compare(_store.FileNames[x], _store.FileNames[y], StringComparison.OrdinalIgnoreCase);
                    break;
                case 1: // Hash
                    result = CompareHashes(_store.CalculatedHashes[x], _store.CalculatedHashes[y]);
                    break;
                case 2: // Status (Enum comparison is fast integer comparison)
                    result = _store.Statuses[x].CompareTo(_store.Statuses[y]);
                    break;
                case 3: // Expected Hash
                    result = CompareHashes(_store.ExpectedHashes[x], _store.ExpectedHashes[y]);
                    break;
                case 4: // Time
                    // Lexical sort for time string is "okay" roughly, 
                    // but parsing to int is better if strictly needed. 
                    // Keeping string compare for speed as it's just visual.
                    result = string.Compare(_store.TimeStrs[x], _store.TimeStrs[y], StringComparison.OrdinalIgnoreCase);
                    break;
            }

            return (Order == SortOrder.Descending) ? -result : result;
        }

        private int CompareHashes(byte[]? h1, byte[]? h2)
        {
            if (h1 == null && h2 == null) return 0;
            if (h1 == null) return -1;
            if (h2 == null) return 1;

            int len = Math.Min(h1.Length, h2.Length);
            for (int i = 0; i < len; i++)
            {
                int c = h1[i].CompareTo(h2[i]);
                if (c != 0) return c;
            }
            return h1.Length.CompareTo(h2.Length);
        }
    }
}