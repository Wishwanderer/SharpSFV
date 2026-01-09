using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Buffers;
using SharpSFV.Interop;
using SharpSFV.Models;
using SharpSFV.Utils;

namespace SharpSFV
{
    public partial class Form1
    {
        // --- STANDARD WRAPPER ---
        private async Task RunHashCreation(string[] paths, string baseDirectory)
        {
            SetProcessingState(true);

            SetupUIForMode("Creation");
            _fileStore.Clear();
            _displayIndices.Clear();
            UpdateDisplayList();

            bool isHDD = false;
            if (_settings.ProcessingMode == ProcessingMode.HDD) isHDD = true;
            else if (_settings.ProcessingMode == ProcessingMode.SSD) isHDD = false;
            else
            {
                if (paths.Length > 0)
                {
                    string testPath = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!;
                    isHDD = DriveDetector.IsRotational(testPath);
                }
            }

            this.Text = $"SharpSFV - Creating... [{(isHDD ? "HDD/Seq" : "SSD/Par")}]";

            Stopwatch globalSw = Stopwatch.StartNew();

            int completed = await ExecuteHashingEngine(paths, baseDirectory, isHDD, true, (curr, total, ok, bad) =>
            {
                ThrottledUiUpdate(curr, total, ok, 0, bad);
            });

            globalSw.Stop();
            HandleCompletion(completed, completed, 0, 0, globalSw);
        }

        // --- JOB QUEUE PROCESSOR ---
        private async Task ProcessJobQueue()
        {
            if (_isJobQueueRunning) return;
            _isJobQueueRunning = true;

            try
            {
                while (true)
                {
                    int currentJobIdx = -1;

                    for (int i = 0; i < _jobStore.Count; i++)
                    {
                        if (_jobStore.Statuses[i] == JobStatus.Queued)
                        {
                            currentJobIdx = i;
                            break;
                        }
                    }

                    if (currentJobIdx == -1) break;

                    _jobStore.UpdateStatus(currentJobIdx, JobStatus.InProgress);

                    this.BeginInvoke(new Action(() => {
                        lvFiles.Invalidate();
                        UpdateJobStats();
                    }));

                    string[] inputs = _jobStore.InputPaths[currentJobIdx];
                    string rootPath = _jobStore.RootPaths[currentJobIdx];
                    int jobId = _jobStore.Ids[currentJobIdx];
                    string jobName = _jobStore.Names[currentJobIdx];

                    bool isHDD = false;
                    if (_settings.ProcessingMode == ProcessingMode.HDD) isHDD = true;
                    else if (_settings.ProcessingMode == ProcessingMode.SSD) isHDD = false;
                    else
                    {
                        if (inputs.Length > 0)
                        {
                            string testPath = Directory.Exists(inputs[0]) ? inputs[0] : Path.GetDirectoryName(inputs[0])!;
                            isHDD = DriveDetector.IsRotational(testPath);
                        }
                    }

                    _isProcessing = true;
                    _fileStore.Clear();
                    _displayIndices.Clear();

                    await ExecuteHashingEngine(inputs, rootPath, isHDD, false, (curr, total, ok, bad) =>
                    {
                        if (total > 0)
                        {
                            double pct = (double)curr / total * 100.0;
                            _jobStore.UpdateProgress(currentJobIdx, pct);
                        }

                        // Drop-frame throttling for Job Mode
                        long now = DateTime.UtcNow.Ticks;
                        if (now - Interlocked.Read(ref _lastUiUpdateTick) < 1000000) return;

                        if (Interlocked.CompareExchange(ref _uiBusy, 1, 0) == 0)
                        {
                            Interlocked.Exchange(ref _lastUiUpdateTick, now);
                            this.BeginInvoke(new Action(() =>
                            {
                                try { lvFiles.Invalidate(); }
                                finally { Interlocked.Exchange(ref _uiBusy, 0); }
                            }));
                        }
                    });

                    _isProcessing = false;

                    string algoExt = GetExtensionForAlgo(_currentHashType);
                    string safeRootName = string.Join("_", jobName.Split(Path.GetInvalidFileNameChars()));
                    string fileName = $"{jobId}.{safeRootName}{algoExt}";
                    string fullSavePath = Path.Combine(rootPath, fileName);

                    bool hasErrors = SaveChecksumFileForJob(fullSavePath);

                    JobStatus finalStatus = hasErrors ? JobStatus.Error : JobStatus.Done;
                    _jobStore.UpdateStatus(currentJobIdx, finalStatus, fullSavePath);
                    _jobStore.UpdateProgress(currentJobIdx, 100.0);

                    _fileStore.Clear();
                    GC.Collect();

                    this.BeginInvoke(new Action(() => {
                        lvFiles.Invalidate();
                        UpdateJobStats();
                    }));
                }
            }
            finally
            {
                _isJobQueueRunning = false;
            }
        }

        // --- SHARED HASHING ENGINE (CREATION ONLY) ---
        private async Task<int> ExecuteHashingEngine(string[] paths, string baseDirectory, bool isHDD, bool updateFileList, Action<int, int, int, int> progressCallback)
        {
            _cts = new CancellationTokenSource();
            GCLatencyMode oldMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            int completed = 0, okCount = 0, errorCount = 0;
            int totalFilesDiscovered = 0;

            int consumerCount = isHDD ? 1 : Environment.ProcessorCount;

            var channel = Channel.CreateBounded<FileJob>(new BoundedChannelOptions(50000)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            var producer = Task.Run(async () =>
            {
                try
                {
                    int originalIndex = 0;
                    var uiBatch = new List<int>(2000);
                    long lastBatchTime = DateTime.UtcNow.Ticks;
                    string pooledBase = StringPool.GetOrAdd(baseDirectory);

                    void FlushUiBatch()
                    {
                        if (uiBatch.Count == 0 || !updateFileList) return;
                        var snapshot = uiBatch.ToList();
                        uiBatch.Clear();

                        this.BeginInvoke(new Action(() =>
                        {
                            Win32Storage.SuspendDrawing(lvFiles);
                            try
                            {
                                _displayIndices.AddRange(snapshot);
                                lvFiles.VirtualListSize = _displayIndices.Count;
                                progressBarTotal.Maximum = _displayIndices.Count;
                            }
                            finally { Win32Storage.ResumeDrawing(lvFiles); }
                        }));
                        lastBatchTime = DateTime.UtcNow.Ticks;
                    }

                    foreach (string path in paths)
                    {
                        if (_cts.Token.IsCancellationRequested) break;

                        if (File.Exists(path))
                        {
                            await ProcessFileEntry(path, pooledBase, originalIndex++, channel.Writer, uiBatch);
                        }
                        else if (Directory.Exists(path))
                        {
                            bool recursive = _settings.ShowAdvancedBar ? _settings.ScanRecursive : true;
                            var opts = new EnumerationOptions
                            {
                                IgnoreInaccessible = true,
                                RecurseSubdirectories = recursive,
                                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
                            };
                            try
                            {
                                foreach (string file in Directory.EnumerateFiles(path, "*", opts))
                                {
                                    if (_cts.Token.IsCancellationRequested) break;
                                    await ProcessFileEntry(file, pooledBase, originalIndex++, channel.Writer, uiBatch);

                                    if (updateFileList && (uiBatch.Count >= 2000 || (DateTime.UtcNow.Ticks - lastBatchTime > 2500000)))
                                    {
                                        Interlocked.Exchange(ref totalFilesDiscovered, originalIndex);
                                        FlushUiBatch();
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    Interlocked.Exchange(ref totalFilesDiscovered, originalIndex);
                    if (updateFileList) FlushUiBatch();
                }
                finally
                {
                    channel.Writer.Complete();
                }
            });

            var consumers = new Task[consumerCount];
            for (int i = 0; i < consumerCount; i++)
            {
                consumers[i] = Task.Run(async () =>
                {
                    byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    HashAlgorithm? reusedAlgo = null;

                    try
                    {
                        if (_currentHashType == HashType.MD5) reusedAlgo = MD5.Create();
                        else if (_currentHashType == HashType.SHA1) reusedAlgo = SHA1.Create();
                        else if (_currentHashType == HashType.SHA256) reusedAlgo = SHA256.Create();

                        while (await channel.Reader.WaitToReadAsync())
                        {
                            while (channel.Reader.TryRead(out FileJob job))
                            {
                                if (_cts.Token.IsCancellationRequested) return;

                                long startTick = Stopwatch.GetTimestamp();
                                byte[]? hashBytes = null;
                                ItemStatus status = ItemStatus.Pending;

                                try
                                {
                                    using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
                                        job.FullPath,
                                        FileMode.Open,
                                        FileAccess.Read,
                                        FileShare.Read,
                                        FileOptions.SequentialScan))
                                    {
                                        long len = RandomAccess.GetLength(handle);
                                        if (len > LargeFileThreshold)
                                        {
                                            using (var fs = new FileStream(handle, FileAccess.Read))
                                            {
                                                if (!_isJobMode) AddActiveJob(job.Index, Path.GetFileName(job.FullPath));

                                                var progress = new Progress<double>(p => UpdateActiveJob(job.Index, p));
                                                using (var ps = new ProgressStream(fs, progress, len))
                                                {
                                                    hashBytes = HashHelper.ComputeHashSync(ps, _currentHashType, sharedBuffer, reusedAlgo);
                                                }

                                                if (!_isJobMode) RemoveActiveJob(job.Index);
                                            }
                                        }
                                        else
                                        {
                                            hashBytes = HashHelper.ComputeHashHandle(handle, _currentHashType, sharedBuffer, reusedAlgo);
                                        }
                                    }
                                }
                                catch { status = ItemStatus.Error; }

                                long endTick = Stopwatch.GetTimestamp();
                                string timeStr = "";
                                if (_settings.ShowTimeTab)
                                {
                                    double elapsedMs = (double)(endTick - startTick) * 1000 / Stopwatch.Frequency;
                                    timeStr = $"{(long)elapsedMs} ms";
                                }

                                if (hashBytes == null || status == ItemStatus.Error)
                                {
                                    status = ItemStatus.Error;
                                    Interlocked.Increment(ref errorCount);
                                }
                                else
                                {
                                    status = ItemStatus.OK;
                                    Interlocked.Increment(ref okCount);
                                }

                                _fileStore.SetResult(job.Index, hashBytes, timeStr, status);

                                int currentCompleted = Interlocked.Increment(ref completed);
                                progressCallback(currentCompleted, totalFilesDiscovered, okCount, errorCount);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(sharedBuffer);
                        reusedAlgo?.Dispose();
                    }
                });
            }

            await Task.WhenAll(consumers);
            await producer;

            GCSettings.LatencyMode = oldMode;
            return completed;
        }

        private async Task RunVerification(string checkFilePath)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();

            GCLatencyMode oldMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            SetupUIForMode("Verification");

            string baseFolder = Path.GetDirectoryName(checkFilePath) ?? "";
            string ext = Path.GetExtension(checkFilePath).ToLowerInvariant();

            // Set Algo based on file extension
            HashType verificationAlgo = _currentHashType;
            if (ext == ".sfv") verificationAlgo = HashType.Crc32;
            else if (ext == ".md5") verificationAlgo = HashType.MD5;
            else if (ext == ".sha1") verificationAlgo = HashType.SHA1;
            else if (ext == ".sha256") verificationAlgo = HashType.SHA256;
            else if (ext == ".xxh3") verificationAlgo = HashType.XxHash3;

            SetAlgorithm(verificationAlgo);

            var parsedLines = new List<(byte[] ExpectedHash, string Filename)>();
            try
            {
                foreach (var line in await File.ReadAllLinesAsync(checkFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(";")) continue;

                    string expectedHashStr = "", filename = "";
                    Match matchA = Regex.Match(line, @"^\s*([0-9a-fA-F]+)\s+\*?(.*?)\s*$");
                    Match matchB = Regex.Match(line, @"^\s*(.*?)\s+([0-9a-fA-F]{8})\s*$");

                    if (verificationAlgo == HashType.Crc32 && matchB.Success) { filename = matchB.Groups[1].Value.Trim(); expectedHashStr = matchB.Groups[2].Value; }
                    else if (matchA.Success) { expectedHashStr = matchA.Groups[1].Value.Trim(); filename = matchA.Groups[2].Value.Trim(); }

                    if (!string.IsNullOrEmpty(expectedHashStr))
                    {
                        try { parsedLines.Add((Convert.FromHexString(expectedHashStr), filename)); } catch { }
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("Error parsing: " + ex.Message); SetProcessingState(false); return; }

            _fileStore.Clear();
            _displayIndices.Clear();

            int missingCount = 0; // Updated dynamically by workers now
            int loadIndex = 0;

            string pooledBase = StringPool.GetOrAdd(baseFolder);

            // --- OPTIMIZATION: NO File.Exists here! Pure memory load ---
            foreach (var entry in parsedLines)
            {
                string pooledName = StringPool.GetOrAdd(Path.GetFileName(entry.Filename));
                string pooledRel = StringPool.GetOrAdd(entry.Filename);

                // Set initial status to QUEUED. Do NOT check disk yet.
                int idx = _fileStore.Add(pooledName, pooledRel, pooledBase, loadIndex++, ItemStatus.Queued, entry.ExpectedHash);
                _displayIndices.Add(idx);
            }

            // UI Refresh - User sees list instantly
            _displayIndices.Sort((a, b) => string.Compare(_fileStore.GetFullPath(a), _fileStore.GetFullPath(b), StringComparison.OrdinalIgnoreCase));
            UpdateDisplayList();
            UpdateStats(0, _fileStore.Count, 0, 0, 0); // Zero stats initially
            progressBarTotal.Maximum = _fileStore.Count;

            int completed = 0, okCount = 0, badCount = 0;

            // Determine Processing Mode (Use Auto logic if needed, but defer heavy checks)
            bool isHDD = false;
            if (_settings.ProcessingMode == ProcessingMode.HDD) isHDD = true;
            else if (_settings.ProcessingMode == ProcessingMode.SSD) isHDD = false;
            else
            {
                // Simple heuristic: Check the root drive type only once
                string root = Path.GetPathRoot(baseFolder) ?? "";
                if (!string.IsNullOrEmpty(root)) isHDD = DriveDetector.IsRotational(root);
            }

            int consumerCount = isHDD ? 1 : Environment.ProcessorCount;
            this.Text = $"SharpSFV - Verifying... [{(isHDD ? "HDD/Seq" : "SSD/Par")}]";

            Stopwatch globalSw = Stopwatch.StartNew();
            var channel = Channel.CreateBounded<FileJob>(50000);

            // --- MODIFIED PRODUCER: Checks Existence Here ---
            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (int idx in _displayIndices)
                    {
                        if (_cts.Token.IsCancellationRequested) break;

                        string fullPath = _fileStore.GetFullPath(idx);
                        byte[]? expected = _fileStore.ExpectedHashes[idx];

                        // Perform File System Check on Background Thread
                        if (File.Exists(fullPath))
                        {
                            // File exists -> Send to Hasher
                            // Update Status to Pending (Processing)
                            _fileStore.Statuses[idx] = ItemStatus.Pending;
                            await channel.Writer.WriteAsync(new FileJob(idx, fullPath, expected), _cts.Token);
                        }
                        else
                        {
                            // File Missing -> Mark immediately
                            _fileStore.Statuses[idx] = ItemStatus.Missing;
                            Interlocked.Increment(ref missingCount);
                            Interlocked.Increment(ref completed);

                            // Trigger UI update for the missing file
                            ThrottledUiUpdate(completed, -1, okCount, badCount, missingCount);
                        }
                    }
                }
                finally { channel.Writer.Complete(); }
            });

            var consumers = new Task[consumerCount];
            for (int i = 0; i < consumerCount; i++)
            {
                consumers[i] = Task.Run(async () =>
                {
                    byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    HashAlgorithm? reusedAlgo = null;

                    try
                    {
                        if (verificationAlgo == HashType.MD5) reusedAlgo = MD5.Create();
                        else if (verificationAlgo == HashType.SHA1) reusedAlgo = SHA1.Create();
                        else if (verificationAlgo == HashType.SHA256) reusedAlgo = SHA256.Create();

                        while (await channel.Reader.WaitToReadAsync())
                        {
                            while (channel.Reader.TryRead(out FileJob job))
                            {
                                if (_cts.Token.IsCancellationRequested) return;

                                long startTick = Stopwatch.GetTimestamp();
                                byte[]? calculatedHash = null;

                                try
                                {
                                    using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
                                        job.FullPath,
                                        FileMode.Open,
                                        FileAccess.Read,
                                        FileShare.Read,
                                        FileOptions.SequentialScan))
                                    {
                                        long len = RandomAccess.GetLength(handle);
                                        if (len > LargeFileThreshold)
                                        {
                                            using (var fs = new FileStream(handle, FileAccess.Read))
                                            {
                                                AddActiveJob(job.Index, Path.GetFileName(job.FullPath));
                                                var progress = new Progress<double>(p => UpdateActiveJob(job.Index, p));
                                                using (var ps = new ProgressStream(fs, progress, len))
                                                {
                                                    calculatedHash = HashHelper.ComputeHashSync(ps, verificationAlgo, sharedBuffer, reusedAlgo);
                                                }
                                                RemoveActiveJob(job.Index);
                                            }
                                        }
                                        else
                                        {
                                            calculatedHash = HashHelper.ComputeHashHandle(handle, verificationAlgo, sharedBuffer, reusedAlgo);
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { return; }
                                catch { }

                                long endTick = Stopwatch.GetTimestamp();
                                string timeStr = "";
                                if (_settings.ShowTimeTab)
                                {
                                    double elapsedMs = (double)(endTick - startTick) * 1000 / Stopwatch.Frequency;
                                    timeStr = $"{(long)elapsedMs} ms";
                                }

                                bool isMatch = false;
                                if (calculatedHash != null && job.ExpectedHash != null)
                                {
                                    if (calculatedHash.SequenceEqual(job.ExpectedHash)) isMatch = true;
                                }

                                ItemStatus status;
                                if (isMatch)
                                {
                                    status = ItemStatus.OK;
                                    Interlocked.Increment(ref okCount);
                                }
                                else if (calculatedHash == null)
                                {
                                    status = ItemStatus.Error;
                                    Interlocked.Increment(ref badCount);
                                }
                                else
                                {
                                    status = ItemStatus.Bad;
                                    Interlocked.Increment(ref badCount);
                                }

                                _fileStore.SetResult(job.Index, calculatedHash, timeStr, status);

                                int currentCompleted = Interlocked.Increment(ref completed);
                                ThrottledUiUpdate(currentCompleted, -1, okCount, badCount, missingCount);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(sharedBuffer);
                        reusedAlgo?.Dispose();
                    }
                });
            }

            await Task.WhenAll(consumers);
            await producer;

            GCSettings.LatencyMode = oldMode;
            globalSw.Stop();
            HandleCompletion(completed, okCount, badCount, missingCount, globalSw, true);
        }

        // Helpers...
        private bool SaveChecksumFileForJob(string fullPath)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(fullPath))
                {
                    sw.WriteLine($"; Generated by SharpSFV (Job Mode)");
                    sw.WriteLine($"; Algorithm: {_currentHashType}");

                    int errorCount = 0;
                    for (int i = 0; i < _fileStore.Count; i++)
                    {
                        if (_fileStore.IsSummaryRows[i]) continue;
                        ItemStatus status = _fileStore.Statuses[i];
                        if (status == ItemStatus.Error || status == ItemStatus.Bad) errorCount++;

                        string hash = _fileStore.GetCalculatedHashString(i);
                        if (!hash.Contains("...") && status == ItemStatus.OK && !string.IsNullOrEmpty(hash))
                        {
                            string fileFullPath = _fileStore.GetFullPath(i);
                            string pathToWrite;
                            try { pathToWrite = Path.GetRelativePath(Path.GetDirectoryName(fullPath)!, fileFullPath); }
                            catch { pathToWrite = fileFullPath; }

                            if (_settings.ShowAdvancedBar && !string.IsNullOrEmpty(_settings.PathPrefix))
                            {
                                pathToWrite = Path.Combine(_settings.PathPrefix, pathToWrite);
                            }

                            sw.WriteLine($"{hash} *{pathToWrite}");
                        }
                    }
                    return errorCount > 0;
                }
            }
            catch { return true; }
        }

        private string GetExtensionForAlgo(HashType type)
        {
            return type switch
            {
                HashType.Crc32 => ".sfv",
                HashType.MD5 => ".md5",
                HashType.SHA1 => ".sha1",
                HashType.SHA256 => ".sha256",
                _ => ".xxh3"
            };
        }
    }
}