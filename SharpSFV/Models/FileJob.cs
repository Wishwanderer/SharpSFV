namespace SharpSFV.Models
{
    /// <summary>
    /// OPTIMIZATION #2: Lightweight Job Struct.
    /// Replaces the FileItemData class in the processing channel.
    /// Passed by value to worker threads to avoid GC allocation per file.
    /// Contains only the data necessary to perform the I/O and Hash check.
    /// </summary>
    public readonly struct FileJob
    {
        /// <summary>
        /// Index into the global FileStore (SoA).
        /// Used by the worker to write results back to the arrays.
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// Full path to the file. 
        /// Passed explicitly to allow RandomAccess to open the handle without 
        /// querying the FileStore (avoiding lock contention).
        /// </summary>
        public readonly string FullPath;

        /// <summary>
        /// The expected hash (if in Verification mode).
        /// Passed here so the worker doesn't need to read from the main store.
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