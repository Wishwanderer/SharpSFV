using SharpSFV.Models;
using SharpSFV.Interop;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Media;

namespace SharpSFV
{
    public partial class Form1
    {
        private void SetupDragDrop()
        {
            this.AllowDrop = true;
            this.DragEnter += Form1_DragEnter;
            this.DragDrop += Form1_DragDrop;
        }

        private void Form1_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        /// <summary>
        /// Handles the files dropped onto the form.
        /// <para>
        /// <b>Logic Flow:</b>
        /// 1. Checks if the app is currently busy (locks UI if so).
        /// 2. <b>Job Mode:</b> Treats the drop as a "Job". 
        ///    - Single Folder -> 1 Job (Name = Folder Name).
        ///    - Multiple Files -> 1 Job (Name = Parent Folder Name).
        /// 3. <b>Standard Mode:</b> treats the drop as a flat list of files.
        ///    - If a single .sfv/.md5 file is dropped, it triggers <b>Verification Mode</b>.
        ///    - Otherwise, it triggers <b>Creation Mode</b>.
        /// </para>
        /// </summary>
        private async void Form1_DragDrop(object? sender, DragEventArgs e)
        {
            // Prevent state corruption: Do not accept new files while the engine is hashing 
            // (unless we are in Job Mode, where we can queue them).
            if (_isProcessing && !_isJobMode) return;

            string[]? paths = (string[]?)e.Data!.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0)
            {
                try
                {
                    await HandleDroppedPaths(paths);
                }
                catch (OperationCanceledException)
                {
                    // Intentional cancellation, ignore.
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"An error occurred while processing files:\n{ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Central entry point for processing paths (used by DragDrop, CLI args, and Open File Dialog).
        /// </summary>
        private async Task HandleDroppedPaths(string[] paths)
        {
            if (paths.Length == 0) return;

            if (_isJobMode)
            {
                // --- JOBS MODE LOGIC ---
                // In Job Mode, we don't scan files immediately. 
                // We store the paths in the JobStore and let the Worker Thread scan them when the job starts.
                string baseDir = "";
                string jobName = "";

                if (paths.Length == 1)
                {
                    // Single item logic
                    if (Directory.Exists(paths[0]))
                    {
                        // FOLDER DROP:
                        // Set baseDir to PARENT so the checksum file is saved BESIDE the folder, not inside it.
                        // Example: Drop "C:\Photos\2021", checksum saved to "C:\Photos\2021.sfv"
                        baseDir = Path.GetDirectoryName(paths[0]) ?? paths[0];
                        jobName = new DirectoryInfo(paths[0]).Name;
                    }
                    else
                    {
                        // FILE DROP:
                        // Set baseDir to containing folder
                        baseDir = Path.GetDirectoryName(paths[0]) ?? "";
                        jobName = Path.GetFileName(paths[0]);
                    }
                }
                else
                {
                    // MULTI ITEM DROP:
                    // Find the deepest common folder among all dropped items. 
                    // The checksum file will be saved there.
                    var parentDirs = paths.Select(p => Path.GetDirectoryName(p) ?? p).ToList();
                    baseDir = FindCommonBasePath(parentDirs);
                    try
                    {
                        if (!string.IsNullOrEmpty(baseDir))
                            jobName = new DirectoryInfo(baseDir).Name;
                        else
                            jobName = "Multi_Files";
                    }
                    catch { jobName = "Multi_Files"; }
                }

                // Add to JobStore (SoA Container)
                _jobStore.Add(jobName, baseDir, paths);

                // Update UI: VirtualListSize determines how many items the ListView requests
                lvFiles.VirtualListSize = _jobStore.Count;
                lvFiles.Invalidate();

                // Auto-start Processor if it's currently idle
                if (!_isJobQueueRunning) _ = ProcessJobQueue();
            }
            else
            {
                // --- STANDARD MODE LOGIC ---
                bool containsFolder = paths.Any(p => Directory.Exists(p));

                // Reset Sorting
                _listSorter.SortColumn = -1;
                _listSorter.Order = SortOrder.None;
                UpdateSortVisuals(-1, SortOrder.None);

                // Verification Check: 
                // If the user drops a SINGLE file and it is a known checksum type (.sfv, .md5), 
                // assume they want to VERIFY it.
                if (!containsFolder && paths.Length == 1 && _verificationExtensions.Contains(Path.GetExtension(paths[0])) && File.Exists(paths[0]))
                {
                    await RunVerification(paths[0]);
                }
                else
                {
                    // Creation Mode
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
        }

        /// <summary>
        /// Updates the UI state to reflect whether the engine is busy.
        /// Locks critical controls to prevent data corruption during processing.
        /// </summary>
        private void SetProcessingState(bool processing)
        {
            _isProcessing = processing;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => SetProcessingState(processing)));
                return;
            }

            // Enable/Disable Control Buttons
            if (_btnStop != null) _btnStop.Enabled = processing;

            if (_btnPause != null)
            {
                _btnPause.Enabled = processing;
                if (processing)
                {
                    _btnPause.Text = "Pause";
                    _isPaused = false;
                    _pauseEvent.Set(); // Ensure we start running
                    Win32Storage.SetProgressBarState(progressBarTotal, Win32Storage.PBST_NORMAL); // Green
                }
            }

            // Disable Mode Switching while active
            // Prevents switching SoA containers (FileStore vs JobStore) while the engine is writing to them.
            if (_menuModeStandard != null) _menuModeStandard.Enabled = !processing;
            if (_menuModeJob != null) _menuModeJob.Enabled = !processing;

            if (processing && _lblTotalTime != null)
            {
                _lblTotalTime.Text = "";
            }
        }
    }
}