using System;

namespace SharpSFV.Models
{
    public enum JobStatus
    {
        Queued,
        InProgress,
        Paused, // Added for Pause functionality
        Done,
        Error
    }

    /// <summary>
    /// A specialized Structure of Arrays (SoA) container for Job Mode.
    /// <para>
    /// <b>Architecture Choice:</b>
    /// Separates the high-level concept of a "Job" (processing a folder) from the low-level "File" operations.
    /// The UI binds virtually to this store when in Job Mode.
    /// Uses parallel arrays to minimize object overhead and maintain cache locality during UI rendering loops.
    /// </para>
    /// </summary>
    public class JobStore
    {
        private const int InitialCapacity = 128;
        private readonly object _lock = new();

        public int Count { get; private set; } = 0;
        private int _capacity = InitialCapacity;

        // --- Parallel Arrays ---
        // Indexes across these arrays correspond to a single Job entity.

        public int[] Ids;
        public string[] Names;       // Display Name (usually folder name)
        public string[] RootPaths;   // Common Base Path for relative calculation
        public string[][] InputPaths;// Raw paths dropped by user (deferred scan until job starts)
        public double[] Progress;
        public JobStatus[] Statuses;
        public string[] OutputPaths; // Final location of the generated checksum file
        public string[] TimeStrs;    // Elapsed execution time

        public JobStore()
        {
            Ids = new int[InitialCapacity];
            Names = new string[InitialCapacity];
            RootPaths = new string[InitialCapacity];
            InputPaths = new string[InitialCapacity][];
            Progress = new double[InitialCapacity];
            Statuses = new JobStatus[InitialCapacity];
            OutputPaths = new string[InitialCapacity];
            TimeStrs = new string[InitialCapacity];
        }

        /// <summary>
        /// Thread-safe addition of a new Job.
        /// </summary>
        /// <param name="inputs">The raw array of file/folder paths dropped by the user.</param>
        /// <returns>The index of the new job.</returns>
        public int Add(string name, string rootPath, string[] inputs)
        {
            lock (_lock)
            {
                if (Count >= _capacity) Resize();

                int index = Count;
                Ids[index] = Count + 1; // Simple 1-based ID for display
                Names[index] = name;
                RootPaths[index] = rootPath;
                InputPaths[index] = inputs;
                Progress[index] = 0;
                Statuses[index] = JobStatus.Queued;
                OutputPaths[index] = "";
                TimeStrs[index] = "";

                Count++;
                return index;
            }
        }

        /// <summary>
        /// Updates progress. 
        /// <para>
        /// <b>Thread Safety:</b> Writing a <c>double</c> is atomic on 64-bit systems. 
        /// Since this is purely for visual feedback, we avoid the overhead of a lock.
        /// </para>
        /// </summary>
        public void UpdateProgress(int index, double percent)
        {
            Progress[index] = percent;
        }

        public void UpdateStatus(int index, JobStatus status, string output = "")
        {
            Statuses[index] = status;
            if (!string.IsNullOrEmpty(output)) OutputPaths[index] = output;
        }

        public void UpdateTime(int index, string timeStr)
        {
            TimeStrs[index] = timeStr;
        }

        /// <summary>
        /// Compacts the arrays by removing all jobs marked <see cref="JobStatus.Done"/>.
        /// <para>
        /// <b>Algorithm:</b> 
        /// Uses a Read/Write index approach (Two-Pointer algorithm).
        /// Iterates through the list once (O(N)). If an item is NOT done, it is moved to the <c>writeIndex</c>.
        /// This avoids the massive performance penalty of calling <c>Array.Copy</c> individually for every removed item (which would be O(N^2)).
        /// </para>
        /// </summary>
        public void RemoveCompleted()
        {
            lock (_lock)
            {
                int writeIndex = 0;
                for (int readIndex = 0; readIndex < Count; readIndex++)
                {
                    // Keep item if it is NOT Done (Keep Queued, InProgress, Paused, Error)
                    if (Statuses[readIndex] != JobStatus.Done)
                    {
                        if (writeIndex != readIndex)
                        {
                            // Shift data to the "Write" head
                            Ids[writeIndex] = Ids[readIndex];
                            Names[writeIndex] = Names[readIndex];
                            RootPaths[writeIndex] = RootPaths[readIndex];
                            InputPaths[writeIndex] = InputPaths[readIndex];
                            Progress[writeIndex] = Progress[readIndex];
                            Statuses[writeIndex] = Statuses[readIndex];
                            OutputPaths[writeIndex] = OutputPaths[readIndex];
                            TimeStrs[writeIndex] = TimeStrs[readIndex];
                        }
                        writeIndex++;
                    }
                }

                // Clean up the tail to prevent memory leaks (dereference managed objects)
                for (int i = writeIndex; i < Count; i++)
                {
                    Names[i] = null!;
                    RootPaths[i] = null!;
                    InputPaths[i] = null!;
                    OutputPaths[i] = null!;
                    TimeStrs[i] = null!;
                    // Value types (Status, Id, Progress) do not need clearing
                }

                Count = writeIndex;
            }
        }

        private void Resize()
        {
            int newCap = _capacity * 2;
            Array.Resize(ref Ids, newCap);
            Array.Resize(ref Names, newCap);
            Array.Resize(ref RootPaths, newCap);
            Array.Resize(ref InputPaths, newCap);
            Array.Resize(ref Progress, newCap);
            Array.Resize(ref Statuses, newCap);
            Array.Resize(ref OutputPaths, newCap);
            Array.Resize(ref TimeStrs, newCap);
            _capacity = newCap;
        }

        public void Clear()
        {
            lock (_lock) { Count = 0; }
        }
    }
}