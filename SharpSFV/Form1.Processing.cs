using SharpSFV.Interop;
using SharpSFV.Models;
using SharpSFV.Utils;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        // --- METRICS STATE ---
        private long _totalBytesDetected = 0;
        private long _processedBytes = 0;
        private long _lastSpeedBytes = 0;
        private long _lastSpeedTick = 0;
        private string _cachedSpeedString = "";

        // --- STANDARD WRAPPER ---
        private async Task RunHashCreation(string[] paths, string baseDirectory)
        {
            SetProcessingState(true);

            // Reset Metrics
            _totalBytesDetected = 0;
            _processedBytes = 0;
            _lastSpeedBytes = 0;
            _lastSpeedTick = Environment.TickCount64;
            _cachedSpeedString = "";

            // UI Setup: Skip for Headless or Create Mode (Mini UI)
            if (!_isHeadless && !_isCreateMode)
            {
                SetupUIForMode("Creation");
                if (_commentsPanel != null) _commentsPanel.Visible = false;
            }

            _fileStore.Clear();
            _displayIndices.Clear();

            // Display List: Skip for Headless/Create Mode (No List View)
            if (!_isHeadless && !_isCreateMode) UpdateDisplayList();

            // Determine Processing Mode
            bool isHDD = false;
            if (_settings.ProcessingMode == ProcessingMode.HDD) isHDD = true;
            else if (_settings.ProcessingMode == ProcessingMode.SSD) isHDD = false;
            else // Auto
            {
                if (paths.Length > 0)
                {
                    string testPath = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!;
                    isHDD = DriveDetector.IsRotational(testPath);
                }
            }

            if (!_isHeadless && !_isCreateMode)
                this.Text = $"SharpSFV - Creating... [{(isHDD ? "HDD/Seq" : "SSD/Par")}]";
            else if (_isHeadless)
                Console.WriteLine($"Mode: {(isHDD ? "HDD (Sequential)" : "SSD (Parallel)")}");

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

                    if (!_isProcessing) SetProcessingState(true);

                    _jobStore.UpdateStatus(currentJobIdx, JobStatus.InProgress);

                    if (!_isHeadless)
                    {
                        this.BeginInvoke(new Action(() => {
                            lvFiles.Invalidate();
                            UpdateJobStats();
                        }));
                    }
                    else
                    {
                        Console.WriteLine($"Starting Job: {_jobStore.Names[currentJobIdx]}");
                    }

                    string[] inputs = _jobStore.InputPaths[currentJobIdx];
                    string rootPath = _jobStore.RootPaths[currentJobIdx];
                    int jobId = _jobStore.Ids[currentJobIdx];
                    string jobName = _jobStore.Names[currentJobIdx];

                    // Reset Job Metrics
                    _totalBytesDetected = 0;
                    _processedBytes = 0;
                    _lastSpeedBytes = 0;
                    _lastSpeedTick = Environment.TickCount64;

                    Stopwatch jobSw = Stopwatch.StartNew();

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

                    _fileStore.Clear();
                    _displayIndices.Clear();

                    await ExecuteHashingEngine(inputs, rootPath, isHDD, false, (curr, total, ok, bad) =>
                    {
                        if (total > 0)
                        {
                            double pct = (double)curr / total * 100.0;
                            _jobStore.UpdateProgress(currentJobIdx, pct);
                        }

                        ThrottledUiUpdate(curr, total, ok, 0, bad);
                    });

                    string algoExt = GetExtensionForAlgo(_currentHashType);
                    string safeRootName = string.Join("_", jobName.Split(Path.GetInvalidFileNameChars()));
                    string fileName = $"{jobId}.{safeRootName}{algoExt}";
                    string fullSavePath = Path.Combine(rootPath, fileName);

                    bool hasErrors = SaveChecksumFileForJob(fullSavePath);

                    jobSw.Stop();
                    string jobTimeStr = $"{jobSw.ElapsedMilliseconds} ms";
                    _jobStore.UpdateTime(currentJobIdx, jobTimeStr);

                    JobStatus finalStatus = hasErrors ? JobStatus.Error : JobStatus.Done;
                    _jobStore.UpdateStatus(currentJobIdx, finalStatus, fullSavePath);
                    _jobStore.UpdateProgress(currentJobIdx, 100.0);

                    _fileStore.Clear();
                    GC.Collect();

                    if (!_isHeadless)
                    {
                        this.BeginInvoke(new Action(() => {
                            lvFiles.Invalidate();
                            UpdateJobStats();
                        }));
                    }
                    else
                    {
                        Console.WriteLine($"Job Finished: {finalStatus} ({jobTimeStr})");
                    }
                }
            }
            finally
            {
                SetProcessingState(false);
                _isJobQueueRunning = false;
                if (_isHeadless) Application.Exit();
            }
        }

        // --- UI / CONSOLE THROTTLING LOGIC ---

        private void ThrottledUiUpdate(int current, int total, int ok, int bad, int missing)
        {
            // 1. Calculate Throughput & ETA (Every ~500ms)
            long now = Environment.TickCount64;
            if (_settings.ShowThroughputStats && (now - _lastSpeedTick > 500))
            {
                long currentBytes = Interlocked.Read(ref _processedBytes);
                long bytesDelta = currentBytes - _lastSpeedBytes;
                long timeDelta = now - _lastSpeedTick;

                if (timeDelta > 0)
                {
                    double speedBps = (double)bytesDelta / timeDelta * 1000.0;
                    double speedMBps = speedBps / (1024 * 1024);

                    string etaStr = "--:--";
                    long totalBytes = Interlocked.Read(ref _totalBytesDetected);
                    if (speedBps > 0 && totalBytes > currentBytes)
                    {
                        double secondsRemaining = (totalBytes - currentBytes) / speedBps;
                        if (secondsRemaining < 86400) // < 1 day
                        {
                            etaStr = TimeSpan.FromSeconds(secondsRemaining).ToString(@"hh\:mm\:ss");
                        }
                    }

                    _cachedSpeedString = $"Speed: {speedMBps:F1} MB/s | ETA: {etaStr}";
                }

                _lastSpeedTick = now;
                _lastSpeedBytes = currentBytes;
            }

            // 2. Pure Time-Based Throttle (Use _lastUiUpdateTick)
            if (current < total)
            {
                long last = Interlocked.Read(ref _lastUiUpdateTick);
                if (now - last < 100) return;
            }

            // 3. Drop-Frame Mechanism
            if (Interlocked.CompareExchange(ref _uiBusy, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _lastUiUpdateTick, now);

                // Headless Output
                if (_isHeadless)
                {
                    try
                    {
                        double percent = (total > 0) ? (double)current / total * 100.0 : 0;
                        string speedTxt = _settings.ShowThroughputStats ? $" | {_cachedSpeedString}" : "";
                        Console.Write($"\r{current}/{total} ({percent:F1}%) | OK: {ok} | BAD: {bad} | MISSING: {missing}{speedTxt}   ");
                    }
                    finally { Interlocked.Exchange(ref _uiBusy, 0); }
                    return;
                }

                // GUI Output
                this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        int safeTotal = (total <= 0) ? _fileStore.Count : total;
                        if (safeTotal <= 0) safeTotal = 1;

                        if (progressBarTotal.Maximum != safeTotal)
                            progressBarTotal.Maximum = safeTotal;

                        progressBarTotal.Value = Math.Min(current, safeTotal);

                        if (_taskbarList != null)
                        {
                            try { _taskbarList.SetProgressValue(this.Handle, (ulong)current, (ulong)safeTotal); } catch { }
                        }

                        if (_isCreateMode)
                        {
                            // Mini Mode
                            if (_lblProgress != null) _lblProgress.Text = $"Processing: {current} / {safeTotal}";
                        }
                        else if (_isJobMode)
                        {
                            // Job Mode
                            UpdateJobStats(_cachedSpeedString);
                            lvFiles.Invalidate();
                        }
                        else
                        {
                            // Standard Mode
                            UpdateStats(current, safeTotal, ok, bad, missing, _cachedSpeedString);
                            lvFiles.Invalidate();
                        }
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
            int bufferSize = isHDD ? 8 * 1024 * 1024 : 512 * 1024;

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
                        if (_isHeadless || _isCreateMode) { uiBatch.Clear(); return; }

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
                            try { Interlocked.Add(ref _totalBytesDetected, new FileInfo(path).Length); } catch { }
                            await ProcessFileEntry(path, pooledBase, originalIndex++, channel.Writer, uiBatch);
                        }
                        else if (Directory.Exists(path))
                        {
                            bool recursive = (_isCreateMode || _settings.ShowAdvancedBar) ? _settings.ScanRecursive : true;

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

                                    try { Interlocked.Add(ref _totalBytesDetected, new FileInfo(file).Length); } catch { }

                                    await ProcessFileEntry(file, pooledBase, originalIndex++, channel.Writer, uiBatch);

                                    Interlocked.Exchange(ref totalFilesDiscovered, originalIndex);

                                    if (updateFileList && (uiBatch.Count >= 2000 || (DateTime.UtcNow.Ticks - lastBatchTime > 2500000)))
                                    {
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
                    byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
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
                                try { _pauseEvent.Wait(_cts.Token); } catch (OperationCanceledException) { return; }
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
                                                if (!_isJobMode && !_isHeadless && !_isCreateMode) AddActiveJob(job.Index, Path.GetFileName(job.FullPath));

                                                var progress = new Progress<double>(p => UpdateActiveJob(job.Index, p));
                                                using (var ps = new ProgressStream(fs, progress, len))
                                                {
                                                    hashBytes = HashHelper.ComputeHashSync(ps, _currentHashType, sharedBuffer, reusedAlgo);
                                                }
                                                Interlocked.Add(ref _processedBytes, len);

                                                if (!_isJobMode && !_isHeadless && !_isCreateMode) RemoveActiveJob(job.Index);
                                            }
                                        }
                                        else
                                        {
                                            long pos = 0;
                                            int read;

                                            if (_currentHashType == HashType.XXHASH3)
                                            {
                                                var xx3 = new System.IO.Hashing.XxHash128();
                                                while ((read = RandomAccess.Read(handle, sharedBuffer, pos)) > 0)
                                                {
                                                    xx3.Append(new ReadOnlySpan<byte>(sharedBuffer, 0, read));
                                                    pos += read;
                                                    Interlocked.Add(ref _processedBytes, read);
                                                }
                                                hashBytes = xx3.GetCurrentHash();
                                            }
                                            else if (_currentHashType == HashType.Crc32)
                                            {
                                                var crc = new System.IO.Hashing.Crc32();
                                                while ((read = RandomAccess.Read(handle, sharedBuffer, pos)) > 0)
                                                {
                                                    crc.Append(new ReadOnlySpan<byte>(sharedBuffer, 0, read));
                                                    pos += read;
                                                    Interlocked.Add(ref _processedBytes, read);
                                                }
                                                hashBytes = crc.GetCurrentHash();
                                            }
                                            else if (reusedAlgo != null)
                                            {
                                                while ((read = RandomAccess.Read(handle, sharedBuffer, pos)) > 0)
                                                {
                                                    reusedAlgo.TransformBlock(sharedBuffer, 0, read, null, 0);
                                                    pos += read;
                                                    Interlocked.Add(ref _processedBytes, read);
                                                }
                                                reusedAlgo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                                hashBytes = reusedAlgo.Hash;
                                            }
                                        }
                                    }
                                }
                                catch { status = ItemStatus.Error; }

                                long endTick = Stopwatch.GetTimestamp();
                                double elapsedMs = (double)(endTick - startTick) * 1000 / Stopwatch.Frequency;
                                string timeStr = $"{(long)elapsedMs} ms";

                                ItemStatus statusFinal = ItemStatus.Error;

                                if (status == ItemStatus.Error || hashBytes == null)
                                {
                                    statusFinal = ItemStatus.Error;
                                    Interlocked.Increment(ref errorCount);
                                }
                                else if (job.ExpectedHash == null)
                                {
                                    // CREATION MODE
                                    statusFinal = ItemStatus.OK;
                                    Interlocked.Increment(ref okCount);
                                }
                                else
                                {
                                    // VERIFICATION MODE
                                    bool isMatch = hashBytes.SequenceEqual(job.ExpectedHash);
                                    if (isMatch)
                                    {
                                        statusFinal = ItemStatus.OK;
                                        Interlocked.Increment(ref okCount);
                                    }
                                    else
                                    {
                                        statusFinal = ItemStatus.Bad;
                                        // Since we are in creation flow (ExecuteHashingEngine), use errorCount variable.
                                        // Realistically this path is for mixed usage, but 'badCount' variable isn't in this scope.
                                        Interlocked.Increment(ref errorCount);
                                    }
                                }

                                _fileStore.SetResult(job.Index, hashBytes, timeStr, statusFinal);

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

        private string FindCommonBasePath(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();
            if (pathList.Count == 0) return "";
            if (pathList.Count == 1) return Directory.Exists(pathList[0]) ? pathList[0] : Path.GetDirectoryName(pathList[0]) ?? "";

            string commonPath = pathList[0];
            foreach (string path in pathList.Skip(1))
            {
                while (!path.StartsWith(commonPath, StringComparison.OrdinalIgnoreCase) && commonPath.Length > 0)
                {
                    commonPath = Path.GetDirectoryName(commonPath) ?? "";
                }
            }
            if (File.Exists(commonPath)) commonPath = Path.GetDirectoryName(commonPath) ?? "";

            return commonPath;
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

        // --- VERIFICATION ENGINE ---
        private async Task RunVerification(string checkFilePath)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();

            _totalBytesDetected = 0;
            _processedBytes = 0;
            _lastSpeedBytes = 0;
            _lastSpeedTick = Environment.TickCount64;
            _cachedSpeedString = "";

            GCLatencyMode oldMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            if (!_isHeadless) SetupUIForMode("Verification");

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
            StringBuilder sbComments = new StringBuilder();

            try
            {
                foreach (var line in await File.ReadAllLinesAsync(checkFilePath))
                {
                    string trimmed = line.TrimStart();
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
            catch (Exception ex)
            {
                if (_isHeadless) Console.WriteLine($"Error parsing: {ex.Message}");
                else MessageBox.Show("Error parsing: " + ex.Message);
                SetProcessingState(false);
                if (_isHeadless) Application.Exit();
                return;
            }

            if (!_isHeadless)
            {
                string commentText = sbComments.ToString();
                if (_txtComments != null) _txtComments.Text = commentText;
                if (_commentsPanel != null) _commentsPanel.Visible = !string.IsNullOrWhiteSpace(commentText);
            }

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

            if (!_isHeadless)
            {
                _displayIndices.Sort((a, b) => string.Compare(_fileStore.GetFullPath(a), _fileStore.GetFullPath(b), StringComparison.OrdinalIgnoreCase));
                UpdateDisplayList();
                UpdateStats(0, _fileStore.Count, 0, 0, missingCount);
                progressBarTotal.Maximum = _fileStore.Count;
            }
            else
            {
                Console.WriteLine($"Verifying {checkFilePath} ({_fileStore.Count} files)...");
            }

            int completed = 0, okCount = 0, badCount = 0;

            bool isHDD = false;
            if (_settings.ProcessingMode == ProcessingMode.HDD) isHDD = true;
            else if (_settings.ProcessingMode == ProcessingMode.SSD) isHDD = false;
            else
            {
                string root = Path.GetPathRoot(baseFolder) ?? "";
                if (!string.IsNullOrEmpty(root)) isHDD = DriveDetector.IsRotational(root);
            }

            if (!_isHeadless)
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
                            try { Interlocked.Add(ref _totalBytesDetected, new FileInfo(fullPath).Length); } catch { }
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

            int bufferSize = isHDD ? 8 * 1024 * 1024 : 512 * 1024;
            int consumerCount = isHDD ? 1 : Environment.ProcessorCount;
            var consumers = new Task[consumerCount];

            for (int i = 0; i < consumerCount; i++)
            {
                consumers[i] = Task.Run(async () =>
                {
                    byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
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
                                try { _pauseEvent.Wait(_cts.Token); } catch (OperationCanceledException) { return; }
                                if (_cts.Token.IsCancellationRequested) return;

                                long startTick = Stopwatch.GetTimestamp();
                                byte[]? calculatedHash = null;
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
                                                if (!_isHeadless) AddActiveJob(job.Index, Path.GetFileName(job.FullPath));
                                                var progress = new Progress<double>(p => UpdateActiveJob(job.Index, p));
                                                var streamToHash = new ProgressStream(fs, progress, len);
                                                calculatedHash = HashHelper.ComputeHashSync(streamToHash, verificationAlgo, sharedBuffer, reusedAlgo);
                                                Interlocked.Add(ref _processedBytes, len);
                                                if (!_isHeadless) RemoveActiveJob(job.Index);
                                            }
                                        }
                                        else
                                        {
                                            long pos = 0;
                                            int read;

                                            if (verificationAlgo == HashType.XXHASH3)
                                            {
                                                var xx3 = new System.IO.Hashing.XxHash128();
                                                while ((read = RandomAccess.Read(handle, sharedBuffer, pos)) > 0)
                                                {
                                                    xx3.Append(new ReadOnlySpan<byte>(sharedBuffer, 0, read));
                                                    pos += read;
                                                    Interlocked.Add(ref _processedBytes, read);
                                                }
                                                calculatedHash = xx3.GetCurrentHash();
                                            }
                                            else if (verificationAlgo == HashType.Crc32)
                                            {
                                                var crc = new System.IO.Hashing.Crc32();
                                                while ((read = RandomAccess.Read(handle, sharedBuffer, pos)) > 0)
                                                {
                                                    crc.Append(new ReadOnlySpan<byte>(sharedBuffer, 0, read));
                                                    pos += read;
                                                    Interlocked.Add(ref _processedBytes, read);
                                                }
                                                calculatedHash = crc.GetCurrentHash();
                                            }
                                            else if (reusedAlgo != null)
                                            {
                                                while ((read = RandomAccess.Read(handle, sharedBuffer, pos)) > 0)
                                                {
                                                    reusedAlgo.TransformBlock(sharedBuffer, 0, read, null, 0);
                                                    pos += read;
                                                    Interlocked.Add(ref _processedBytes, read);
                                                }
                                                reusedAlgo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                                calculatedHash = reusedAlgo.Hash;
                                            }
                                        }
                                    }
                                }
                                catch { status = ItemStatus.Error; }

                                long endTick = Stopwatch.GetTimestamp();
                                double elapsedMs = (double)(endTick - startTick) * 1000 / Stopwatch.Frequency;
                                string timeStr = $"{(long)elapsedMs} ms";

                                ItemStatus statusFinal;
                                if (status == ItemStatus.Error || calculatedHash == null)
                                {
                                    statusFinal = ItemStatus.Error;
                                    Interlocked.Increment(ref badCount);
                                }
                                else if (job.ExpectedHash != null && calculatedHash.SequenceEqual(job.ExpectedHash))
                                {
                                    statusFinal = ItemStatus.OK;
                                    Interlocked.Increment(ref okCount);
                                }
                                else
                                {
                                    statusFinal = ItemStatus.Bad;
                                    Interlocked.Increment(ref badCount);
                                }

                                _fileStore.SetResult(job.Index, calculatedHash, timeStr, statusFinal);

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