using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpSFV.Interop;
using System.Buffers;
using SharpSFV.Models;
using SharpSFV.Utils;

namespace SharpSFV
{
    public partial class Form1
    {
        private async Task RunHashCreation(string[] paths, string baseDirectory)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();

            GCLatencyMode oldMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            SetupUIForMode("Creation");

            // SoA: Clear Store
            _fileStore.Clear();
            _displayIndices.Clear();
            UpdateDisplayList();

            int completed = 0, okCount = 0, errorCount = 0;

            bool isHDD = _settings.OptimizeForHDD;
            if (!isHDD && paths.Length > 0)
            {
                string testPath = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!;
                isHDD = DriveDetector.IsRotational(testPath);
            }

            int consumerCount = isHDD ? 1 : Environment.ProcessorCount;
            this.Text = $"SharpSFV - Creating... [{(isHDD ? "HDD/Seq" : "SSD/Par")}]";

            Stopwatch globalSw = Stopwatch.StartNew();

            // SoA: Channel carries Structs (FileJob) now
            var channel = Channel.CreateBounded<FileJob>(new BoundedChannelOptions(50000)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            // --- PRODUCER TASK ---
            var producer = Task.Run(async () =>
            {
                try
                {
                    int originalIndex = 0;
                    var uiBatch = new List<int>(2000); // Batch indices
                    long lastBatchTime = DateTime.UtcNow.Ticks;

                    void FlushUiBatch()
                    {
                        if (uiBatch.Count == 0) return;
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

                    // Pre-pool the base directory string
                    string pooledBase = StringPool.GetOrAdd(baseDirectory);

                    foreach (string path in paths)
                    {
                        if (_cts.Token.IsCancellationRequested) break;

                        if (File.Exists(path))
                        {
                            await ProcessFileEntry(path, pooledBase, originalIndex++, channel.Writer, uiBatch);
                        }
                        else if (Directory.Exists(path))
                        {
                            var opts = new EnumerationOptions
                            {
                                IgnoreInaccessible = true,
                                RecurseSubdirectories = true,
                                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
                            };
                            try
                            {
                                foreach (string file in Directory.EnumerateFiles(path, "*", opts))
                                {
                                    if (_cts.Token.IsCancellationRequested) break;
                                    await ProcessFileEntry(file, pooledBase, originalIndex++, channel.Writer, uiBatch);

                                    if (uiBatch.Count >= 2000 || (DateTime.UtcNow.Ticks - lastBatchTime > 2500000))
                                    {
                                        FlushUiBatch();
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    FlushUiBatch();
                }
                finally
                {
                    channel.Writer.Complete();
                }
            });

            // --- CONSUMER TASKS ---
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

                                Stopwatch sw = Stopwatch.StartNew();
                                byte[]? hashBytes = null;
                                ItemStatus status = ItemStatus.Pending;

                                try
                                {
                                    // IO OPTIMIZATION: Zero-Allocation Handle Open
                                    using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
                                        job.FullPath,
                                        FileMode.Open,
                                        FileAccess.Read,
                                        FileShare.Read,
                                        FileOptions.SequentialScan))
                                    {
                                        long len = RandomAccess.GetLength(handle);
                                        bool isLarge = len > LargeFileThreshold;

                                        if (isLarge)
                                        {
                                            // Fallback to Stream wrapper for progress reporting on large files
                                            using (var fs = new FileStream(handle, FileAccess.Read))
                                            {
                                                AddActiveJob(job.Index, Path.GetFileName(job.FullPath));
                                                var progress = new Progress<double>(p => UpdateActiveJob(job.Index, p));
                                                var streamToHash = new ProgressStream(fs, progress, len);

                                                hashBytes = HashHelper.ComputeHashSync(streamToHash, _currentHashType, sharedBuffer, reusedAlgo);

                                                RemoveActiveJob(job.Index);
                                            }
                                        }
                                        else
                                        {
                                            // PURE FAST PATH: RandomAccess -> Buffer -> Hash
                                            hashBytes = HashHelper.ComputeHashHandle(handle, _currentHashType, sharedBuffer, reusedAlgo);
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { return; }
                                catch (Exception)
                                {
                                    status = ItemStatus.Error;
                                }

                                sw.Stop();

                                string timeStr = _settings.ShowTimeTab ? $"{sw.ElapsedMilliseconds} ms" : "";

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

                                // SoA Update
                                _fileStore.SetResult(job.Index, hashBytes, timeStr, status);

                                int currentCompleted = Interlocked.Increment(ref completed);
                                ThrottledUiUpdate(currentCompleted, -1, okCount, 0, errorCount);
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
            HandleCompletion(completed, okCount, 0, errorCount, globalSw);
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

            int missingCount = 0;
            int loadIndex = 0;

            string pooledBase = StringPool.GetOrAdd(baseFolder);

            // Populate Store
            foreach (var entry in parsedLines)
            {
                string fullPath = Path.GetFullPath(Path.Combine(baseFolder, entry.Filename));
                bool exists = File.Exists(fullPath);

                string pooledName = StringPool.GetOrAdd(Path.GetFileName(entry.Filename));
                string pooledRel = StringPool.GetOrAdd(entry.Filename);

                ItemStatus initialStatus = exists ? ItemStatus.Pending : ItemStatus.Missing;
                if (!exists) missingCount++;

                int idx = _fileStore.Add(pooledName, pooledRel, pooledBase, loadIndex++, initialStatus, entry.ExpectedHash);
                _displayIndices.Add(idx);
            }

            // In verification, we usually want alphabetical sort initially
            // Simple sort by Name using the Store
            _displayIndices.Sort((a, b) => string.Compare(_fileStore.GetFullPath(a), _fileStore.GetFullPath(b), StringComparison.OrdinalIgnoreCase));

            // Re-assign original indices based on sort if needed, or just keep them
            UpdateDisplayList();

            UpdateStats(0, _fileStore.Count, 0, 0, missingCount);
            progressBarTotal.Maximum = _fileStore.Count;

            int completed = 0, okCount = 0, badCount = 0;

            bool isHDD = _settings.OptimizeForHDD;
            // Check first existing file for drive type
            string? testFile = null;
            for (int i = 0; i < _fileStore.Count; i++)
            {
                string p = _fileStore.GetFullPath(i);
                if (File.Exists(p)) { testFile = p; break; }
            }
            if (!isHDD && testFile != null) isHDD = DriveDetector.IsRotational(testFile);

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
                        if (_fileStore.Statuses[idx] == ItemStatus.Missing) continue;

                        string fullPath = _fileStore.GetFullPath(idx);
                        byte[]? expected = _fileStore.ExpectedHashes[idx];

                        await channel.Writer.WriteAsync(new FileJob(idx, fullPath, expected), _cts.Token);
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

                                Stopwatch sw = Stopwatch.StartNew();
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
                                        bool isLarge = len > LargeFileThreshold;

                                        if (isLarge)
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

                                sw.Stop();

                                string timeStr = _settings.ShowTimeTab ? $"{sw.ElapsedMilliseconds} ms" : "";

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
            GCSettings.LatencyMode = oldMode;
            globalSw.Stop();
            HandleCompletion(completed, okCount, badCount, missingCount, globalSw, true);
        }
    }
}