using System;
using System.IO;

namespace SharpSFV
{
    /// <summary>
    /// OPTIMIZATION #2: Structure of Arrays (SoA) Architecture.
    /// Replaces List<FileItemData>.
    /// Stores data in parallel arrays to improve CPU cache locality,
    /// eliminate object header overhead (16 bytes per file), and reduce GC pressure.
    /// </summary>
    public class FileStore
    {
        private const int InitialCapacity = 8192;
        private readonly object _lock = new();

        public int Count { get; private set; } = 0;
        private int _capacity = InitialCapacity;

        // --- Parallel Arrays ---

        // Path Data (Nullable to allow empty slots and efficient clearing)
        public string?[] FileNames;
        public string?[] RelativePaths;
        public string?[] BaseDirectories;

        // State
        public ItemStatus[] Statuses;
        public int[] OriginalIndices;

        // Hash Data (Nullable byte arrays)
        public byte[]?[] ExpectedHashes;
        public byte[]?[] CalculatedHashes;
        public string?[] CachedHashStrings; // UI Cache for hex strings

        // Meta Data
        public string?[] TimeStrs;
        public bool[] IsSummaryRows;

        public FileStore()
        {
            FileNames = new string?[InitialCapacity];
            RelativePaths = new string?[InitialCapacity];
            BaseDirectories = new string?[InitialCapacity];
            Statuses = new ItemStatus[InitialCapacity];
            OriginalIndices = new int[InitialCapacity];

            ExpectedHashes = new byte[]?[InitialCapacity];
            CalculatedHashes = new byte[]?[InitialCapacity];
            CachedHashStrings = new string?[InitialCapacity];

            TimeStrs = new string?[InitialCapacity];
            IsSummaryRows = new bool[InitialCapacity];
        }

        /// <summary>
        /// Adds a new file entry in a thread-safe manner.
        /// </summary>
        public int Add(string fileName, string relativePath, string baseDir, int originalIndex, ItemStatus status = ItemStatus.Queued, byte[]? expectedHash = null)
        {
            lock (_lock)
            {
                if (Count >= _capacity) Resize();

                int index = Count;

                FileNames[index] = fileName;
                RelativePaths[index] = relativePath;
                BaseDirectories[index] = baseDir;
                OriginalIndices[index] = originalIndex;
                Statuses[index] = status;
                ExpectedHashes[index] = expectedHash;

                // Defaults
                CalculatedHashes[index] = null;
                CachedHashStrings[index] = null;
                TimeStrs[index] = "";
                IsSummaryRows[index] = false;

                Count++;
                return index;
            }
        }

        public void AddSummary(string name, string timeStr)
        {
            lock (_lock)
            {
                if (Count >= _capacity) Resize();
                int index = Count;

                FileNames[index] = name;
                TimeStrs[index] = timeStr;
                IsSummaryRows[index] = true;
                Statuses[index] = ItemStatus.OK;
                OriginalIndices[index] = int.MaxValue;

                // Empty fields to prevent null refs or ensure safe defaults
                RelativePaths[index] = "";
                BaseDirectories[index] = "";

                Count++;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                Count = 0;
                // Clear reference arrays to allow GC to reclaim large buffers
                Array.Clear(ExpectedHashes, 0, _capacity);
                Array.Clear(CalculatedHashes, 0, _capacity);
                Array.Clear(CachedHashStrings, 0, _capacity);
                Array.Clear(FileNames, 0, _capacity);
                // We don't necessarily need to clear strings like BaseDirectories if we trust Count, 
                // but clearing helps GC reclaim memory if the new list is much smaller.
                Array.Clear(RelativePaths, 0, _capacity);
                Array.Clear(BaseDirectories, 0, _capacity);
            }
        }

        private void Resize()
        {
            int newCap = _capacity * 2;

            Array.Resize(ref FileNames, newCap);
            Array.Resize(ref RelativePaths, newCap);
            Array.Resize(ref BaseDirectories, newCap);
            Array.Resize(ref Statuses, newCap);
            Array.Resize(ref OriginalIndices, newCap);

            Array.Resize(ref ExpectedHashes, newCap);
            Array.Resize(ref CalculatedHashes, newCap);
            Array.Resize(ref CachedHashStrings, newCap);

            Array.Resize(ref TimeStrs, newCap);
            Array.Resize(ref IsSummaryRows, newCap);

            _capacity = newCap;
        }

        // --- Data Access Helpers ---

        public string GetFullPath(int index)
        {
            // Reconstruct path on-demand
            if (IsSummaryRows[index]) return "";

            // We use the null-forgiving operator (!) here because for any valid 'index' < 'Count'
            // that isn't a summary row, Add() guarantees these fields are populated.
            return Path.Combine(BaseDirectories[index]!, RelativePaths[index]!);
        }

        public string GetCalculatedHashString(int index)
        {
            if (CachedHashStrings[index] != null) return CachedHashStrings[index]!;

            byte[]? hash = CalculatedHashes[index];
            if (hash != null)
            {
                string hex = Convert.ToHexString(hash);
                CachedHashStrings[index] = hex;
                return hex;
            }

            var status = Statuses[index];
            return (status == ItemStatus.Queued || status == ItemStatus.Pending) ? "Pending" : "";
        }

        public string GetExpectedHashString(int index)
        {
            byte[]? hash = ExpectedHashes[index];
            return hash != null ? Convert.ToHexString(hash) : "";
        }

        /// <summary>
        /// Updates the result for a specific index. 
        /// No global lock required as indices are unique per worker.
        /// </summary>
        public void SetResult(int index, byte[]? hash, string timeStr, ItemStatus status)
        {
            CalculatedHashes[index] = hash;
            CachedHashStrings[index] = null; // Invalidate cache
            TimeStrs[index] = timeStr;
            Statuses[index] = status;
        }

        /// <summary>
        /// Removes an item at a specific index. Expensive O(N) operation.
        /// </summary>
        public void RemoveAt(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= Count) return;

                int copyCount = Count - index - 1;
                if (copyCount > 0)
                {
                    Array.Copy(FileNames, index + 1, FileNames, index, copyCount);
                    Array.Copy(RelativePaths, index + 1, RelativePaths, index, copyCount);
                    Array.Copy(BaseDirectories, index + 1, BaseDirectories, index, copyCount);
                    Array.Copy(Statuses, index + 1, Statuses, index, copyCount);
                    Array.Copy(OriginalIndices, index + 1, OriginalIndices, index, copyCount);

                    Array.Copy(ExpectedHashes, index + 1, ExpectedHashes, index, copyCount);
                    Array.Copy(CalculatedHashes, index + 1, CalculatedHashes, index, copyCount);
                    Array.Copy(CachedHashStrings, index + 1, CachedHashStrings, index, copyCount);

                    Array.Copy(TimeStrs, index + 1, TimeStrs, index, copyCount);
                    Array.Copy(IsSummaryRows, index + 1, IsSummaryRows, index, copyCount);
                }

                Count--;

                // Null out the last slot to prevent memory leaks
                // Since arrays are now nullable, this is perfectly valid.
                FileNames[Count] = null;
                RelativePaths[Count] = null;
                BaseDirectories[Count] = null;
                ExpectedHashes[Count] = null;
                CalculatedHashes[Count] = null;
                CachedHashStrings[Count] = null;
                TimeStrs[Count] = null;
            }
        }
    }
}