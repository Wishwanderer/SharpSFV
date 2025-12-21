using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        private async Task HandleDroppedPaths(string[] paths)
        {
            if (paths.Length == 0) return;
            bool containsFolder = paths.Any(p => Directory.Exists(p));

            _listSorter.SortColumn = -1;
            _listSorter.Order = SortOrder.None;
            UpdateSortVisuals(-1, SortOrder.None);

            if (!containsFolder && paths.Length == 1 && _verificationExtensions.Contains(Path.GetExtension(paths[0])) && File.Exists(paths[0]))
            {
                await RunVerification(paths[0]);
            }
            else
            {
                string baseDirectory = "";
                if (paths.Length == 1 && Directory.Exists(paths[0])) baseDirectory = paths[0];
                else
                {
                    var parentDirs = paths.Select(p => Path.GetDirectoryName(p) ?? p).ToList();
                    baseDirectory = FindCommonBasePath(parentDirs);
                }

                List<string> allFilesToHash = new List<string>();
                foreach (string path in paths)
                {
                    if (File.Exists(path)) allFilesToHash.Add(path);
                    else if (Directory.Exists(path))
                    {
                        try { allFilesToHash.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)); }
                        catch (Exception ex) { MessageBox.Show($"Error accessing folder '{path}': {ex.Message}"); }
                    }
                }
                await RunHashCreation(allFilesToHash.ToArray(), baseDirectory);
            }
        }

        private async Task RunHashCreation(string[] filePaths, string baseDirectory)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();

            SetupUIForMode("Creation");
            UpdateStats(0, filePaths.Length, 0, 0, 0);

            _allItems.Clear();
            _displayList.Clear();
            int originalIndex = 0;

            foreach (var fullPath in filePaths)
            {
                if (Directory.Exists(fullPath)) continue;

                string relativePath = "";
                if (!string.IsNullOrEmpty(baseDirectory))
                {
                    try { relativePath = Path.GetRelativePath(baseDirectory, fullPath); }
                    catch { relativePath = fullPath; }
                }
                else relativePath = Path.GetFileName(fullPath);

                var data = new FileItemData
                {
                    FullPath = fullPath,
                    FileName = Path.GetFileName(fullPath),
                    RelativePath = relativePath,
                    BaseDirectory = baseDirectory,
                    OriginalIndex = originalIndex++
                };
                _allItems.Add(data);
            }

            _displayList.AddRange(_allItems);
            UpdateDisplayList();

            if (_allItems.Count == 0) { SetProcessingState(false); return; }

            progressBarTotal.Maximum = _allItems.Count;
            progressBarTotal.Value = 0;

            int completed = 0, okCount = 0, errorCount = 0;

            // --- DRIVE DETECTION ---
            int maxThreads = Environment.ProcessorCount;
            string driveMode = "SSD Mode";

            if (_settings.OptimizeForHDD)
            {
                maxThreads = 1;
                driveMode = "HDD Mode (Forced)";
            }
            else if (_allItems.Count > 0 && DriveDetector.IsRotational(_allItems[0].FullPath))
            {
                maxThreads = 1;
                driveMode = "HDD Mode (Detected)";
            }

            this.Text = $"SharpSFV - Creating... [{driveMode}]";
            Stopwatch globalSw = Stopwatch.StartNew();

            try
            {
                using (var semaphore = new SemaphoreSlim(maxThreads))
                {
                    var processingList = _displayList.ToList();
                    var tasks = processingList.Select(async data =>
                    {
                        if (_cts.Token.IsCancellationRequested) return;

                        await semaphore.WaitAsync(_cts.Token);
                        try
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            IProgress<double>? progress = null;

                            try
                            {
                                long len = new FileInfo(data.FullPath).Length;
                                if (len > LargeFileThreshold)
                                {
                                    PinItemToTop(data);
                                    data.Status = "0%";
                                    progress = new Progress<double>(p =>
                                    {
                                        int pct = (int)p;
                                        if (data.Status != $"{pct}%") { data.Status = $"{pct}%"; lvFiles.Invalidate(); }
                                    });
                                }
                            }
                            catch { }

                            string hash = await HashHelper.ComputeHashAsync(data.FullPath, _currentHashType, progress, _cts.Token);
                            sw.Stop();

                            if (data.IsPinned) UnpinAndRestore(data);

                            if (hash == "CANCELLED") return;

                            data.CalculatedHash = hash;
                            if (_settings.ShowTimeTab) data.TimeStr = $"{sw.ElapsedMilliseconds} ms";

                            if (hash == "FILE_NOT_FOUND" || hash == "ACCESS_DENIED" || hash == "ERROR")
                            {
                                data.Status = hash;
                                data.ForeColor = ColRedText;
                                data.BackColor = ColRedBack;
                                Interlocked.Increment(ref errorCount);
                            }
                            else
                            {
                                data.Status = "Done";
                                data.ForeColor = ColGreenText;
                                data.BackColor = ColGreenBack;
                                Interlocked.Increment(ref okCount);
                            }

                            int currentCompleted = Interlocked.Increment(ref completed);
                            ThrottledUiUpdate(currentCompleted, processingList.Count, okCount, 0, errorCount);
                        }
                        catch (OperationCanceledException) { }
                        finally { semaphore.Release(); }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) { MessageBox.Show("Operation Stopped."); }

            globalSw.Stop();
            HandleCompletion(completed, okCount, 0, errorCount, globalSw);
        }

        private async Task RunVerification(string checkFilePath)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();
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

            var parsedLines = new List<(string ExpectedHash, string Filename)>();
            try
            {
                foreach (var line in await File.ReadAllLinesAsync(checkFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(";")) continue;

                    string expectedHash = "", filename = "";
                    Match matchA = Regex.Match(line, @"^\s*([0-9a-fA-F]+)\s+\*?(.*?)\s*$");
                    Match matchB = Regex.Match(line, @"^\s*(.*?)\s+([0-9a-fA-F]{8})\s*$");

                    if (verificationAlgo == HashType.Crc32 && matchB.Success)
                    {
                        filename = matchB.Groups[1].Value.Trim();
                        expectedHash = matchB.Groups[2].Value;
                    }
                    else if (matchA.Success)
                    {
                        expectedHash = matchA.Groups[1].Value.Trim();
                        filename = matchA.Groups[2].Value.Trim();
                    }

                    if (!string.IsNullOrEmpty(expectedHash) && !string.IsNullOrEmpty(filename))
                        parsedLines.Add((expectedHash, filename));
                }
            }
            catch (Exception ex) { MessageBox.Show("Error parsing: " + ex.Message); SetProcessingState(false); return; }

            _allItems.Clear();
            _displayList.Clear();
            int missingCount = 0;

            foreach (var entry in parsedLines)
            {
                string fullPath = Path.GetFullPath(Path.Combine(baseFolder, entry.Filename));
                bool exists = File.Exists(fullPath);

                var data = new FileItemData
                {
                    FullPath = fullPath,
                    FileName = entry.Filename,
                    RelativePath = entry.Filename,
                    BaseDirectory = baseFolder,
                    ExpectedHash = entry.ExpectedHash,
                    CalculatedHash = "Waiting...",
                    Status = exists ? "Pending" : "MISSING"
                };

                if (!exists)
                {
                    data.ForeColor = ColYellowText;
                    data.BackColor = ColYellowBack;
                    data.FontStyle = FontStyle.Strikeout;
                    missingCount++;
                }
                _allItems.Add(data);
            }

            _allItems.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < _allItems.Count; i++) _allItems[i].OriginalIndex = i;

            _displayList.AddRange(_allItems);
            UpdateDisplayList();
            UpdateStats(0, _allItems.Count, 0, 0, missingCount);
            progressBarTotal.Maximum = _allItems.Count;
            progressBarTotal.Value = 0;

            int completed = 0, okCount = 0, badCount = 0;

            int maxThreads = Environment.ProcessorCount;
            string driveMode = "SSD Mode";

            if (_settings.OptimizeForHDD)
            {
                maxThreads = 1;
                driveMode = "HDD Mode (Forced)";
            }
            else
            {
                var firstFile = _displayList.FirstOrDefault(x => File.Exists(x.FullPath));
                if (firstFile != null && DriveDetector.IsRotational(firstFile.FullPath))
                {
                    maxThreads = 1;
                    driveMode = "HDD Mode (Detected)";
                }
            }

            this.Text = $"SharpSFV - Verifying... [{driveMode}]";
            Stopwatch globalSw = Stopwatch.StartNew();

            try
            {
                using (var semaphore = new SemaphoreSlim(maxThreads))
                {
                    var processingList = _displayList.ToList();
                    var tasks = processingList.Select(async data =>
                    {
                        if (data.Status == "MISSING") return;
                        if (_cts.Token.IsCancellationRequested) return;

                        await semaphore.WaitAsync(_cts.Token);
                        try
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            IProgress<double>? progress = null;

                            try
                            {
                                if (new FileInfo(data.FullPath).Length > LargeFileThreshold)
                                {
                                    PinItemToTop(data);
                                    data.Status = "0%";
                                    progress = new Progress<double>(p =>
                                    {
                                        int pct = (int)p;
                                        if (data.Status != $"{pct}%") { data.Status = $"{pct}%"; lvFiles.Invalidate(); }
                                    });
                                }
                            }
                            catch { }

                            string calculatedHash = await HashHelper.ComputeHashAsync(data.FullPath, verificationAlgo, progress, _cts.Token);
                            sw.Stop();

                            if (data.IsPinned) UnpinAndRestore(data);
                            if (calculatedHash == "CANCELLED") return;

                            data.CalculatedHash = calculatedHash;
                            if (_settings.ShowTimeTab) data.TimeStr = $"{sw.ElapsedMilliseconds} ms";

                            bool isMatch = false;

                            if (calculatedHash == "FILE_NOT_FOUND" || calculatedHash == "ACCESS_DENIED" || calculatedHash == "ERROR")
                            {
                                // Error Status
                            }
                            else
                            {
                                if (calculatedHash.Equals(data.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                }
                                else if (verificationAlgo == HashType.Crc32)
                                {
                                    string reversed = ReverseHexBytes(calculatedHash);
                                    if (reversed.Equals(data.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                                        isMatch = true;
                                }
                            }

                            if (isMatch)
                            {
                                data.Status = "OK";
                                data.ForeColor = ColGreenText;
                                data.BackColor = ColGreenBack;
                                Interlocked.Increment(ref okCount);
                            }
                            else if (calculatedHash.Length > 20)
                            {
                                data.Status = calculatedHash;
                                data.ForeColor = ColYellowText;
                                data.BackColor = ColYellowBack;
                                Interlocked.Increment(ref badCount);
                            }
                            else
                            {
                                data.Status = "BAD";
                                data.ForeColor = ColRedText;
                                data.BackColor = ColRedBack;
                                Interlocked.Increment(ref badCount);
                            }

                            int currentCompleted = Interlocked.Increment(ref completed);
                            ThrottledUiUpdate(currentCompleted, processingList.Count, okCount, badCount, missingCount);
                        }
                        catch (OperationCanceledException) { }
                        finally { semaphore.Release(); }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) { MessageBox.Show("Operation Stopped."); }

            globalSw.Stop();
            HandleCompletion(completed, okCount, badCount, missingCount, globalSw, true);
        }

        private void HandleCompletion(int completed, int ok, int bad, int missing, Stopwatch sw, bool verifyMode = false)
        {
            // FIX: Safely check for cancellation to resolve CS8602
            bool isCancelled = _cts?.Token.IsCancellationRequested ?? false;

            if (!isCancelled)
            {
                // Play success or error sound based on results
                // Logic: Error sound if any files were BAD, or if verifying and files are MISSING
                if (bad > 0 || (missing > 0 && verifyMode))
                    SystemSounds.Exclamation.Play();
                else
                    SystemSounds.Asterisk.Play();
            }

            SetProcessingState(false);
            UpdateStats(completed, _allItems.Count, ok, bad, missing);
            progressBarTotal.Value = completed;

            if (verifyMode)
            {
                string algoName = Enum.GetName(typeof(HashType), _currentHashType) ?? "Hash";
                this.Text = (bad == 0 && missing == 0)
                    ? $"SharpSFV [{algoName}] - All Files OK"
                    : $"SharpSFV [{algoName}] - {bad} Bad, {missing} Missing";
            }
            else
            {
                this.Text = $"SharpSFV - Creation Complete [{_currentHashType}]";
            }

            // Summary Row
            if (_settings.ShowTimeTab && !isCancelled)
            {
                var summaryItem = new FileItemData
                {
                    FileName = "TOTAL ELAPSED TIME",
                    CalculatedHash = "",
                    Status = "",
                    ExpectedHash = "",
                    TimeStr = $"{sw.ElapsedMilliseconds} ms",
                    IsSummaryRow = true,
                    OriginalIndex = int.MaxValue,
                    ForeColor = Color.Blue,
                    FontStyle = FontStyle.Bold
                };
                _allItems.Add(summaryItem);
                _displayList.Add(summaryItem);
                UpdateDisplayList();
            }
            else
            {
                lvFiles.Invalidate();
            }
        }

        private void SetProcessingState(bool processing)
        {
            _isProcessing = processing;
            if (_btnStop != null) _btnStop.Enabled = processing;
        }

        private void PinItemToTop(FileItemData data)
        {
            if (this.InvokeRequired) { this.Invoke(new Action(() => PinItemToTop(data))); return; }
            data.IsPinned = true;
            _displayList.Remove(data);
            _displayList.Insert(0, data);
            lvFiles.Invalidate();
        }

        private void UnpinAndRestore(FileItemData data)
        {
            if (this.InvokeRequired) { this.Invoke(new Action(() => UnpinAndRestore(data))); return; }
            data.IsPinned = false;
            _displayList.Sort(_listSorter);
            lvFiles.Invalidate();
        }

        private string ReverseHexBytes(string hex)
        {
            if (hex.Length % 2 != 0) return hex;
            char[] charArray = new char[hex.Length];
            for (int i = 0; i < hex.Length; i += 2)
            {
                charArray[i] = hex[hex.Length - i - 2];
                charArray[i + 1] = hex[hex.Length - i - 1];
            }
            return new string(charArray);
        }

        private void ThrottledUiUpdate(int current, int total, int ok, int bad, int missing)
        {
            long now = DateTime.Now.Ticks;
            if (now - Interlocked.Read(ref _lastUiUpdateTick) > 1000000)
            {
                lock (_uiLock)
                {
                    if (now - _lastUiUpdateTick > 1000000)
                    {
                        _lastUiUpdateTick = now;
                        this.Invoke(new Action(() =>
                        {
                            progressBarTotal.Value = Math.Min(current, progressBarTotal.Maximum);
                            UpdateStats(current, total, ok, bad, missing);
                            lvFiles.Invalidate();
                        }));
                    }
                }
            }
        }
    }
}