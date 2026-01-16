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
using System.Text; // Required for StringBuilder
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

            // Standard Mode Setup
            SetupUIForMode("Creation");

            // Hide comments in Creation mode
            if (_commentsPanel != null) _commentsPanel.Visible = false;

            _fileStore.Clear();
            _displayIndices.Clear();
            UpdateDisplayList();

            // Determine Processing Mode
            bool isHDD = false;
            if (_settings.ProcessingMode == ProcessingMode.HDD)
            {
                isHDD = true;
            }
            else if (_settings.ProcessingMode == ProcessingMode.SSD)
            {
                isHDD = false;
            }
            else // Auto
            {
                if (paths.Length > 0)
                {
                    string testPath = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!;
                    isHDD = DriveDetector.IsRotational(testPath);
                }
            }

            this.Text = $"SharpSFV - Creating... [{(isHDD ? "HDD/Seq" : "SSD/Par")}]";

            Stopwatch globalSw = Stopwatch.StartNew();

            // Execute Engine with UI updates ENABLED (true)
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

                    // FIFO: Find next Queued job
                    for (int i = 0; i < _jobStore.Count; i++)
                    {
                        if (_jobStore.Statuses[i] == JobStatus.Queued)
                        {
                            currentJobIdx = i;
                            break;
                        }
                    }

                    if (currentJobIdx == -1) break; // No more jobs

                    // FIX: Ensure UI controls (Pause/Stop) are enabled when we find work
                    if (!_isProcessing) SetProcessingState(true);

                    _jobStore.UpdateStatus(currentJobIdx, JobStatus.InProgress);

                    // Update Stats (One-off, no throttling needed for status change)
                    this.BeginInvoke(new Action(() => {
                        lvFiles.Invalidate();
                        UpdateJobStats();
                    }));

                    string[] inputs = _jobStore.InputPaths[currentJobIdx];
                    string rootPath = _jobStore.RootPaths[currentJobIdx];
                    int jobId = _jobStore.Ids[currentJobIdx];
                    string jobName = _jobStore.Names[currentJobIdx];

                    // Start Timing the Job
                    Stopwatch jobSw = Stopwatch.StartNew();

                    // Determine Processing Mode for this Job
                    bool isHDD = false;
                    if (_settings.ProcessingMode == ProcessingMode.HDD)
                    {
                        isHDD = true;
                    }
                    else if (_settings.ProcessingMode == ProcessingMode.SSD)
                    {
                        isHDD = false;
                    }
                    else // Auto
                    {
                        if (inputs.Length > 0)
                        {
                            string testPath = Directory.Exists(inputs[0]) ? inputs[0] : Path.GetDirectoryName(inputs[0])!;
                            isHDD = DriveDetector.IsRotational(testPath);
                        }
                    }

                    _fileStore.Clear();
                    _displayIndices.Clear();

                    // Execute Hashing
                    // Note: updateFileList is 'false' for Job Mode to save memory/cpu
                    await ExecuteHashingEngine(inputs, rootPath, isHDD, false, (curr, total, ok, bad) =>
                    {
                        if (total > 0)
                        {
                            double pct = (double)curr / total * 100.0;
                            _jobStore.UpdateProgress(currentJobIdx, pct);
                        }

                        // --- OPTIMIZATION: Drop-Frame UI Throttling ---
                        long now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref _lastUiUpdateTick) < 100) return;

                        if (Interlocked.CompareExchange(ref _uiBusy, 1, 0) == 0)
                        {
                            Interlocked.Exchange(ref _lastUiUpdateTick, now);

                            this.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    lvFiles.Invalidate();
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _uiBusy, 0);
                                }
                            }));
                        }
                    });

                    // Generate Filename
                    string algoExt = GetExtensionForAlgo(_currentHashType);
                    string safeRootName = string.Join("_", jobName.Split(Path.GetInvalidFileNameChars()));
                    string fileName = $"{jobId}.{safeRootName}{algoExt}";
                    string fullSavePath = Path.Combine(rootPath, fileName);

                    // Save
                    bool hasErrors = SaveChecksumFileForJob(fullSavePath);

                    // Stop Timing
                    jobSw.Stop();
                    string jobTimeStr = $"{jobSw.ElapsedMilliseconds} ms";
                    _jobStore.UpdateTime(currentJobIdx, jobTimeStr);

                    // Finalize Job
                    JobStatus finalStatus = hasErrors ? JobStatus.Error : JobStatus.Done;
                    _jobStore.UpdateStatus(currentJobIdx, finalStatus, fullSavePath);
                    _jobStore.UpdateProgress(currentJobIdx, 100.0);

                    // Clean Memory
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
                // FIX: Disable UI controls only when the entire queue is finished/stopped
                SetProcessingState(false);
                _isJobQueueRunning = false;
            }
        }

        // --- UI THROTTLING LOGIC ---

        private void ThrottledUiUpdate(int current, int total, int ok, int bad, int missing)
        {
            // 1. Force update if complete (100%)
            // If total is known (>0) and current == total, we always let it through.
            bool isComplete = (total > 0 && current >= total);

            if (!isComplete)
            {
                // 2. Pure Time-Based Check (100ms / 10 FPS)
                // We use Environment.TickCount64 for low-overhead millisecond timing.
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref _lastUiUpdateTick);

                if (now - last < 100) return;
            }

            // 3. Drop-Frame Mechanism
            // If the UI thread is still painting the previous frame (_uiBusy == 1),
            // we skip this update request to prevent message pump saturation.
            if (Interlocked.CompareExchange(ref _uiBusy, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _lastUiUpdateTick, Environment.TickCount64);

                this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Safely handle the progress bar max
                        int safeTotal = (total <= 0) ? _fileStore.Count : total;
                        if (safeTotal <= 0) safeTotal = 1; // Prevent div/0 visuals

                        if (progressBarTotal.Maximum != safeTotal)
                            progressBarTotal.Maximum = safeTotal;

                        // Ensure Value doesn't exceed Maximum
                        progressBarTotal.Value = Math.Min(current, safeTotal);

                        UpdateStats(current, safeTotal, ok, bad, missing);

                        // IMPORTANT: Invalidate the ListView to repaint rows (Colors/Status text)
                        lvFiles.Invalidate();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _uiBusy, 0);
                    }
                }));
            }
        }

        // --- SHARED HASHING ENGINE ---
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
                        try { _pauseEvent.Wait(_cts.Token); } catch { break; }

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
                                    try { _pauseEvent.Wait(_cts.Token); } catch { break; }

                                    await ProcessFileEntry(file, pooledBase, originalIndex++, channel.Writer, uiBatch);

                                    // Periodic Flush Logic
                                    // 1. Always update the atomic total counter (FIX: This runs even if updateFileList is false)
                                    Interlocked.Exchange(ref totalFilesDiscovered, originalIndex);

                                    // 2. Conditionally update UI list
                                    if (updateFileList && (uiBatch.Count >= 2000 || (DateTime.UtcNow.Ticks - lastBatchTime > 2500000)))
                                    {
                                        FlushUiBatch();
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    // Final flush
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
                                // --- CHECK PAUSE & CANCEL ---
                                try { _pauseEvent.Wait(_cts.Token); }
                                catch (OperationCanceledException) { return; }

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

                                // Always calculate time, regardless of visibility settings
                                long endTick = Stopwatch.GetTimestamp();
                                double elapsedMs = (double)(endTick - startTick) * 1000 / Stopwatch.Frequency;
                                string timeStr = $"{(long)elapsedMs} ms";

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

                                // Pass current 'totalFilesDiscovered' (might be updating while producer runs)
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

        private bool SaveChecksumFileForJob(string fullPath)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(fullPath))
                {
                    if (_settings.EnableChecksumComments)
                    {
                        sw.WriteLine($"; Generated by SharpSFV (Job Mode)");
                        sw.WriteLine($"; Algorithm: {_currentHashType}");
                    }

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

        private async Task RunVerification(string checkFilePath)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();

            GCLatencyMode oldMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            SetupUIForMode("Verification");

            string baseFolder = Path.GetDirectoryName(checkFilePath) ?? "";
            string ext = Path.GetExtension(checkFilePath).ToLowerInvariant();

            HashType verificationAlgo = _currentHashType;
            if (ext == ".sfv") verificationAlgo = HashType.Crc32;
            else if (ext == ".md5") verificationAlgo = HashType.MD5;
            else if (ext == ".sha1") verificationAlgo = HashType.SHA1;
            else if (ext == ".sha256") verificationAlgo = HashType.SHA256;
            else if (ext == ".xxh3") verificationAlgo = HashType.XXHASH3;

            SetAlgorithm(verificationAlgo);

            var parsedLines = new List<(byte[] ExpectedHash, string Filename)>();

            // --- NEW: Comment Parser ---
            StringBuilder sbComments = new StringBuilder();

            try
            {
                foreach (var line in await File.ReadAllLinesAsync(checkFilePath))
                {
                    string trimmed = line.TrimStart();

                    // Detect Comments starting with //
                    if (trimmed.StartsWith("//"))
                    {
                        sbComments.AppendLine(trimmed.Substring(2).Trim());
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line) || trimmed.StartsWith(";")) continue;

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

            // --- DISPLAY COMMENTS ---
            string commentText = sbComments.ToString();
            if (_txtComments != null) _txtComments.Text = commentText;
            if (_commentsPanel != null) _commentsPanel.Visible = !string.IsNullOrWhiteSpace(commentText);

            _fileStore.Clear();
            _displayIndices.Clear();

            int missingCount = 0;
            int loadIndex = 0;

            string pooledBase = StringPool.GetOrAdd(baseFolder);

            foreach (var entry in parsedLines)
            {
                string pooledName = StringPool.GetOrAdd(Path.GetFileName(entry.Filename));
                string pooledRel = StringPool.GetOrAdd(entry.Filename);

                int idx = _fileStore.Add(pooledName, pooledRel, pooledBase, loadIndex++, ItemStatus.Queued, entry.ExpectedHash);
                _displayIndices.Add(idx);
            }

            _displayIndices.Sort((a, b) => string.Compare(_fileStore.GetFullPath(a), _fileStore.GetFullPath(b), StringComparison.OrdinalIgnoreCase));
            UpdateDisplayList();

            UpdateStats(0, _fileStore.Count, 0, 0, missingCount);
            progressBarTotal.Maximum = _fileStore.Count;

            int completed = 0, okCount = 0, badCount = 0;

            bool isHDD = false;
            if (_settings.ProcessingMode == ProcessingMode.HDD) isHDD = true;
            else if (_settings.ProcessingMode == ProcessingMode.SSD) isHDD = false;
            else
            {
                string root = Path.GetPathRoot(baseFolder) ?? "";
                if (!string.IsNullOrEmpty(root)) isHDD = DriveDetector.IsRotational(root);
            }

            int consumerCount = isHDD ? 1 : Environment.ProcessorCount;
            this.Text = $"SharpSFV - Verifying... [{(isHDD ? "HDD/Seq" : "SSD/Par")}]";

            Stopwatch globalSw = Stopwatch.StartNew();
            var channel = Channel.CreateBounded<FileJob>(50000);

            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (int idx in _displayIndices)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                        try { _pauseEvent.Wait(_cts.Token); } catch { break; }

                        string fullPath = _fileStore.GetFullPath(idx);
                        byte[]? expected = _fileStore.ExpectedHashes[idx];

                        if (File.Exists(fullPath))
                        {
                            _fileStore.Statuses[idx] = ItemStatus.Pending;
                            await channel.Writer.WriteAsync(new FileJob(idx, fullPath, expected), _cts.Token);
                        }
                        else
                        {
                            _fileStore.Statuses[idx] = ItemStatus.Missing;
                            Interlocked.Increment(ref missingCount);
                            Interlocked.Increment(ref completed);
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
                                try { _pauseEvent.Wait(_cts.Token); }
                                catch (OperationCanceledException) { return; }

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
                                                var streamToHash = new ProgressStream(fs, progress, len);
                                                calculatedHash = HashHelper.ComputeHashSync(streamToHash, verificationAlgo, sharedBuffer, reusedAlgo);
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

                                // Always calculate time, regardless of visibility settings
                                long endTick = Stopwatch.GetTimestamp();
                                double elapsedMs = (double)(endTick - startTick) * 1000 / Stopwatch.Frequency;
                                string timeStr = $"{(long)elapsedMs} ms";

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
    }
}