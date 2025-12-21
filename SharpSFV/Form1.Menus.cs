using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        private void SetupCustomMenu()
        {
            _menuStrip = new MenuStrip { Dock = DockStyle.Top };
            var menuFile = new ToolStripMenuItem("File");
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Open...", null, (s, e) => PerformOpenAction()) { ShortcutKeys = Keys.Control | Keys.O });
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Save As...", null, (s, e) => PerformSaveAction()) { ShortcutKeys = Keys.Control | Keys.S });
            menuFile.DropDownItems.Add(new ToolStripSeparator());
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit()) { ShortcutKeys = Keys.Alt | Keys.F4 });

            var menuEdit = new ToolStripMenuItem("Edit");
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Copy Details", null, (s, e) => PerformCopyAction()) { ShortcutKeys = Keys.Control | Keys.C });
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Paste Paths", null, (s, e) => PerformPasteAction()) { ShortcutKeys = Keys.Control | Keys.V });
            menuEdit.DropDownItems.Add(new ToolStripSeparator());
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Select All", null, (s, e) => PerformSelectAllAction()) { ShortcutKeys = Keys.Control | Keys.A });

            var menuOptions = new ToolStripMenuItem("Options");
            _menuOptionsTime = new ToolStripMenuItem("Enable Time Elapsed Tab", null, (s, e) => ToggleTimeColumn()) { CheckOnClick = true };
            _menuOptionsAbsolutePaths = new ToolStripMenuItem("Always Save Absolute Paths", null, (s, e) => { _settings.UseAbsolutePaths = !_settings.UseAbsolutePaths; }) { CheckOnClick = true };
            _menuOptionsFilter = new ToolStripMenuItem("Show Search/Filter Bar", null, (s, e) => ToggleFilterPanel()) { CheckOnClick = true };
            _menuOptionsHDD = new ToolStripMenuItem("Force HDD Mode (Always Sequential)", null, (s, e) => { _settings.OptimizeForHDD = !_settings.OptimizeForHDD; }) { CheckOnClick = true };

            var menuAlgo = new ToolStripMenuItem("Default Hashing Algorithm");
            AddAlgoMenuItem(menuAlgo, "xxHash-3 (128-bit)", HashType.XxHash3);
            AddAlgoMenuItem(menuAlgo, "CRC-32 (SFV)", HashType.Crc32);
            AddAlgoMenuItem(menuAlgo, "MD5", HashType.MD5);
            AddAlgoMenuItem(menuAlgo, "SHA-1", HashType.SHA1);
            AddAlgoMenuItem(menuAlgo, "SHA-256", HashType.SHA256);

            menuOptions.DropDownItems.AddRange(new ToolStripItem[] { _menuOptionsTime, _menuOptionsAbsolutePaths, _menuOptionsFilter, _menuOptionsHDD, menuAlgo, new ToolStripSeparator(), new ToolStripMenuItem("Generate 'Delete BAD Files' Script", null, (s, e) => PerformBatchExport()) });

            var menuHelp = new ToolStripMenuItem("Help");
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("View current Disk Data", null, (s, e) => ShowDiskDebugInfo()));
            menuHelp.DropDownItems.Add(new ToolStripSeparator());
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("Credits", null, (s, e) => ShowCredits()));
            _menuStrip.Items.AddRange(new ToolStripItem[] { menuFile, menuEdit, menuOptions, menuHelp });
        }

        private void SetupContextMenu()
        {
            _ctxMenu = new ContextMenuStrip();
            var itemOpen = new ToolStripMenuItem("Open Containing Folder", null, CtxOpenFolder_Click);
            var itemCopyPath = new ToolStripMenuItem("Copy Full Path", null, CtxCopyPath_Click);
            var itemCopyHash = new ToolStripMenuItem("Copy Hash", null, CtxCopyHash_Click);
            var itemRename = new ToolStripMenuItem("Rename File...", null, CtxRename_Click);
            var itemDelete = new ToolStripMenuItem("Delete File", null, CtxDelete_Click);
            var itemRemove = new ToolStripMenuItem("Remove from List", null, CtxRemoveList_Click);

            _ctxMenu.Items.AddRange(new ToolStripItem[] { itemOpen, new ToolStripSeparator(), itemCopyPath, itemCopyHash, new ToolStripSeparator(), itemRename, itemDelete, itemRemove });
            _ctxMenu.Opening += (s, e) =>
            {
                if (lvFiles.SelectedIndices.Count == 0) { e.Cancel = true; return; }
                bool singleSel = lvFiles.SelectedIndices.Count == 1;
                bool fileExists = false;
                if (singleSel)
                {
                    var data = _displayList[lvFiles.SelectedIndices[0]];
                    fileExists = File.Exists(data.FullPath);
                }
                itemOpen.Enabled = singleSel;
                itemRename.Enabled = singleSel && fileExists;
                itemDelete.Enabled = singleSel && fileExists;
                itemCopyPath.Enabled = singleSel;
                itemCopyHash.Enabled = singleSel;
            };
            lvFiles.ContextMenuStrip = _ctxMenu;
        }

        #region Actions

        private void PerformSelectAllAction()
        {
            lvFiles.BeginUpdate();
            for (int i = 0; i < lvFiles.VirtualListSize; i++) lvFiles.Items[i].Selected = true;
            lvFiles.EndUpdate();
        }

        private void PerformCopyAction()
        {
            if (lvFiles.SelectedIndices.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (int index in lvFiles.SelectedIndices)
            {
                var data = _displayList[index];
                sb.AppendLine($"{data.FileName}\t{data.CalculatedHash}\t{data.Status}");
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
            if (_allItems.Count == 0) { MessageBox.Show("No files to save."); return; }

            string defaultFileName = "checksums";
            string initialDirectory = "";
            var firstData = _allItems.FirstOrDefault();

            if (firstData != null && !string.IsNullOrEmpty(firstData.BaseDirectory)) { defaultFileName = Path.GetFileName(firstData.BaseDirectory); initialDirectory = firstData.BaseDirectory; }
            else if (firstData != null) initialDirectory = Path.GetDirectoryName(firstData.FullPath) ?? "";

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

            using (SaveFileDialog sfd = new SaveFileDialog { Filter = fileFilter, FileName = defaultFileName + defaultExt, InitialDirectory = initialDirectory })
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

                            foreach (var data in _allItems)
                            {
                                string hash = data.CalculatedHash;
                                if (!hash.Contains("...") && !hash.Equals("Pending") && !string.IsNullOrEmpty(hash) && !data.IsSummaryRow)
                                {
                                    string pathToWrite;
                                    if (_menuOptionsAbsolutePaths != null && _menuOptionsAbsolutePaths.Checked) pathToWrite = data.FullPath;
                                    else try { pathToWrite = Path.GetRelativePath(saveDirectory, data.FullPath); } catch { pathToWrite = data.FullPath; }
                                    sw.WriteLine($"{hash} *{pathToWrite}");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving checksum file: " + ex.Message); }
                }
            }
        }

        private void PerformBatchExport()
        {
            var badItems = _allItems.Where(i => i.Status == "BAD" || i.Status.Contains("ERROR")).ToList();
            if (badItems.Count == 0) { MessageBox.Show("No files marked as 'BAD' found."); return; }

            using (SaveFileDialog sfd = new SaveFileDialog { Filter = "Batch Script (*.bat)|*.bat", FileName = "delete_bad_files.bat" })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine("@echo off\necho Deleting BAD files...");
                            foreach (var item in badItems) sw.WriteLine($"del \"{item.FullPath}\"");
                            sw.WriteLine("pause");
                        }
                        MessageBox.Show("Script saved.");
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving script: " + ex.Message); }
                }
            }
        }

        #endregion

        #region Helper Methods

        private void AddAlgoMenuItem(ToolStripMenuItem parent, string text, HashType type)
        {
            var item = new ToolStripMenuItem(text, null, (s, e) => SetAlgorithm(type));
            parent.DropDownItems.Add(item);
            _algoMenuItems[type] = item;
        }

        private void SetAlgorithm(HashType type)
        {
            _currentHashType = type;
            foreach (var kvp in _algoMenuItems) kvp.Value.Checked = (kvp.Key == type);
            if (!_isProcessing) this.Text = $"SharpSFV - Create [{_currentHashType}]";
        }

        private void ToggleTimeColumn()
        {
            _settings.ShowTimeTab = _menuOptionsTime?.Checked ?? false;
            bool exists = lvFiles.Columns.Cast<ColumnHeader>().Any(ch => ch.Text == "Time");
            if (_settings.ShowTimeTab && !exists) lvFiles.Columns.Add("Time", 80);
            else if (!_settings.ShowTimeTab && exists) lvFiles.Columns.Remove(lvFiles.Columns.Cast<ColumnHeader>().First(ch => ch.Text == "Time"));
            lvFiles.Invalidate();
        }

        private void ToggleFilterPanel()
        {
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;
            if (_filterPanel != null) _filterPanel.Visible = _settings.ShowFilterPanel;
        }

        private void ShowDiskDebugInfo()
        {
            string pathToCheck = Application.ExecutablePath;
            if (_allItems.Count > 0 && !string.IsNullOrEmpty(_allItems[0].FullPath))
            {
                pathToCheck = _allItems[0].FullPath;
            }

            string info = DriveDetector.GetDebugInfo(pathToCheck);

            // Create a custom dialog for easy copying
            Form debugForm = new Form();
            debugForm.Text = "Disk Debug Info";
            debugForm.Size = new Size(500, 450);
            debugForm.StartPosition = FormStartPosition.CenterParent;
            debugForm.MinimizeBox = false;
            debugForm.MaximizeBox = false;

            TextBox txtInfo = new TextBox();
            txtInfo.Multiline = true;
            txtInfo.ReadOnly = true;
            txtInfo.ScrollBars = ScrollBars.Vertical;
            txtInfo.Text = info;
            txtInfo.Dock = DockStyle.Fill;
            // Use generic monospace to ensure alignment
            txtInfo.Font = new Font(FontFamily.GenericMonospace, 9F, FontStyle.Regular);
            txtInfo.Select(0, 0); // Deselect text initially

            Panel pnlBottom = new Panel();
            pnlBottom.Height = 40;
            pnlBottom.Dock = DockStyle.Bottom;

            Button btnCopy = new Button();
            btnCopy.Text = "Copy to Clipboard";
            btnCopy.Size = new Size(130, 25);
            btnCopy.Location = new Point(10, 8);
            btnCopy.Click += (s, e) => { Clipboard.SetText(info); MessageBox.Show("Copied to clipboard!", "SharpSFV", MessageBoxButtons.OK, MessageBoxIcon.Information); };

            Button btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.Size = new Size(75, 25);
            btnClose.Location = new Point(400, 8);
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.DialogResult = DialogResult.OK;

            pnlBottom.Controls.Add(btnCopy);
            pnlBottom.Controls.Add(btnClose);

            debugForm.Controls.Add(txtInfo);
            debugForm.Controls.Add(pnlBottom);

            debugForm.ShowDialog();
        }

        private void ShowCredits()
        {
            Form credits = new Form();
            credits.Text = "About SharpSFV";
            credits.Size = new Size(300, 200);
            credits.StartPosition = FormStartPosition.CenterParent;
            credits.FormBorderStyle = FormBorderStyle.FixedDialog;
            credits.MaximizeBox = false;
            credits.MinimizeBox = false;

            Label lbl = new Label { Text = "SharpSFV v2.30\n\nCreated by L33T.\nInspired by QuickSFV.", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 80, Padding = new Padding(0, 20, 0, 0) };
            LinkLabel link = new LinkLabel { Text = "Visit GitHub Repository", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top };
            link.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/Wishwanderer/SharpSFV", UseShellExecute = true }); } catch { } };
            Button btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point((credits.ClientSize.Width - 80) / 2, 120), Size = new Size(80, 30) };

            credits.Controls.Add(btnOk);
            credits.Controls.Add(link);
            credits.Controls.Add(lbl);
            credits.ShowDialog();
        }

        #endregion

        #region Context Menu Handlers

        private void CtxOpenFolder_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count != 1) return;
            var data = _displayList[lvFiles.SelectedIndices[0]];
            try { Process.Start("explorer.exe", $"/select,\"{data.FullPath}\""); } catch { }
        }
        private void CtxCopyPath_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count > 0)
                Clipboard.SetText(_displayList[lvFiles.SelectedIndices[0]].FullPath);
        }
        private void CtxCopyHash_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count > 0)
                Clipboard.SetText(_displayList[lvFiles.SelectedIndices[0]].CalculatedHash);
        }
        private void CtxRename_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count != 1) return;
            var data = _displayList[lvFiles.SelectedIndices[0]];
            string newName = SimpleInputDialog.ShowDialog("Rename File", "Enter new filename:", Path.GetFileName(data.FullPath));
            if (!string.IsNullOrWhiteSpace(newName))
            {
                try
                {
                    File.Move(data.FullPath, Path.Combine(Path.GetDirectoryName(data.FullPath)!, newName));
                    data.FullPath = Path.Combine(Path.GetDirectoryName(data.FullPath)!, newName);
                    data.FileName = newName;
                    lvFiles.Invalidate();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }
        private void CtxDelete_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedIndices.Count != 1) return;
            var data = _displayList[lvFiles.SelectedIndices[0]];
            if (MessageBox.Show("Delete?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                try
                {
                    File.Delete(data.FullPath);
                    _allItems.Remove(data);
                    _displayList.Remove(data);
                    UpdateDisplayList();
                }
                catch { }
            }
        }
        private void CtxRemoveList_Click(object? sender, EventArgs e)
        {
            var indices = lvFiles.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
            foreach (int i in indices)
            {
                var data = _displayList[i];
                _displayList.RemoveAt(i);
                _allItems.Remove(data);
            }
            UpdateDisplayList();
        }

        #endregion
    }
}