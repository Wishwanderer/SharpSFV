using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using SharpSFV.Models;
using SharpSFV.Interop;
using SharpSFV.Utils;

namespace SharpSFV
{
    public partial class Form1
    {
        // --- SHELL INTEGRATION ACTIONS ---

        /// <summary>
        /// Registers SharpSFV in the Windows Explorer Context Menu via the Registry.
        /// <para>
        /// <b>Strategy:</b> Writes to <c>HKEY_CURRENT_USER</c> (HKCU) instead of HKLM. 
        /// This avoids the need for UAC (Admin) elevation while still allowing the menu to appear for the current user.
        /// </para>
        /// </summary>
        private void PerformRegisterShell()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                string verifyCommand = $"\"{exePath}\" \"%1\"";
                string createCommand = $"\"{exePath}\" -create \"%1\"";

                // Register for Folders (Directory)
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\SharpSFV"))
                {
                    key.SetValue("", "SharpSFV: Create Checksum");
                    key.SetValue("Icon", exePath);
                    using (var cmd = key.CreateSubKey("command")) cmd.SetValue("", createCommand);
                }

                // Register for All Files (*)
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\SharpSFV"))
                {
                    key.SetValue("", "SharpSFV: Create Checksum");
                    key.SetValue("Icon", exePath);
                    using (var cmd = key.CreateSubKey("command")) cmd.SetValue("", createCommand);
                }

                // Register File Association (.sfv)
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.sfv")) key.SetValue("", "SharpSFV.File");

                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SharpSFV.File"))
                {
                    key.SetValue("", "SFV Checksum File");
                    using (var shell = key.CreateSubKey(@"shell\open\command")) shell.SetValue("", verifyCommand);
                    using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", exePath + ",0");
                }

                MessageBox.Show("Shell integration registered successfully!\n\nYou can now Right-Click files or folders to create checksums.", "SharpSFV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error registering shell extension: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PerformUnregisterShell()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\SharpSFV", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\SharpSFV", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SharpSFV.File", false);
                MessageBox.Show("Shell integration removed.", "SharpSFV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error removing shell extension: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --- SMART BAD FILE ACTIONS ---

        /// <summary>
        /// Scans the FileStore for items marked <see cref="ItemStatus.Bad"/> or <see cref="ItemStatus.Error"/> 
        /// and moves them to a "_BAD_FILES" subdirectory relative to their original location.
        /// </summary>
        private void PerformMoveBadFiles()
        {
            if (_fileStore.Count == 0) return;
            if (MessageBox.Show("This will move all files marked as BAD or ERROR to a '_BAD_FILES' subfolder in their respective locations.\n\nContinue?",
                "Move Bad Files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            Cursor.Current = Cursors.WaitCursor;
            int movedCount = 0;
            int errorCount = 0;

            try
            {
                for (int i = 0; i < _fileStore.Count; i++)
                {
                    var status = _fileStore.Statuses[i];
                    if (status == ItemStatus.Bad || status == ItemStatus.Error)
                    {
                        try
                        {
                            string fullPath = _fileStore.GetFullPath(i);
                            if (!File.Exists(fullPath)) continue;

                            string? dir = Path.GetDirectoryName(fullPath);
                            if (dir == null) continue;

                            string badDir = Path.Combine(dir, "_BAD_FILES");
                            if (!Directory.Exists(badDir)) Directory.CreateDirectory(badDir);

                            string fileName = Path.GetFileName(fullPath);
                            string destPath = Path.Combine(badDir, fileName);

                            // Handle filename collisions by appending timestamp
                            if (File.Exists(destPath))
                            {
                                string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                                destPath = Path.Combine(badDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{timestamp}{Path.GetExtension(fileName)}");
                            }

                            File.Move(fullPath, destPath);

                            // Update Store to reflect new location (optional, but good for UI consistency)
                            string? oldRel = _fileStore.RelativePaths[i];
                            if (oldRel != null)
                            {
                                string? relDir = Path.GetDirectoryName(oldRel);
                                string newRel = string.IsNullOrEmpty(relDir)
                                    ? Path.Combine("_BAD_FILES", fileName)
                                    : Path.Combine(relDir, "_BAD_FILES", fileName);

                                _fileStore.RelativePaths[i] = newRel;
                            }
                            movedCount++;
                        }
                        catch { errorCount++; }
                    }
                }
            }
            finally
            {
                Cursor.Current = Cursors.Default;
                lvFiles.Invalidate();
            }

            MessageBox.Show($"Operation Complete.\nMoved: {movedCount}\nErrors: {errorCount}", "Move Bad Files", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void PerformRenameBadFiles()
        {
            if (_fileStore.Count == 0) return;
            if (MessageBox.Show("This will append '.CORRUPT' to all files marked as BAD or ERROR.\n\nContinue?",
                "Rename Bad Files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            Cursor.Current = Cursors.WaitCursor;
            int renamedCount = 0;
            int errorCount = 0;

            try
            {
                for (int i = 0; i < _fileStore.Count; i++)
                {
                    var status = _fileStore.Statuses[i];
                    if (status == ItemStatus.Bad || status == ItemStatus.Error)
                    {
                        try
                        {
                            string fullPath = _fileStore.GetFullPath(i);
                            if (!File.Exists(fullPath)) continue;

                            string newPath = fullPath + ".CORRUPT";
                            File.Move(fullPath, newPath);

                            string? oldName = _fileStore.FileNames[i];
                            if (oldName != null) _fileStore.FileNames[i] = oldName + ".CORRUPT";

                            string? oldRel = _fileStore.RelativePaths[i];
                            if (oldRel != null) _fileStore.RelativePaths[i] = oldRel + ".CORRUPT";

                            renamedCount++;
                        }
                        catch { errorCount++; }
                    }
                }
            }
            finally
            {
                Cursor.Current = Cursors.Default;
                lvFiles.Invalidate();
            }

            MessageBox.Show($"Operation Complete.\nRenamed: {renamedCount}\nErrors: {errorCount}", "Rename Bad Files", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // --- CONTROL ACTIONS (PAUSE / CANCEL) ---

        private void PerformTogglePause()
        {
            if (!_isProcessing) return;

            if (_isPaused)
            {
                // Resume
                _isPaused = false;
                if (_btnPause != null)
                {
                    _btnPause.Text = "Pause";
                    _btnPause.ForeColor = SystemColors.ControlText;
                }

                if (_isJobMode)
                {
                    for (int i = 0; i < _jobStore.Count; i++)
                    {
                        if (_jobStore.Statuses[i] == JobStatus.Paused)
                        {
                            _jobStore.Statuses[i] = JobStatus.InProgress;
                            lvFiles.Invalidate();
                            break;
                        }
                    }
                }

                SetProgressBarColor(Win32Storage.PBST_NORMAL);
                _pauseEvent.Set(); // Signal threads to continue
            }
            else
            {
                // Pause
                _isPaused = true;
                if (_btnPause != null)
                {
                    _btnPause.Text = "Resume";
                    _btnPause.ForeColor = Color.DarkGoldenrod;
                }

                if (_isJobMode)
                {
                    for (int i = 0; i < _jobStore.Count; i++)
                    {
                        if (_jobStore.Statuses[i] == JobStatus.InProgress)
                        {
                            _jobStore.Statuses[i] = JobStatus.Paused;
                            lvFiles.Invalidate();
                            break;
                        }
                    }
                }

                SetProgressBarColor(Win32Storage.PBST_PAUSED);
                _pauseEvent.Reset(); // Block threads
            }
        }

        private void PerformCancelAction()
        {
            if (!_isProcessing) return;
            // If paused, we must unpause to allow threads to exit gracefully upon cancellation
            if (_isPaused) _pauseEvent.Set();
            _cts?.Cancel();

            // Restarting the app is a clean way to ensure all P/Invoke handles and thread states are reset
            Application.Restart();
            Environment.Exit(0);
        }

        /// <summary>
        /// Reads the clipboard for a Hex string and compares it against the selected file's calculated hash.
        /// Useful for quick verification against website checksums.
        /// </summary>
        private void PerformCompareClipboard()
        {
            try
            {
                string text = Clipboard.GetText().Trim();
                if (string.IsNullOrEmpty(text)) return;

                // Validate Hex format
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-fA-F]+$"))
                {
                    MessageBox.Show("Clipboard does not contain a valid Hex hash.", "Invalid Hash", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                int targetIdx = -1;
                if (lvFiles.SelectedIndices.Count == 1) targetIdx = _displayIndices[lvFiles.SelectedIndices[0]];
                else if (_fileStore.Count == 1) targetIdx = 0;

                if (targetIdx == -1)
                {
                    MessageBox.Show("Please select a single file to compare, or filter the list to one item.", "Selection Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (text.Length % 2 != 0) text = "0" + text; // Pad if odd length

                byte[] expectedBytes;
                try { expectedBytes = Convert.FromHexString(text); }
                catch
                {
                    MessageBox.Show("Could not parse clipboard text as a Hex string.", "Parse Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _fileStore.ExpectedHashes[targetIdx] = expectedBytes;

                if (_fileStore.CalculatedHashes[targetIdx] != null)
                {
                    bool match = _fileStore.CalculatedHashes[targetIdx].SequenceEqual(expectedBytes);
                    _fileStore.Statuses[targetIdx] = match ? ItemStatus.OK : ItemStatus.Bad;

                    if (match) System.Media.SystemSounds.Asterisk.Play();
                    else System.Media.SystemSounds.Hand.Play();
                }

                lvFiles.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading clipboard: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --- CONTEXT MENU HANDLERS ---
        private void CtxOpenFolder_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count != 1) return;
            int uiIndex = lvFiles.SelectedIndices[0];
            int storeIndex = _displayIndices[uiIndex];

            string fullPath = _fileStore.GetFullPath(storeIndex);
            try { Process.Start("explorer.exe", $"/select,\"{fullPath}\""); } catch { }
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
                // Format: <HASH> *<PATH>
                foreach (int index in lvFiles.SelectedIndices)
                {
                    int storeIdx = _displayIndices[index];
                    string hash = _fileStore.GetCalculatedHashString(storeIdx);

                    if (string.IsNullOrEmpty(hash) || hash == "Pending") hash = "";

                    string path;
                    if (_settings.ShowFullPaths)
                    {
                        path = _fileStore.GetFullPath(storeIdx);
                    }
                    else
                    {
                        // Use Relative path or just filename if relative is missing
                        path = _fileStore.RelativePaths[storeIdx] ?? _fileStore.FileNames[storeIdx] ?? "";
                    }

                    sb.AppendLine($"{hash} *{path}");
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

        /// <summary>
        /// Saves the current list to a file (SFV, MD5, etc.).
        /// <para>
        /// <b>Logic:</b>
        /// 1. Determines extension based on current Algo.
        /// 2. Iterates the FileStore.
        /// 3. Converts Absolute paths to Relative paths if <see cref="PathStorageMode"/> is Relative.
        /// 4. Prepends optional PathPrefix (Advanced Options).
        /// </para>
        /// </summary>
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
                case HashType.XXHASH3: default: defaultExt = ".xxh3"; fileFilter = "xxHash3 File (*.xxh3)|*.xxh3|Text File (*.txt)|*.txt"; break;
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
                            if (_settings.EnableChecksumComments)
                            {
                                sw.WriteLine($"; Generated by SharpSFV (Signature: {_settings.CustomSignature})");
                                sw.WriteLine($"; Algorithm: {_currentHashType}");
                            }

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
                                                // Fallback if path is on a different drive
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

        // --- SCRIPT GENERATION & EXTRAS ---

        /// <summary>
        /// Generates a Windows Batch (.bat) file to delete all files marked as BAD/ERROR.
        /// </summary>
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

        // --- DIALOG HELPERS ---

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

                    // Update SoA Data
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
            if (lvFiles.SelectedIndices.Count == 0) return;

            int count = lvFiles.SelectedIndices.Count;
            if (MessageBox.Show($"Delete {count} file(s) from disk?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                // Gather UI indices first
                var uiIndices = new List<int>();
                foreach (int idx in lvFiles.SelectedIndices) uiIndices.Add(idx);
                // Sort descending to remove without invalidating subsequent indices within the loop
                uiIndices.Sort((a, b) => b.CompareTo(a));

                lvFiles.BeginUpdate();
                try
                {
                    foreach (int uiIndex in uiIndices)
                    {
                        if (uiIndex >= _displayIndices.Count) continue;
                        int storeIndex = _displayIndices[uiIndex];
                        string fullPath = _fileStore.GetFullPath(storeIndex);

                        try
                        {
                            if (File.Exists(fullPath)) File.Delete(fullPath);

                            // Remove from Store and Display List
                            _fileStore.RemoveAt(storeIndex);
                            _displayIndices.RemoveAt(uiIndex);

                            // Shift subsequent display indices because the Store indices shifted down
                            for (int i = 0; i < _displayIndices.Count; i++)
                            {
                                if (_displayIndices[i] > storeIndex) _displayIndices[i]--;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to delete {fullPath}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    UpdateDisplayList();
                    lvFiles.EndUpdate();
                }
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
            // Sort descending for safe removal
            indicesToRemove.Sort();

            for (int i = indicesToRemove.Count - 1; i >= 0; i--)
            {
                _fileStore.RemoveAt(indicesToRemove[i]);
            }

            // Rebuild Display Indices (simpler than shifting logic for massive removals)
            _displayIndices.Clear();
            for (int i = 0; i < _fileStore.Count; i++) _displayIndices.Add(i);

            UpdateDisplayList();
            lvFiles.EndUpdate();
        }

        private void PerformSelectAllAction()
        {
            lvFiles.BeginUpdate();
            for (int i = 0; i < lvFiles.VirtualListSize; i++)
            {
                lvFiles.Items[i].Selected = true;
            }
            lvFiles.EndUpdate();
        }

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
                Text = "SharpSFV v2.63\nInspired by QuickSFV\n\nCreated by L33T",
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
    }
}