namespace SharpSFV.Models
{
    /// <summary>
    /// A lightweight, immutable data packet used to transfer work from the Producer (enumeration) 
    /// to the Consumer (hashing) threads via <see cref="System.Threading.Channels.Channel{T}"/>.
    /// <para>
    /// <b>Performance Note:</b> 
    /// Defined as a <c>readonly struct</c> (Value Type). 
    /// Instances are allocated on the Stack or inline within arrays, avoiding Heap allocation.
    /// This eliminates Garbage Collection pressure even when processing millions of files.
    /// </para>
    /// </summary>
    public readonly struct FileJob
    {
        /// <summary>
        /// The index into the global <see cref="FileStore"/> (Structure of Arrays).
        /// <para>
        /// <b>Usage:</b> The worker thread uses this index to write the calculated hash and status 
        /// back into the parallel arrays (e.g., <c>_fileStore.CalculatedHashes[Index] = ...</c>).
        /// </para>
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// The absolute file path required for I/O operations.
        /// <para>
        /// <b>Optimization:</b> Passed explicitly to allow the worker thread to open the file handle 
        /// without querying the <see cref="FileStore"/> (which would require acquiring a Read Lock, causing contention).
        /// </para>
        /// </summary>
        public readonly string FullPath;

        /// <summary>
        /// The expected hash for Verification mode, or <c>null</c> for Creation mode.
        /// <para>
        /// <b>Optimization:</b> Passed here so the worker thread can perform the comparison locally 
        /// without accessing shared memory.
        /// </para>
        /// </summary>
        public readonly byte[]? ExpectedHash;

        public FileJob(int index, string fullPath, byte[]? expectedHash)
        {
            Index = index;
            FullPath = fullPath;
            ExpectedHash = expectedHash;
        }
    }
}