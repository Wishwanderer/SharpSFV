using SharpSFV.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
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
            // Fix CS8600: Null Coalesce if name is missing (shouldn't happen for valid files)
            string currentName = _fileStore.FileNames[storeIndex] ?? "";

            string newName = SimpleInputDialog.ShowDialog("Rename File", "Enter new filename:", currentName);

            if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
            {
                try
                {
                    // Fix CS8600: BaseDirectories might be null in SoA
                    string baseDir = _fileStore.BaseDirectories[storeIndex] ?? "";

                    // Note: BaseDirectories is the scanning root, not necessarily the immediate parent.
                    // We need the directory of the actual file.
                    string? fileDir = Path.GetDirectoryName(currentPath);
                    if (fileDir == null) fileDir = baseDir;

                    string newPath = Path.Combine(fileDir, newName);

                    File.Move(currentPath, newPath);

                    // Update SoA Arrays
                    _fileStore.FileNames[storeIndex] = newName;

                    // Update Relative Path Logic
                    // Fix CS8600: RelativePaths might be null
                    string oldRel = _fileStore.RelativePaths[storeIndex] ?? currentName;
                    string? relDir = Path.GetDirectoryName(oldRel);
                    string newRel = string.IsNullOrEmpty(relDir) ? newName : Path.Combine(relDir, newName);

                    // We don't pool here since it's a unique rename
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

                    // Remove from Store O(N) but rare operation
                    _fileStore.RemoveAt(storeIndex);

                    // Rebuild Display Indices completely to ensure consistency
                    // (Faster than trying to shift indices in the list)
                    _displayIndices.RemoveAt(uiIndex);

                    // Since Store shifted, all indices > storeIndex in _displayIndices are now invalid (off by 1)
                    // We must decrement them.
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

            // Get Store Indices to remove
            var indicesToRemove = new List<int>();
            foreach (int uiIdx in lvFiles.SelectedIndices)
            {
                indicesToRemove.Add(_displayIndices[uiIdx]);
            }
            indicesToRemove.Sort(); // Ascending

            // Remove from Store in reverse order to preserve validity of lower indices
            for (int i = indicesToRemove.Count - 1; i >= 0; i--)
            {
                _fileStore.RemoveAt(indicesToRemove[i]);
            }

            // Rebuild Display List - Simplest approach after bulk removal
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
            for (int i = 0; i < lvFiles.VirtualListSize; i++) lvFiles.Items[i].Selected = true;
            lvFiles.EndUpdate();
        }

        private void PerformCopyAction()
        {
            if (lvFiles.SelectedIndices.Count == 0) return;
            var sb = new StringBuilder();
            foreach (int index in lvFiles.SelectedIndices)
            {
                int storeIdx = _displayIndices[index];
                // Fix CS8600: Handle null name
                string name = _fileStore.FileNames[storeIdx] ?? "";
                sb.AppendLine($"{name}\t{_fileStore.GetCalculatedHashString(storeIdx)}\t{_fileStore.Statuses[storeIdx]}");
            }
            try { Clipboard.SetText(sb.ToString()); } catch (Exception ex) { MessageBox.Show(ex.Message); }
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
            if (_fileStore.Count == 0) { MessageBox.Show("No files to save."); return; }

            string defaultFileName = "checksums";
            string initialDirectory = GetCurrentRootDirectory();

            // Try to find a sensible default name from the first item
            if (_fileStore.Count > 0)
            {
                string? baseDir = _fileStore.BaseDirectories[0];
                if (!string.IsNullOrEmpty(baseDir))
                {
                    try { defaultFileName = new DirectoryInfo(baseDir).Name; } catch { }
                }
            }

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

                            // Iterate all items in store
                            for (int i = 0; i < _fileStore.Count; i++)
                            {
                                if (_fileStore.IsSummaryRows[i]) continue;

                                string hash = _fileStore.GetCalculatedHashString(i);
                                ItemStatus status = _fileStore.Statuses[i];

                                if (!hash.Contains("...") && status != ItemStatus.Queued && status != ItemStatus.Pending && !string.IsNullOrEmpty(hash))
                                {
                                    string pathToWrite;
                                    string fullPath = _fileStore.GetFullPath(i);

                                    if (_menuOptionsAbsolutePaths != null && _menuOptionsAbsolutePaths.Checked)
                                        pathToWrite = fullPath;
                                    else
                                        try { pathToWrite = Path.GetRelativePath(saveDirectory, fullPath); } catch { pathToWrite = fullPath; }

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

                            // Group using indices
                            var indices = Enumerable.Range(0, _fileStore.Count).Where(i => !_fileStore.IsSummaryRows[i]);
                            var groups = indices.GroupBy(i => _fileStore.GetCalculatedHashString(i));

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

                            var indices = Enumerable.Range(0, _fileStore.Count).Where(i => !_fileStore.IsSummaryRows[i]);
                            var groups = indices.GroupBy(i => _fileStore.GetCalculatedHashString(i));

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
                Text = "SharpSFV v2.70\nInspired by QuickSFV\n\nCreated by L33T.",
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
                // Fix CS8600: Handle null BaseDirectories[0]
                string? baseDir = _fileStore.BaseDirectories[0];
                if (!string.IsNullOrEmpty(baseDir)) return baseDir;
                return Path.GetDirectoryName(_fileStore.GetFullPath(0)) ?? "";
            }
            return "";
        }

        #endregion
    }
}