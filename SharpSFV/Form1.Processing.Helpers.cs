using SharpSFV.Interop;
using SharpSFV.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime;
using System.Threading;
using System.Threading.Channels;
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

                await RunHashCreation(paths, baseDirectory);
            }
        }

        private async Task ProcessFileEntry(string fullPath, string baseDir, int originalIndex, ChannelWriter<FileJob> writer, List<int> uiBatch)
        {
            // OPTIMIZATION: Span-based path manipulation to avoid allocations before pooling
            ReadOnlySpan<char> pathSpan = fullPath.AsSpan();
            ReadOnlySpan<char> baseSpan = baseDir.AsSpan();

            string pooledRelPath;

            if (pathSpan.StartsWith(baseSpan, StringComparison.OrdinalIgnoreCase) && pathSpan.Length > baseSpan.Length)
            {
                int offset = baseSpan.Length;
                // Handle trailing slash in base
                if (pathSpan[offset] == Path.DirectorySeparatorChar || pathSpan[offset] == Path.AltDirectorySeparatorChar)
                    offset++;

                // Zero-allocation string pooling from Span
                pooledRelPath = StringPool.GetOrAdd(pathSpan.Slice(offset));
            }
            else
            {
                // Fallback for complex relative paths
                try
                {
                    string rel = Path.GetRelativePath(baseDir, fullPath);
                    pooledRelPath = StringPool.GetOrAdd(rel);
                }
                catch
                {
                    pooledRelPath = StringPool.GetOrAdd(Path.GetFileName(fullPath));
                }
            }

            // Zero-allocation filename pooling
            var fileNameSpan = Path.GetFileName(pathSpan);
            string pooledFileName = StringPool.GetOrAdd(fileNameSpan);

            // Add to Store (SoA)
            int storeIndex = _fileStore.Add(pooledFileName, pooledRelPath, baseDir, originalIndex, ItemStatus.Queued);

            // Add to UI Batch
            uiBatch.Add(storeIndex);

            // Create Job Struct (No Object Allocation)
            var job = new FileJob(storeIndex, fullPath, null);

            await writer.WriteAsync(job, _cts!.Token);
        }

        private void ThrottledUiUpdate(int current, int total, int ok, int bad, int missing)
        {
            // Modulo throttling
            if (current % 50 != 0 && total == -1) return;

            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref _lastUiUpdateTick) > 500000)
            {
                lock (_uiLock)
                {
                    if (now - _lastUiUpdateTick > 500000)
                    {
                        _lastUiUpdateTick = now;
                        this.BeginInvoke(new Action(() =>
                        {
                            int safeTotal = (total <= 0) ? _fileStore.Count : total;
                            if (progressBarTotal.Maximum > 0)
                                progressBarTotal.Value = Math.Min(current, progressBarTotal.Maximum);

                            UpdateStats(current, safeTotal, ok, bad, missing);
                            lvFiles.Invalidate();
                        }));
                    }
                }
            }
        }

        private void SetProcessingState(bool processing)
        {
            _isProcessing = processing;
            if (_btnStop != null)
            {
                if (this.InvokeRequired) this.Invoke(new Action(() => _btnStop.Enabled = processing));
                else _btnStop.Enabled = processing;
            }

            // FIX: Clear the total time label when starting a NEW operation
            if (processing && _lblTotalTime != null)
            {
                // Use Invoke/BeginInvoke to be thread-safe if called from non-UI context (rare but safer)
                if (this.InvokeRequired)
                    this.BeginInvoke(new Action(() => _lblTotalTime.Text = ""));
                else
                    _lblTotalTime.Text = "";
            }
        }

        private void HandleCompletion(int completed, int ok, int bad, int missing, Stopwatch sw, bool verifyMode = false)
        {
            bool isCancelled = _cts?.Token.IsCancellationRequested ?? false;

            if (!isCancelled)
            {
                if (bad > 0 || (missing > 0 && verifyMode)) SystemSounds.Exclamation.Play();
                else SystemSounds.Asterisk.Play();
            }

            SetProcessingState(false);

            this.Invoke(new Action(() =>
            {
                RecalculateColumnWidths();

                UpdateStats(completed, _fileStore.Count, ok, bad, missing);
                progressBarTotal.Value = Math.Min(completed, progressBarTotal.Maximum);

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

                // FIX: Update Total Time Label format
                if (_settings.ShowTimeTab && !isCancelled && _lblTotalTime != null)
                {
                    _lblTotalTime.Text = $"Total Elapsed Time: {sw.ElapsedMilliseconds} ms";
                    _lblTotalTime.Visible = true;
                }

                lvFiles.Invalidate();
            }));
        }

        private string FindCommonBasePath(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return "";
            if (paths.Count == 1) return paths[0];

            string[] shortestPathParts = paths.OrderBy(p => p.Length).First().Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string commonPath = "";
            bool isRooted = Path.IsPathRooted(paths[0]);

            for (int i = 0; i < shortestPathParts.Length; i++)
            {
                string currentSegment = shortestPathParts[i];
                if (!paths.All(p =>
                {
                    var parts = p.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    return i < parts.Length && parts[i].Equals(currentSegment, StringComparison.OrdinalIgnoreCase);
                })) break;

                commonPath = (i == 0 && isRooted && currentSegment.Contains(":"))
                    ? currentSegment + Path.DirectorySeparatorChar
                    : Path.Combine(commonPath, currentSegment);
            }
            return commonPath.TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}