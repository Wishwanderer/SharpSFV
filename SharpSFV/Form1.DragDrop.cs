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

        private async void Form1_DragDrop(object? sender, DragEventArgs e)
        {
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

        private async Task HandleDroppedPaths(string[] paths)
        {
            if (paths.Length == 0) return;

            if (_isJobMode)
            {
                // --- JOBS MODE LOGIC ---
                string baseDir = "";
                string jobName = "";

                if (paths.Length == 1)
                {
                    // Single item logic
                    if (Directory.Exists(paths[0]))
                    {
                        // FOLDER DROP:
                        // Set baseDir to PARENT so checksum saves BESIDE the folder
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
                    // MULTI ITEM LOGIC:
                    // Find common base. Checksum saves in that common base.
                    var parentDirs = paths.Select(p => Path.GetDirectoryName(p) ?? p).ToList();
                    baseDir = FindCommonBasePath(parentDirs); // Logic now resides in Form1.Processing.cs
                    try
                    {
                        if (!string.IsNullOrEmpty(baseDir))
                            jobName = new DirectoryInfo(baseDir).Name;
                        else
                            jobName = "Multi_Files";
                    }
                    catch { jobName = "Multi_Files"; }
                }

                // Add to JobStore (SoA)
                _jobStore.Add(jobName, baseDir, paths);

                // Refresh UI
                lvFiles.VirtualListSize = _jobStore.Count;
                lvFiles.Invalidate();

                // Start Processor if idle
                if (!_isJobQueueRunning) _ = ProcessJobQueue();
            }
            else
            {
                // --- STANDARD LOGIC ---
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
        }

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
            // Prevents state corruption (e.g., clearing lists while engine is writing)
            if (_menuModeStandard != null) _menuModeStandard.Enabled = !processing;
            if (_menuModeJob != null) _menuModeJob.Enabled = !processing;

            if (processing && _lblTotalTime != null)
            {
                _lblTotalTime.Text = "";
            }
        }
    }
}