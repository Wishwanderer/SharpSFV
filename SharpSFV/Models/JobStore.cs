using System;

namespace SharpSFV.Models
{
    public enum JobStatus
    {
        Queued,
        InProgress,
        Done,
        Error
    }

    public class JobStore
    {
        private const int InitialCapacity = 128;
        private readonly object _lock = new();

        public int Count { get; private set; } = 0;
        private int _capacity = InitialCapacity;

        // Parallel Arrays
        public int[] Ids;
        public string[] Names;       // Job Name
        public string[] RootPaths;   // Common Base Path
        public string[][] InputPaths;// Raw paths dropped by user (deferred scan)
        public double[] Progress;
        public JobStatus[] Statuses;
        public string[] OutputPaths; // Where the hash file was saved

        public JobStore()
        {
            Ids = new int[InitialCapacity];
            Names = new string[InitialCapacity];
            RootPaths = new string[InitialCapacity];
            InputPaths = new string[InitialCapacity][];
            Progress = new double[InitialCapacity];
            Statuses = new JobStatus[InitialCapacity];
            OutputPaths = new string[InitialCapacity];
        }

        public int Add(string name, string rootPath, string[] inputs)
        {
            lock (_lock)
            {
                if (Count >= _capacity) Resize();

                int index = Count;
                Ids[index] = Count + 1; // n+1 Numbering
                Names[index] = name;
                RootPaths[index] = rootPath;
                InputPaths[index] = inputs;
                Progress[index] = 0;
                Statuses[index] = JobStatus.Queued;
                OutputPaths[index] = "";

                Count++;
                return index;
            }
        }

        public void UpdateProgress(int index, double percent)
        {
            // Atomic update, no lock needed for double
            Progress[index] = percent;
        }

        public void UpdateStatus(int index, JobStatus status, string output = "")
        {
            Statuses[index] = status;
            if (!string.IsNullOrEmpty(output)) OutputPaths[index] = output;
        }

        public void RemoveCompleted()
        {
            lock (_lock)
            {
                int writeIndex = 0;
                for (int readIndex = 0; readIndex < Count; readIndex++)
                {
                    // Keep item if it is NOT Done (Keep Queued, InProgress, Error)
                    if (Statuses[readIndex] != JobStatus.Done)
                    {
                        if (writeIndex != readIndex)
                        {
                            Ids[writeIndex] = Ids[readIndex];
                            Names[writeIndex] = Names[readIndex];
                            RootPaths[writeIndex] = RootPaths[readIndex];
                            InputPaths[writeIndex] = InputPaths[readIndex];
                            Progress[writeIndex] = Progress[readIndex];
                            Statuses[writeIndex] = Statuses[readIndex];
                            OutputPaths[writeIndex] = OutputPaths[readIndex];
                        }
                        writeIndex++;
                    }
                }

                // Clear the rest to release references (GC)
                for (int i = writeIndex; i < Count; i++)
                {
                    Names[i] = null!;
                    RootPaths[i] = null!;
                    InputPaths[i] = null!;
                    OutputPaths[i] = null!;
                    // Note: Status/Ids/Progress are value types, no need to clear
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
            _capacity = newCap;
        }

        public void Clear()
        {
            lock (_lock) { Count = 0; }
        }
    }
}