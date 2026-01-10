using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SharpSFV.Models;
using SharpSFV.Interop;
using SharpSFV.Utils;

namespace SharpSFV
{
    public partial class Form1
    {
        #region Control Actions (Pause / Cancel)

        private void PerformTogglePause()
        {
            if (!_isProcessing) return;

            if (_isPaused)
            {
                // RESUME ACTION
                _isPaused = false;
                if (_btnPause != null)
                {
                    _btnPause.Text = "Pause";
                    _btnPause.ForeColor = SystemColors.ControlText; // Standard Color
                }
                SetProgressBarColor(Win32Storage.PBST_NORMAL); // Green
                _pauseEvent.Set(); // Unblock threads
            }
            else
            {
                // PAUSE ACTION
                _isPaused = true;
                if (_btnPause != null)
                {
                    _btnPause.Text = "Resume";
                    _btnPause.ForeColor = Color.DarkGoldenrod; // Visual cue for active pause
                }
                SetProgressBarColor(Win32Storage.PBST_PAUSED); // Yellow
                _pauseEvent.Reset(); // Block threads
            }
        }

        private void PerformCancelAction()
        {
            if (!_isProcessing) return;

            if (MessageBox.Show("Are you sure you want to cancel the current operation?",
                "Cancel Operation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                // If paused, resume first so threads can exit/cancel properly
                if (_isPaused) _pauseEvent.Set();

                // Signal cancellation to background tasks
                _cts?.Cancel();

                // RESTART APPLICATION LOGIC
                // We rely on FormClosing to save the current settings (Window position, etc.)
                // Then we restart to ensure a completely clean state (RAM/Handles).
                Application.Restart();
                Environment.Exit(0);
            }
        }

        #endregion

        #region Context Menu Handlers

        private void CtxOpenFolder_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count != 1) return;
            int uiIndex = lvFiles.SelectedIndices[0];
            int storeIndex = _displayIndices[uiIndex];

            string fullPath = _fileStore.GetFullPath(storeIndex);
            try { Process.Start("explorer.exe", $"/select,\"{fullPath}\""); } catch { }
        }

        private void CtxCopyPath_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count > 0)
            {
                int uiIndex = lvFiles.SelectedIndices[0];
                int storeIndex = _displayIndices[uiIndex];
                Clipboard.SetText(_fileStore.GetFullPath(storeIndex));
            }
        }

        private void CtxCopyHash_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count > 0)
            {
                int uiIndex = lvFiles.SelectedIndices[0];
                int storeIndex = _displayIndices[uiIndex];
                Clipboard.SetText(_fileStore.GetCalculatedHashString(storeIndex));
            }
        }

        private void CtxRename_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count != 1) return;

            int uiIndex = lvFiles.SelectedIndices[0];
            int storeIndex = _displayIndices[uiIndex];

            string currentPath = _fileStore.GetFullPath(storeIndex);
            string currentName = _fileStore.FileNames[storeIndex] ?? "";

            string newName = SimpleInputDialog.ShowDialog("Rename File", "Enter new filename:", currentName);

            if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
            {
                try
                {
                    string baseDir = _fileStore.BaseDirectories[storeIndex] ?? "";
                    string? fileDir = Path.GetDirectoryName(currentPath);
                    if (fileDir == null) fileDir = baseDir;

                    string newPath = Path.Combine(fileDir, newName);

                    File.Move(currentPath, newPath);

                    _fileStore.FileNames[storeIndex] = newName;

                    string oldRel = _fileStore.RelativePaths[storeIndex] ?? currentName;
                    string? relDir = Path.GetDirectoryName(oldRel);
                    string newRel = string.IsNullOrEmpty(relDir) ? newName : Path.Combine(relDir, newName);

                    _fileStore.RelativePaths[storeIndex] = newRel;

                    lvFiles.Invalidate();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }

        private void CtxDelete_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count != 1) return;

            int uiIndex = lvFiles.SelectedIndices[0];
            int storeIndex = _displayIndices[uiIndex];
            string fullPath = _fileStore.GetFullPath(storeIndex);

            if (MessageBox.Show("Delete file from disk?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try
                {
                    File.Delete(fullPath);

                    _fileStore.RemoveAt(storeIndex);
                    _displayIndices.RemoveAt(uiIndex);

                    for (int i = 0; i < _displayIndices.Count; i++)
                    {
                        if (_displayIndices[i] > storeIndex) _displayIndices[i]--;
                    }

                    UpdateDisplayList();
                }
                catch (Exception ex) { MessageBox.Show($"Could not delete file: {ex.Message}"); }
            }
        }

        private void CtxRemoveList_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count == 0) return;

            lvFiles.BeginUpdate();

            var indicesToRemove = new List<int>();
            foreach (int uiIdx in lvFiles.SelectedIndices)
            {
                indicesToRemove.Add(_displayIndices[uiIdx]);
            }
            indicesToRemove.Sort();

            for (int i = indicesToRemove.Count - 1; i >= 0; i--)
            {
                _fileStore.RemoveAt(indicesToRemove[i]);
            }

            _displayIndices.Clear();
            for (int i = 0; i < _fileStore.Count; i++) _displayIndices.Add(i);

            UpdateDisplayList();
            lvFiles.EndUpdate();
        }

        #endregion

        #region Main Menu Actions

        private void PerformSelectAllAction()
        {
            lvFiles.BeginUpdate();
            for (int i = 0; i < lvFiles.VirtualListSize; i++)
            {
                lvFiles.Items[i].Selected = true;
            }
            lvFiles.EndUpdate();
        }

        private void PerformCopyAction()
        {
            if (lvFiles.SelectedIndices.Count == 0) return;
            var sb = new StringBuilder();

            if (_isJobMode)
            {
                foreach (int index in lvFiles.SelectedIndices)
                {
                    if (index < _jobStore.Count)
                    {
                        sb.AppendLine($"{_jobStore.Names[index]}\t{_jobStore.Statuses[index]}\t{_jobStore.Progress[index]:F1}%");
                    }
                }
            }
            else
            {
                foreach (int index in lvFiles.SelectedIndices)
                {
                    int storeIdx = _displayIndices[index];
                    string name = _fileStore.FileNames[storeIdx] ?? "";
                    sb.AppendLine($"{name}\t{_fileStore.GetCalculatedHashString(storeIdx)}\t{_fileStore.Statuses[storeIdx]}");
                }
            }

            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        private void PerformPasteAction()
        {
            if (!Clipboard.ContainsText()) return;
            string clipboardText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipboardText)) return;

            string[] lines = clipboardText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            List<string> validPaths = new List<string>();

            foreach (string line in lines)
            {
                string cleanPath = line.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(cleanPath)) continue;
                try { if (File.Exists(cleanPath) || Directory.Exists(cleanPath)) validPaths.Add(cleanPath); } catch { }
            }

            if (validPaths.Count > 0) _ = HandleDroppedPaths(validPaths.ToArray());
        }

        private void PerformOpenAction()
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Hash Files (*.xxh3;*.md5;*.sfv;*.sha1;*.sha256;*.txt)|*.xxh3;*.md5;*.sfv;*.sha1;*.sha256;*.txt|All Files (*.*)|*.*" })
            {
                if (ofd.ShowDialog() == DialogResult.OK) _ = HandleDroppedPaths(new string[] { ofd.FileName });
            }
        }

        private void PerformSaveAction()
        {
            if (_fileStore.Count == 0 && !_isJobMode) { MessageBox.Show("No files to save."); return; }
            if (_isJobMode) { MessageBox.Show("Job Mode saves checksums automatically upon completion."); return; }

            string defaultFileName = "checksums";
            string initialDirectory = GetCurrentRootDirectory();

            string fileFilter;
            string defaultExt;
            switch (_currentHashType)
            {
                case HashType.Crc32: defaultExt = ".sfv"; fileFilter = "SFV File (*.sfv)|*.sfv|Text File (*.txt)|*.txt"; break;
                case HashType.MD5: defaultExt = ".md5"; fileFilter = "MD5 File (*.md5)|*.md5|Text File (*.txt)|*.txt"; break;
                case HashType.SHA1: defaultExt = ".sha1"; fileFilter = "SHA1 File (*.sha1)|*.sha1|Text File (*.txt)|*.txt"; break;
                case HashType.SHA256: defaultExt = ".sha256"; fileFilter = "SHA256 File (*.sha256)|*.sha256|Text File (*.txt)|*.txt"; break;
                case HashType.XxHash3: default: defaultExt = ".xxh3"; fileFilter = "xxHash3 File (*.xxh3)|*.xxh3|Text File (*.txt)|*.txt"; break;
            }

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = fileFilter,
                FileName = defaultFileName + defaultExt,
                InitialDirectory = initialDirectory
            })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string saveDirectory = Path.GetDirectoryName(sfd.FileName) ?? initialDirectory;
                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine($"; Generated by SharpSFV (Signature: {_settings.CustomSignature})");
                            sw.WriteLine($"; Algorithm: {_currentHashType}");

                            for (int i = 0; i < _fileStore.Count; i++)
                            {
                                if (_fileStore.IsSummaryRows[i]) continue;

                                string hash = _fileStore.GetCalculatedHashString(i);
                                ItemStatus status = _fileStore.Statuses[i];

                                if (!hash.Contains("...") && status != ItemStatus.Queued && status != ItemStatus.Pending && !string.IsNullOrEmpty(hash))
                                {
                                    string fullPath = _fileStore.GetFullPath(i);
                                    string pathToWrite = fullPath;

                                    if (_settings.PathStorageMode == PathStorageMode.Relative)
                                    {
                                        try
                                        {
                                            string relPath = Path.GetRelativePath(saveDirectory, fullPath);
                                            if (Path.IsPathRooted(relPath) && !relPath.StartsWith("."))
                                            {
                                                pathToWrite = _fileStore.RelativePaths[i] ?? Path.GetFileName(fullPath);
                                            }
                                            else
                                            {
                                                pathToWrite = relPath;
                                            }
                                        }
                                        catch { pathToWrite = fullPath; }
                                    }

                                    if (_settings.ShowAdvancedBar && !string.IsNullOrEmpty(_settings.PathPrefix))
                                    {
                                        pathToWrite = Path.Combine(_settings.PathPrefix, pathToWrite);
                                    }

                                    sw.WriteLine($"{hash} *{pathToWrite}");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving checksum file: " + ex.Message); }
                }
            }
        }

        #endregion

        #region Script Generation Actions

        private void PerformBatchExport()
        {
            var badIndices = new List<int>();
            for (int i = 0; i < _fileStore.Count; i++)
            {
                if (_fileStore.Statuses[i] == ItemStatus.Bad || _fileStore.Statuses[i] == ItemStatus.Error)
                    badIndices.Add(i);
            }

            if (badIndices.Count == 0) return;

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "Batch Script (*.bat)|*.bat",
                FileName = "delete_bad_files.bat",
                InitialDirectory = GetCurrentRootDirectory()
            })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine("@echo off\necho Deleting BAD files...");
                            foreach (int idx in badIndices)
                            {
                                sw.WriteLine($"del \"{_fileStore.GetFullPath(idx)}\"");
                            }
                            sw.WriteLine("pause");
                        }
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving script: " + ex.Message); }
                }
            }
        }

        private void PerformGenerateDupCopyScript()
        {
            if (_fileStore.Count == 0 || _chkShowDuplicates?.Checked != true) return;

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "Batch Script (*.bat)|*.bat",
                FileName = "copy_duplicates.bat",
                InitialDirectory = GetCurrentRootDirectory()
            })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine("@echo off");
                            sw.WriteLine("echo Copying duplicates (Source: 1st file -> Dest: duplicates)...");

                            var validIndices = new List<int>();
                            for (int i = 0; i < _fileStore.Count; i++) if (!_fileStore.IsSummaryRows[i]) validIndices.Add(i);

                            var groups = validIndices.GroupBy(i => _fileStore.GetCalculatedHashString(i));

                            foreach (var g in groups)
                            {
                                var list = g.ToList();
                                if (list.Count > 1)
                                {
                                    string source = _fileStore.GetFullPath(list[0]);
                                    for (int i = 1; i < list.Count; i++)
                                    {
                                        sw.WriteLine($"copy /y \"{source}\" \"{_fileStore.GetFullPath(list[i])}\"");
                                    }
                                }
                            }
                            sw.WriteLine("pause");
                        }
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving script: " + ex.Message); }
                }
            }
        }

        private void PerformGenerateDupDeleteScript()
        {
            if (_fileStore.Count == 0 || _chkShowDuplicates?.Checked != true) return;

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "Batch Script (*.bat)|*.bat",
                FileName = "delete_duplicates.bat",
                InitialDirectory = GetCurrentRootDirectory()
            })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine("@echo off");
                            sw.WriteLine("echo Deleting duplicate files (Keeping 1st, deleting rest)...");

                            var validIndices = new List<int>();
                            for (int i = 0; i < _fileStore.Count; i++) if (!_fileStore.IsSummaryRows[i]) validIndices.Add(i);

                            var groups = validIndices.GroupBy(i => _fileStore.GetCalculatedHashString(i));

                            foreach (var g in groups)
                            {
                                var list = g.ToList();
                                if (list.Count > 1)
                                {
                                    for (int i = 1; i < list.Count; i++)
                                    {
                                        sw.WriteLine($"del \"{_fileStore.GetFullPath(list[i])}\"");
                                    }
                                }
                            }
                            sw.WriteLine("pause");
                        }
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving script: " + ex.Message); }
                }
            }
        }

        #endregion

        #region Dialog Actions

        private void ShowDiskDebugInfo()
        {
            string pathToCheck = Application.ExecutablePath;
            if (_fileStore.Count > 0)
            {
                pathToCheck = _fileStore.GetFullPath(0);
            }

            string info = DriveDetector.GetDebugInfo(pathToCheck);

            Form debugForm = new Form
            {
                Text = "Disk Debug Info",
                Size = new Size(500, 450),
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false
            };

            TextBox txtInfo = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Text = info,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9F, FontStyle.Regular)
            };
            txtInfo.Select(0, 0);

            Panel pnlBottom = new Panel { Height = 40, Dock = DockStyle.Bottom };

            Button btnCopy = new Button { Text = "Copy to Clipboard", Size = new Size(130, 25), Location = new Point(10, 8) };
            btnCopy.Click += (s, e) => { Clipboard.SetText(info); MessageBox.Show("Copied!", "SharpSFV", MessageBoxButtons.OK, MessageBoxIcon.Information); };

            Button btnClose = new Button { Text = "Close", Size = new Size(75, 25), Location = new Point(400, 8), Anchor = AnchorStyles.Top | AnchorStyles.Right, DialogResult = DialogResult.OK };

            pnlBottom.Controls.Add(btnCopy);
            pnlBottom.Controls.Add(btnClose);
            debugForm.Controls.Add(txtInfo);
            debugForm.Controls.Add(pnlBottom);
            debugForm.ShowDialog();
        }

        private void ShowCredits()
        {
            Form credits = new Form
            {
                Text = "About SharpSFV",
                Size = new Size(300, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            Label lbl = new Label
            {
                Text = "SharpSFV v2.80\nInspired by QuickSFV\n\nCreated by L33T.",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 80,
                Padding = new Padding(0, 20, 0, 0)
            };

            LinkLabel link = new LinkLabel
            {
                Text = "Visit GitHub Repository",
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top
            };
            link.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/Wishwanderer/SharpSFV", UseShellExecute = true }); } catch { } };

            Button btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point((credits.ClientSize.Width - 80) / 2, 120), Size = new Size(80, 30) };

            credits.Controls.Add(btnOk);
            credits.Controls.Add(link);
            credits.Controls.Add(lbl);
            credits.ShowDialog();
        }

        private string GetCurrentRootDirectory()
        {
            if (_fileStore.Count > 0)
            {
                string? baseDir = _fileStore.BaseDirectories[0];
                if (!string.IsNullOrEmpty(baseDir)) return baseDir;
                return Path.GetDirectoryName(_fileStore.GetFullPath(0)) ?? "";
            }
            return "";
        }

        private void PerformResetDefaults()
        {
            _settings.ResetToDefaults();
            _skipSaveOnClose = true;
            Application.Restart();
            Environment.Exit(0);
        }

        #endregion
    }
}