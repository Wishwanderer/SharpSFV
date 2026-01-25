using System;
using System.IO;

namespace SharpSFV
{
    /// <summary>
    /// Implements a Structure of Arrays (SoA) data container for file information.
    /// <para>
    /// <b>Architecture Choice:</b>
    /// Unlike <c>List&lt;Class&gt;</c>, SoA stores properties in parallel arrays.
    /// This eliminates the 16-24 byte object header overhead per file, significantly improves CPU cache locality
    /// during sequential iteration, and reduces GC pressure by removing millions of small objects from the heap.
    /// </para>
    /// </summary>
    public class FileStore
    {
        // Initial allocation size. Powers of 2 are generally preferred for memory alignment.
        private const int InitialCapacity = 8192;
        private readonly object _lock = new();

        public int Count { get; private set; } = 0;
        private int _capacity = InitialCapacity;

        // --- Parallel Arrays (The "Structure" of Arrays) ---
        // Arrays are nullable (?) to allow aggressive GC reclamation upon Clear().

        public string?[] FileNames;
        public string?[] RelativePaths;
        public string?[] BaseDirectories;

        public ItemStatus[] Statuses;
        public int[] OriginalIndices;

        // Byte arrays for hashes. Null indicates not calculated/expected.
        public byte[]?[] ExpectedHashes;
        public byte[]?[] CalculatedHashes;

        // Optimization: Cache the hex string conversion for the UI to prevent re-allocating strings on every Paint event.
        public string?[] CachedHashStrings;

        // Meta Data for UI rendering
        public string?[] TimeStrs;
        public bool[] IsSummaryRows;

        public FileStore()
        {
            // Initialize all parallel arrays to the default capacity.
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
        /// Thread-safe addition of a new file entry. Checks capacity and resizes if necessary.
        /// </summary>
        /// <returns>The index of the newly added item, used to map UI indices to Store indices.</returns>
        public int Add(string fileName, string relativePath, string baseDir, int originalIndex, ItemStatus status = ItemStatus.Queued, byte[]? expectedHash = null)
        {
            lock (_lock)
            {
                if (Count >= _capacity) Resize();

                int index = Count;

                // Direct array access is faster than list accessors.
                FileNames[index] = fileName;
                RelativePaths[index] = relativePath;
                BaseDirectories[index] = baseDir;
                OriginalIndices[index] = originalIndex;
                Statuses[index] = status;
                ExpectedHashes[index] = expectedHash;

                // Ensure clean state for recycled slots
                CalculatedHashes[index] = null;
                CachedHashStrings[index] = null;
                TimeStrs[index] = "";
                IsSummaryRows[index] = false;

                Count++;
                return index;
            }
        }

        /// <summary>
        /// Adds a virtual "Summary" row to the list. 
        /// These are rendered differently (Bold/Blue) and ignored by the hashing engine.
        /// </summary>
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
                OriginalIndices[index] = int.MaxValue; // Force sort to bottom

                // Initialize required fields to prevent null reference exceptions during sort/render
                RelativePaths[index] = "";
                BaseDirectories[index] = "";

                Count++;
            }
        }

        /// <summary>
        /// Resets the store. 
        /// <para>
        /// <b>Performance Note:</b> Explicitly clears array references.
        /// Simply setting Count=0 would keep the arrays populated with string references, 
        /// preventing the Garbage Collector from reclaiming the memory used by 100k+ file paths.
        /// </para>
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                Count = 0;
                Array.Clear(ExpectedHashes, 0, _capacity);
                Array.Clear(CalculatedHashes, 0, _capacity);
                Array.Clear(CachedHashStrings, 0, _capacity);
                Array.Clear(FileNames, 0, _capacity);
                Array.Clear(RelativePaths, 0, _capacity);
                Array.Clear(BaseDirectories, 0, _capacity);
            }
        }

        /// <summary>
        /// Doubles the capacity of all parallel arrays.
        /// <para>
        /// Triggered automatically when Count exceeds current capacity.
        /// This is an expensive operation (allocation + copy), so InitialCapacity should be tuned to average use cases.
        /// </para>
        /// </summary>
        private void Resize()
        {
            int newCap = _capacity * 2;

            // C# `ref` resize is syntactic sugar for Array.Copy to a new, larger array.
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

        /// <summary>
        /// Reconstructs the full file path on-demand.
        /// <para>
        /// <b>Memory Optimization:</b> Storing the full path string for every file is redundant 
        /// when we already store BaseDir and RelativePath. Re-combining them only when needed (e.g., for IO or Tooltip)
        /// saves significant heap space.
        /// </para>
        /// </summary>
        public string GetFullPath(int index)
        {
            if (IsSummaryRows[index]) return "";

            // '!' operator is safe here because Add() ensures these are populated for non-summary rows.
            return Path.Combine(BaseDirectories[index]!, RelativePaths[index]!);
        }

        /// <summary>
        /// Returns the Hex string of the calculated hash, using a write-through cache.
        /// Hex conversion is CPU expensive; we compute it once and cache it until the hash changes.
        /// </summary>
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
        /// <b>Thread Safety:</b> No lock is required here because worker threads operate on unique, pre-assigned indices.
        /// </summary>
        public void SetResult(int index, byte[]? hash, string timeStr, ItemStatus status)
        {
            CalculatedHashes[index] = hash;
            CachedHashStrings[index] = null; // Invalidate cache so it regenerates on next read
            TimeStrs[index] = timeStr;
            Statuses[index] = status;
        }

        /// <summary>
        /// Removes an item at a specific index.
        /// <para>
        /// <b>Warning:</b> This is an O(N) operation requiring an array block copy for every parallel array.
        /// Avoid using this inside tight loops.
        /// </para>
        /// </summary>
        public void RemoveAt(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= Count) return;

                int copyCount = Count - index - 1;
                if (copyCount > 0)
                {
                    // Shift all data down by one index to fill the gap
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

                // Null out the last slot.
                // If we don't, the last item remains referenced at index [Count], leaking memory.
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