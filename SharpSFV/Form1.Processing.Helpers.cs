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

            // 3. Advanced Filtering
            if (_settings.ShowAdvancedBar)
            {
                string fileName = Path.GetFileName(fullPath);
                if (!IsFileAllowed(fileName)) return;
            }

            ReadOnlySpan<char> pathSpan = fullPath.AsSpan();
            ReadOnlySpan<char> baseSpan = baseDir.AsSpan();

            string pooledRelPath;

            if (pathSpan.StartsWith(baseSpan, StringComparison.OrdinalIgnoreCase) && pathSpan.Length > baseSpan.Length)
            {
                int offset = baseSpan.Length;
                if (pathSpan[offset] == Path.DirectorySeparatorChar || pathSpan[offset] == Path.AltDirectorySeparatorChar)
                    offset++;

                pooledRelPath = StringPool.GetOrAdd(pathSpan.Slice(offset));
            }
            else
            {
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

            int storeIndex = _fileStore.Add(pooledFileName, pooledRelPath, baseDir, originalIndex, ItemStatus.Queued);

            uiBatch.Add(storeIndex);

            var job = new FileJob(storeIndex, fullPath, null);
            await writer.WriteAsync(job, _cts!.Token);
        }

        private bool IsFileAllowed(string filename)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ExcludePattern))
            {
                var patterns = _settings.ExcludePattern.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in patterns)
                {
                    if (FitsMask(filename, p.Trim())) return false;
                }
            }

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

        private bool FitsMask(string fileName, string fileMask)
        {
            if (string.IsNullOrEmpty(fileMask) || fileMask == "*.*" || fileMask == "*") return true;

            if (fileMask.StartsWith("*") && !fileMask.Contains("?") && fileMask.IndexOf('*', 1) == -1)
            {
                return fileName.EndsWith(fileMask.Substring(1), StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                string pattern = "^" + Regex.Escape(fileMask)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
            }
            catch { return true; }
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