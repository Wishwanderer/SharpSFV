using SharpSFV.Interop;
using SharpSFV.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.RegularExpressions;

namespace SharpSFV
{
    public partial class Form1
    {
        /// <summary>
        /// Processes a single discovered file during enumeration.
        /// <para>
        /// <b>Responsibilities:</b>
        /// 1. Checks cancellation/pause state.
        /// 2. Applies Include/Exclude filters.
        /// 3. Computes the relative path (pooling the string to save memory).
        /// 4. Adds the item to the <see cref="FileStore"/> and pushes a job to the <see cref="Channel{T}"/>.
        /// </para>
        /// </summary>
        /// <param name="fullPath">The absolute path to the file.</param>
        /// <param name="baseDir">The root directory of the scan (used for relative path calculation).</param>
        /// <param name="originalIndex">The sequential index of discovery (for sorting).</param>
        /// <param name="writer">The channel writer to push the work item to.</param>
        /// <param name="uiBatch">A buffer list of indices to update the UI in batches.</param>
        private async Task ProcessFileEntry(string fullPath, string baseDir, int originalIndex, ChannelWriter<FileJob> writer, List<int> uiBatch)
        {
            // 1. Check Pause State (Block here if paused)
            try
            {
                _pauseEvent.Wait(_cts?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) { return; }

            // 2. Check Cancellation
            if (_cts != null && _cts.IsCancellationRequested) return;

            // 3. Advanced Filtering (Skip if file doesn't match patterns)
            if (_settings.ShowAdvancedBar)
            {
                string fileName = Path.GetFileName(fullPath);
                if (!IsFileAllowed(fileName)) return;
            }

            // 4. Optimized Path Pooling
            // We use Spans to process path strings without allocating substrings until necessary.
            ReadOnlySpan<char> pathSpan = fullPath.AsSpan();
            ReadOnlySpan<char> baseSpan = baseDir.AsSpan();

            string pooledRelPath;

            // Check if path starts with baseDir to compute relative path efficiently
            if (pathSpan.StartsWith(baseSpan, StringComparison.OrdinalIgnoreCase) && pathSpan.Length > baseSpan.Length)
            {
                int offset = baseSpan.Length;
                // Skip separator if present
                if (pathSpan[offset] == Path.DirectorySeparatorChar || pathSpan[offset] == Path.AltDirectorySeparatorChar)
                    offset++;

                // GetOrAdd uses AlternateLookup<Span<char>> to find existing string or create new one
                pooledRelPath = StringPool.GetOrAdd(pathSpan.Slice(offset));
            }
            else
            {
                // Fallback for complex paths or paths outside baseDir
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

            var fileNameSpan = Path.GetFileName(pathSpan);
            string pooledFileName = StringPool.GetOrAdd(fileNameSpan);

            // Add to SoA Store (Thread-safe)
            int storeIndex = _fileStore.Add(pooledFileName, pooledRelPath, baseDir, originalIndex, ItemStatus.Queued);

            uiBatch.Add(storeIndex);

            // Create lightweight struct and push to channel
            var job = new FileJob(storeIndex, fullPath, null);
            await writer.WriteAsync(job, _cts!.Token);
        }

        /// <summary>
        /// Determines if a file should be processed based on Include/Exclude patterns.
        /// </summary>
        private bool IsFileAllowed(string filename)
        {
            // Check Exclusions first
            if (!string.IsNullOrWhiteSpace(_settings.ExcludePattern))
            {
                var patterns = _settings.ExcludePattern.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in patterns)
                {
                    if (FitsMask(filename, p.Trim())) return false;
                }
            }

            // Check Inclusions
            if (!string.IsNullOrWhiteSpace(_settings.IncludePattern))
            {
                bool match = false;
                var patterns = _settings.IncludePattern.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (patterns.Length == 0) return true;

                foreach (var p in patterns)
                {
                    if (FitsMask(filename, p.Trim()))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match) return false;
            }

            return true;
        }

        /// <summary>
        /// Matches a filename against a wildcard mask (e.g., "*.txt", "data?.dat").
        /// </summary>
        /// <param name="fileMask">The pattern to match against (supports * and ?).</param>
        private bool FitsMask(string fileName, string fileMask)
        {
            if (string.IsNullOrEmpty(fileMask) || fileMask == "*.*" || fileMask == "*") return true;

            // Optimization for simple suffix check (e.g. "*.txt")
            if (fileMask.StartsWith("*") && !fileMask.Contains("?") && fileMask.IndexOf('*', 1) == -1)
            {
                return fileName.EndsWith(fileMask.Substring(1), StringComparison.OrdinalIgnoreCase);
            }

            // Fallback to Regex for complex masks
            try
            {
                string pattern = "^" + Regex.Escape(fileMask)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
            }
            catch { return true; }
        }

        /// <summary>
        /// Calculates the deepest common directory among a list of paths.
        /// Used when users drop multiple files/folders from different locations to determine
        /// where the relative paths should start from.
        /// </summary>
        private string FindCommonBasePath(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return "";
            if (paths.Count == 1) return paths[0];

            // Split the first path into segments
            string[] shortestPathParts = paths.OrderBy(p => p.Length).First().Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string commonPath = "";
            bool isRooted = Path.IsPathRooted(paths[0]);

            for (int i = 0; i < shortestPathParts.Length; i++)
            {
                string currentSegment = shortestPathParts[i];
                // Check if this segment exists in all other paths at index i
                if (!paths.All(p =>
                {
                    var parts = p.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    return i < parts.Length && parts[i].Equals(currentSegment, StringComparison.OrdinalIgnoreCase);
                })) break;

                // Reconstruct path
                commonPath = (i == 0 && isRooted && currentSegment.Contains(":"))
                    ? currentSegment + Path.DirectorySeparatorChar
                    : Path.Combine(commonPath, currentSegment);
            }
            return commonPath.TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}