using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1 : Form
    {
        // ... (Existing fields) ...
        private AppSettings _settings;
        private ListViewColumnSorter _lvwColumnSorter;

        private bool _isProcessing = false;
        private HashType _currentHashType = HashType.XxHash3;

        private MenuStrip? _menuStrip;
        private ContextMenuStrip? _ctxMenu; // NEW: Context Menu Field
        private ToolStripMenuItem? _menuOptionsTime;
        private ToolStripMenuItem? _menuOptionsAbsolutePaths;
        private Dictionary<HashType, ToolStripMenuItem> _algoMenuItems = new Dictionary<HashType, ToolStripMenuItem>();
        private Panel? _statsPanel;
        private Label? _lblProgress;
        private Label? _lblStatsRow;
        private ProgressBar progressBarCurrent = new ProgressBar();

        private readonly HashSet<string> _verificationExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xxh3", ".xxh", ".sum", ".md5", ".sfv", ".sha1", ".sha256", ".txt"
        };

        private const int MaxWindowsPathLength = 256;

        public Form1()
        {
            InitializeComponent();

            _settings = new AppSettings(Application.ExecutablePath);
            _lvwColumnSorter = new ListViewColumnSorter();

            _settings.Load();

            this.lvFiles.ListViewItemSorter = _lvwColumnSorter;
            this.lvFiles.ColumnClick += LvFiles_ColumnClick;
            this.FormClosing += Form1_FormClosing;

            SetupCustomMenu();
            SetupContextMenu(); // NEW: Call setup for context menu
            ApplySettings();
            SetupStatsPanel();
            SetupDragDrop();
            SetupLayout();

            this.Text = "SharpSFV";
        }

        // ... (Form1_Load, ApplySettings, Form1_FormClosing, SetupLayout, SetupStatsPanel, SetupCustomMenu, AddAlgoMenuItem, SetAlgorithm, SetupDragDrop, LvFiles_ColumnClick, UpdateStats, Form1_DragDrop, HandleDroppedPaths, FindCommonBasePath, RunHashCreation, RunVerification unchanged) ...

        // PASTE THE EXISTING METHODS HERE (omitted for brevity, assume previous code exists)
        // Ensure you keep all previous logic for RunHashCreation, RunVerification, etc.

        // ... [Insert previous methods here] ...

        private async void Form1_Load(object? sender, EventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) await HandleDroppedPaths(args.Skip(1).ToArray());
        }

        private void ApplySettings()
        {
            if (_settings.WindowSize.Width > 100) this.Size = _settings.WindowSize;

            if (_settings.HasCustomLocation)
            {
                this.StartPosition = FormStartPosition.Manual;
                this.Location = _settings.WindowLocation;

                bool isOnScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(this.Bounds));
                if (!isOnScreen) this.StartPosition = FormStartPosition.CenterScreen;
            }

            if (_menuOptionsTime != null)
                _menuOptionsTime.Checked = _settings.ShowTimeTab;

            if (_menuOptionsAbsolutePaths != null)
                _menuOptionsAbsolutePaths.Checked = _settings.UseAbsolutePaths;

            ToggleTimeColumn();
            SetAlgorithm(_settings.DefaultAlgo);
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            bool timeEnabled = _menuOptionsTime?.Checked ?? false;
            _settings.UseAbsolutePaths = _menuOptionsAbsolutePaths?.Checked ?? false;
            _settings.Save(this, timeEnabled, _currentHashType);
        }

        private void SetupLayout()
        {
            this.Controls.Clear();
            Panel progressPanel = new Panel { Height = 40, Dock = DockStyle.Bottom };

            progressBarTotal.Dock = DockStyle.Bottom;
            progressBarTotal.Height = 20;

            progressBarCurrent.Dock = DockStyle.Top;
            progressBarCurrent.Height = 20;
            progressBarCurrent.Minimum = 0;
            progressBarCurrent.Maximum = 100;

            progressPanel.Controls.Add(progressBarCurrent);
            progressPanel.Controls.Add(progressBarTotal);

            this.Controls.Add(progressPanel);
            lvFiles.Dock = DockStyle.Fill;
            this.Controls.Add(lvFiles);
            if (_statsPanel != null) this.Controls.Add(_statsPanel);
            if (_menuStrip != null) this.Controls.Add(_menuStrip);
        }

        // ... (Keep SetupStatsPanel, SetupCustomMenu, etc. exactly as they were in previous steps)

        private void SetupStatsPanel()
        {
            _statsPanel = new Panel { Height = 50, Dock = DockStyle.Top, BackColor = SystemColors.ControlLight, Padding = new Padding(10, 5, 10, 5) };
            _lblProgress = new Label { Text = "Ready", AutoSize = true, Font = new Font(this.Font, FontStyle.Bold), Location = new Point(10, 8) };
            _lblStatsRow = new Label { Text = "OK: 0     BAD: 0     MISSING: 0", AutoSize = true, Location = new Point(10, 28) };
            _statsPanel.Controls.Add(_lblProgress);
            _statsPanel.Controls.Add(_lblStatsRow);
        }

        private void SetupCustomMenu()
        {
            _menuStrip = new MenuStrip { Dock = DockStyle.Top };

            var menuFile = new ToolStripMenuItem("File");
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Open...", null, (s, e) => PerformOpenAction()) { ShortcutKeys = Keys.Control | Keys.O });
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Save As...", null, (s, e) => PerformSaveAction()) { ShortcutKeys = Keys.Control | Keys.S });
            menuFile.DropDownItems.Add(new ToolStripSeparator());
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit()) { ShortcutKeys = Keys.Alt | Keys.F4 });

            var menuOptions = new ToolStripMenuItem("Options");
            _menuOptionsTime = new ToolStripMenuItem("Enable Time Elapsed Tab", null, (s, e) => ToggleTimeColumn()) { CheckOnClick = true };

            _menuOptionsAbsolutePaths = new ToolStripMenuItem("Always Save Absolute Paths", null, (s, e) => {
                _settings.UseAbsolutePaths = !_settings.UseAbsolutePaths;
            })
            { CheckOnClick = true };

            var menuAlgo = new ToolStripMenuItem("Default Hashing Algorithm");
            AddAlgoMenuItem(menuAlgo, "xxHash-3 (128-bit)", HashType.XxHash3);
            AddAlgoMenuItem(menuAlgo, "CRC-32 (SFV)", HashType.Crc32);
            AddAlgoMenuItem(menuAlgo, "MD5", HashType.MD5);
            AddAlgoMenuItem(menuAlgo, "SHA-1", HashType.SHA1);
            AddAlgoMenuItem(menuAlgo, "SHA-256", HashType.SHA256);

            menuOptions.DropDownItems.AddRange(new ToolStripItem[] {
                _menuOptionsTime,
                _menuOptionsAbsolutePaths,
                menuAlgo,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Generate 'Delete BAD Files' Script", null, (s, e) => PerformBatchExport())
            });

            var menuHelp = new ToolStripMenuItem("Help");
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("Credits", null, (s, e) => ShowCredits()));

            _menuStrip.Items.AddRange(new ToolStripItem[] { menuFile, menuOptions, menuHelp });
        }

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

        // NEW: Context Menu Setup and Handlers
        private void SetupContextMenu()
        {
            _ctxMenu = new ContextMenuStrip();

            var itemOpen = new ToolStripMenuItem("Open Containing Folder", null, CtxOpenFolder_Click);
            var itemCopyPath = new ToolStripMenuItem("Copy Full Path", null, CtxCopyPath_Click);
            var itemCopyHash = new ToolStripMenuItem("Copy Hash", null, CtxCopyHash_Click);
            var itemRename = new ToolStripMenuItem("Rename File...", null, CtxRename_Click);
            var itemDelete = new ToolStripMenuItem("Delete File", null, CtxDelete_Click); // Physical Delete
            var itemRemove = new ToolStripMenuItem("Remove from List", null, CtxRemoveList_Click); // List remove

            _ctxMenu.Items.AddRange(new ToolStripItem[] {
                itemOpen,
                new ToolStripSeparator(),
                itemCopyPath,
                itemCopyHash,
                new ToolStripSeparator(),
                itemRename,
                itemDelete,
                itemRemove
            });

            _ctxMenu.Opening += (s, e) =>
            {
                if (lvFiles.SelectedItems.Count == 0)
                {
                    e.Cancel = true;
                    return;
                }

                // Enable/Disable logic based on selection count or file status
                bool singleSel = lvFiles.SelectedItems.Count == 1;
                bool fileExists = false;

                if (singleSel && lvFiles.SelectedItems[0].Tag is FileItemData data)
                {
                    fileExists = File.Exists(data.FullPath);
                }

                itemOpen.Enabled = singleSel;
                itemRename.Enabled = singleSel && fileExists; // Can't rename if missing
                itemDelete.Enabled = singleSel && fileExists; // Can't physically delete if missing
                itemCopyPath.Enabled = singleSel;
                itemCopyHash.Enabled = singleSel;
            };

            lvFiles.ContextMenuStrip = _ctxMenu;
        }

        private void CtxOpenFolder_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count != 1) return;
            if (lvFiles.SelectedItems[0].Tag is FileItemData data)
            {
                try
                {
                    if (File.Exists(data.FullPath))
                    {
                        // Select the file in explorer
                        Process.Start("explorer.exe", $"/select,\"{data.FullPath}\"");
                    }
                    else if (Directory.Exists(Path.GetDirectoryName(data.FullPath)))
                    {
                        // If file missing, just open the folder
                        Process.Start("explorer.exe", Path.GetDirectoryName(data.FullPath)!);
                    }
                }
                catch (Exception ex) { MessageBox.Show("Error opening folder: " + ex.Message); }
            }
        }

        private void CtxCopyPath_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count > 0 && lvFiles.SelectedItems[0].Tag is FileItemData data)
                Clipboard.SetText(data.FullPath);
        }

        private void CtxCopyHash_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count > 0)
            {
                // Hash is in SubItem 1
                string hash = lvFiles.SelectedItems[0].SubItems[1].Text;
                if (!string.IsNullOrEmpty(hash) && !hash.Contains("..."))
                    Clipboard.SetText(hash);
            }
        }

        private void CtxDelete_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count != 1) return;
            var item = lvFiles.SelectedItems[0];
            if (item.Tag is FileItemData data)
            {
                if (MessageBox.Show($"Are you sure you want to permanently delete:\n{data.FullPath}?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try
                    {
                        File.Delete(data.FullPath);
                        lvFiles.Items.Remove(item);
                    }
                    catch (Exception ex) { MessageBox.Show("Error deleting file: " + ex.Message); }
                }
            }
        }

        private void CtxRemoveList_Click(object? sender, EventArgs e)
        {
            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
                lvFiles.Items.Remove(item);
            }
        }

        private void CtxRename_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count != 1) return;
            var item = lvFiles.SelectedItems[0];
            if (item.Tag is FileItemData data)
            {
                string dir = Path.GetDirectoryName(data.FullPath) ?? "";
                string oldName = Path.GetFileName(data.FullPath);

                string newName = SimpleInputDialog.ShowDialog("Rename File", "Enter new filename:", oldName);
                if (!string.IsNullOrWhiteSpace(newName) && newName != oldName)
                {
                    string newPath = Path.Combine(dir, newName);
                    try
                    {
                        File.Move(data.FullPath, newPath);

                        // Update Data
                        data.FullPath = newPath;
                        if (!string.IsNullOrEmpty(data.RelativePath))
                            data.RelativePath = Path.Combine(Path.GetDirectoryName(data.RelativePath) ?? "", newName);

                        // Update UI
                        item.Text = newName;
                    }
                    catch (Exception ex) { MessageBox.Show("Error renaming file: " + ex.Message); }
                }
            }
        }

        // ... (Keep existing methods: SetupDragDrop, LvFiles_ColumnClick, UpdateStats, Form1_DragDrop, HandleDroppedPaths, FindCommonBasePath, RunHashCreation, RunVerification, PerformBatchExport, PerformOpenAction, PerformSaveAction, ToggleTimeColumn, ShowCredits) ...
        // INCLUDE ALL PREVIOUS LOGIC HERE FOR THOSE METHODS

        private void SetupDragDrop()
        {
            this.AllowDrop = true;
            this.DragEnter += (s, e) => { if (e.Data!.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            this.DragDrop += Form1_DragDrop;
        }

        private void LvFiles_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            if (_isProcessing) return;
            if (e.Column != _lvwColumnSorter.SortColumn)
            {
                _lvwColumnSorter.SortColumn = e.Column;
                _lvwColumnSorter.Order = SortOrder.Ascending;
            }
            else
            {
                if (_lvwColumnSorter.Order == SortOrder.None) _lvwColumnSorter.Order = SortOrder.Ascending;
                else if (_lvwColumnSorter.Order == SortOrder.Ascending) _lvwColumnSorter.Order = SortOrder.Descending;
                else _lvwColumnSorter.Order = SortOrder.None;
            }
            this.lvFiles.Sort();
        }

        private void UpdateStats(int current, int total, int ok, int bad, int missing)
        {
            if (_lblProgress != null) _lblProgress.Text = $"Completed files: {current} / {total}";
            if (_lblStatsRow != null)
            {
                _lblStatsRow.Text = $"OK: {ok}     BAD: {bad}     MISSING: {missing}";
                if (bad > 0 || missing > 0) _lblStatsRow.ForeColor = Color.Red;
                else if (ok > 0) _lblStatsRow.ForeColor = Color.DarkGreen;
                else _lblStatsRow.ForeColor = Color.Black;
            }
        }

        private async void Form1_DragDrop(object? sender, DragEventArgs e)
        {
            if (_isProcessing) return;
            string[]? paths = (string[]?)e.Data!.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0)
            {
                await HandleDroppedPaths(paths);
            }
        }

        private async Task HandleDroppedPaths(string[] paths)
        {
            if (paths.Length == 0) return;

            bool containsFolder = paths.Any(p => Directory.Exists(p));

            _lvwColumnSorter.SortColumn = -1;
            _lvwColumnSorter.Order = SortOrder.None;
            this.lvFiles.ListViewItemSorter = null;

            if (!containsFolder && paths.Length == 1 && _verificationExtensions.Contains(Path.GetExtension(paths[0])) && File.Exists(paths[0]))
            {
                await RunVerification(paths[0]);
            }
            else
            {
                string baseDirectory = "";
                if (paths.Length > 0)
                {
                    baseDirectory = paths.FirstOrDefault(p => Directory.Exists(p)) ?? "";
                    if (string.IsNullOrEmpty(baseDirectory) && File.Exists(paths[0]))
                    {
                        baseDirectory = Path.GetDirectoryName(paths[0]) ?? "";
                    }
                    else if (paths.Length > 1 && string.IsNullOrEmpty(baseDirectory))
                    {
                        var directories = paths.Select(p => Path.GetDirectoryName(p) ?? "").Distinct().ToList();
                        if (directories.Count == 1) baseDirectory = directories[0];
                        else if (directories.Count > 1) baseDirectory = FindCommonBasePath(directories);
                    }
                }

                List<string> allFilesToHash = new List<string>();
                foreach (string path in paths)
                {
                    if (File.Exists(path)) allFilesToHash.Add(path);
                    else if (Directory.Exists(path))
                    {
                        try { allFilesToHash.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)); }
                        catch (Exception ex) { MessageBox.Show($"Error accessing folder '{path}': {ex.Message}", "Folder Access Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    }
                }
                await RunHashCreation(allFilesToHash.ToArray(), baseDirectory);
            }

            this.lvFiles.ListViewItemSorter = _lvwColumnSorter;
        }

        private string FindCommonBasePath(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return "";
            if (paths.Count == 1) return paths[0];

            string[] shortestPathParts = paths.OrderBy(p => p.Length).First().Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string commonPath = "";

            for (int i = 0; i < shortestPathParts.Length; i++)
            {
                string currentSegment = shortestPathParts[i];
                bool allMatch = true;
                foreach (string path in paths)
                {
                    string[] otherPathParts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    if (i >= otherPathParts.Length || !otherPathParts[i].Equals(currentSegment, StringComparison.OrdinalIgnoreCase))
                    {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch) commonPath = Path.Combine(commonPath, currentSegment);
                else break;
            }
            if (commonPath.Length > 0 && commonPath.IndexOf(':') == 1 && Path.IsPathRooted(paths[0]))
            {
                return paths[0].Substring(0, 3) + commonPath.Substring(3);
            }
            return commonPath;
        }

        private async Task RunHashCreation(string[] filePaths, string baseDirectory)
        {
            _isProcessing = true;
            SetupUIForMode("Creation");
            UpdateStats(0, filePaths.Length, 0, 0, 0);

            List<ListViewItem> items = new List<ListViewItem>();
            int originalIndex = 0;
            int skippedCount = 0;

            foreach (var fullPath in filePaths)
            {
                if (Directory.Exists(fullPath)) continue;

                if (fullPath.Length > MaxWindowsPathLength)
                {
                    MessageBox.Show($"Skipping file due to excessively long path (>{MaxWindowsPathLength} chars): {fullPath}", "Path Too Long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    skippedCount++;
                    continue;
                }

                string fileName = Path.GetFileName(fullPath);
                if (fileName.Any(c => Path.GetInvalidFileNameChars().Contains(c)) || fullPath.Any(c => Path.GetInvalidPathChars().Contains(c)))
                {
                    MessageBox.Show($"Skipping file due to invalid characters in filename: {fileName}", "Invalid Characters", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    skippedCount++;
                    continue;
                }

                string relativePath = "";
                if (!string.IsNullOrEmpty(baseDirectory))
                {
                    try { relativePath = Path.GetRelativePath(baseDirectory, fullPath); }
                    catch { relativePath = fullPath; }
                }
                else relativePath = Path.GetFileName(fullPath);

                var item = new ListViewItem(Path.GetFileName(fullPath));
                item.SubItems.AddRange(new[] { "Calculating...", "Pending", "" });
                item.Tag = new FileItemData
                {
                    FullPath = fullPath,
                    RelativePath = relativePath,
                    BaseDirectory = baseDirectory,
                    Index = originalIndex++
                };
                items.Add(item);
                lvFiles.Items.Add(item);
            }

            if (items.Count == 0)
            {
                _isProcessing = false;
                UpdateStats(0, 0, 0, 0, 0);
                this.Text = $"SharpSFV - Create [{_currentHashType}]";
                if (skippedCount > 0) MessageBox.Show("No valid files added due to path errors.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            progressBarTotal.Maximum = items.Count;
            progressBarTotal.Value = 0;
            progressBarCurrent.Value = 0;

            Stopwatch sw = new Stopwatch();
            bool showTime = _menuOptionsTime?.Checked ?? false;
            int completed = 0;
            int okCount = 0;
            int errorCount = 0;

            var progress = new Progress<double>(percent =>
            {
                int val = (int)percent;
                if (val != progressBarCurrent.Value)
                    progressBarCurrent.Value = Math.Min(100, Math.Max(0, val));
            });

            foreach (var item in items)
            {
                item.EnsureVisible();
                if (item.Tag is FileItemData data)
                {
                    progressBarCurrent.Value = 0;
                    sw.Restart();

                    string hash = await HashHelper.ComputeHashAsync(data.FullPath, _currentHashType, progress);
                    sw.Stop();

                    item.SubItems[1].Text = hash;
                    if (showTime) item.SubItems[3].Text = $"{sw.ElapsedMilliseconds} ms";

                    if (hash == "FILE_NOT_FOUND" || hash == "ACCESS_DENIED" || hash == "ERROR")
                    {
                        item.SubItems[2].Text = hash;
                        item.ForeColor = Color.Red;
                        item.BackColor = Color.FromArgb(255, 230, 230);
                        errorCount++;
                    }
                    else
                    {
                        item.SubItems[2].Text = "Done";
                        item.ForeColor = SystemColors.ControlText;
                        item.BackColor = SystemColors.Window;
                        okCount++;
                    }

                    completed++;
                    UpdateStats(completed, items.Count, okCount, 0, errorCount);
                }
                progressBarTotal.Value++;
            }
            progressBarCurrent.Value = 0;
            _isProcessing = false;
            this.Text = $"SharpSFV - Creation Complete [{_currentHashType}]";
        }

        private async Task RunVerification(string checkFilePath)
        {
            _isProcessing = true;
            SetupUIForMode("Verification");

            string baseFolder = Path.GetDirectoryName(checkFilePath) ?? "";
            string ext = Path.GetExtension(checkFilePath).ToLowerInvariant();

            HashType verificationAlgo = _currentHashType;
            if (ext == ".sfv") verificationAlgo = HashType.Crc32;
            else if (ext == ".md5") verificationAlgo = HashType.MD5;
            else if (ext == ".sha1") verificationAlgo = HashType.SHA1;
            else if (ext == ".sha256") verificationAlgo = HashType.SHA256;
            else if (ext == ".xxh3") verificationAlgo = HashType.XxHash3;

            var parsedLines = new List<(string ExpectedHash, string Filename)>();
            try
            {
                foreach (var line in await File.ReadAllLinesAsync(checkFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";")) continue;

                    string pattern = @"^([0-9a-fA-F]+)\s+\*?(.*)$";
                    Match match = Regex.Match(line, pattern);

                    if (match.Success)
                    {
                        string expectedHash = match.Groups[1].Value;
                        string filename = match.Groups[2].Value.Trim();
                        if (!string.IsNullOrEmpty(expectedHash) && !string.IsNullOrEmpty(filename))
                            parsedLines.Add((expectedHash, filename));
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("Error parsing hash file: " + ex.Message); _isProcessing = false; return; }

            List<ListViewItem> items = new List<ListViewItem>();
            int originalIndex = 0;
            int okCount = 0, badCount = 0, missingCount = 0;

            foreach (var entry in parsedLines)
            {
                string fullPath = Path.GetFullPath(Path.Combine(baseFolder, entry.Filename));
                bool exists = File.Exists(fullPath);

                if (fullPath.Length > MaxWindowsPathLength) exists = false;

                var item = new ListViewItem(entry.Filename);
                item.SubItems.Add("Waiting...");
                item.SubItems.Add(exists ? "Pending" : "MISSING");
                item.SubItems.Add(entry.ExpectedHash);
                item.SubItems.Add("");

                if (!exists)
                {
                    item.ForeColor = Color.Red;
                    item.Font = new Font(lvFiles.Font, FontStyle.Strikeout);
                    missingCount++;
                }

                item.Tag = new FileItemData
                {
                    FullPath = fullPath,
                    RelativePath = entry.Filename,
                    BaseDirectory = baseFolder,
                    ExpectedHash = entry.ExpectedHash,
                    Index = originalIndex++
                };
                items.Add(item);
                lvFiles.Items.Add(item);
            }

            UpdateStats(0, items.Count, 0, 0, missingCount);

            progressBarTotal.Maximum = items.Count;
            progressBarTotal.Value = 0;
            progressBarCurrent.Value = 0;

            Stopwatch sw = new Stopwatch();
            bool showTime = _menuOptionsTime?.Checked ?? false;
            int completed = 0;

            var progress = new Progress<double>(percent =>
            {
                int val = (int)percent;
                if (val != progressBarCurrent.Value)
                    progressBarCurrent.Value = Math.Min(100, Math.Max(0, val));
            });

            foreach (var item in items)
            {
                item.EnsureVisible();
                if (item.Tag is FileItemData data)
                {
                    if (item.SubItems[2].Text == "MISSING")
                    {
                        completed++;
                        progressBarTotal.Value++;
                        UpdateStats(completed, items.Count, okCount, badCount, missingCount);
                        continue;
                    }

                    progressBarCurrent.Value = 0;
                    sw.Restart();
                    string calculatedHash = await HashHelper.ComputeHashAsync(data.FullPath, verificationAlgo, progress);
                    sw.Stop();

                    item.SubItems[1].Text = calculatedHash;
                    if (showTime) item.SubItems[4].Text = $"{sw.ElapsedMilliseconds} ms";

                    if (calculatedHash == "FILE_NOT_FOUND" || calculatedHash == "ACCESS_DENIED" || calculatedHash == "ERROR")
                    {
                        item.SubItems[2].Text = calculatedHash;
                        item.ForeColor = Color.Red;
                        item.BackColor = Color.FromArgb(255, 230, 230);
                        badCount++;
                    }
                    else if (calculatedHash.Equals(data.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        item.SubItems[2].Text = "OK";
                        item.ForeColor = Color.DarkGreen;
                        item.BackColor = Color.FromArgb(230, 255, 230);
                        okCount++;
                    }
                    else
                    {
                        item.SubItems[2].Text = "BAD";
                        item.ForeColor = Color.Red;
                        item.BackColor = Color.FromArgb(255, 230, 230);
                        badCount++;
                    }

                    completed++;
                    UpdateStats(completed, items.Count, okCount, badCount, missingCount);
                }
                progressBarTotal.Value++;
            }
            progressBarCurrent.Value = 0;
            _isProcessing = false;
            string algoName = Enum.GetName(typeof(HashType), verificationAlgo) ?? "Hash";
            this.Text = (badCount == 0 && missingCount == 0)
                ? $"SharpSFV [{algoName}] - All Files OK"
                : $"SharpSFV [{algoName}] - {badCount} Bad, {missingCount} Missing";
        }

        private void SetupUIForMode(string mode)
        {
            lvFiles.BeginUpdate();
            lvFiles.Items.Clear();
            lvFiles.Columns.Clear();

            lvFiles.Columns.Add("File Name", 300);
            lvFiles.Columns.Add("Hash", 220);
            lvFiles.Columns.Add("Status", 100);

            if (mode == "Verification") lvFiles.Columns.Add("Expected Hash", 220);
            if (_menuOptionsTime != null && _menuOptionsTime.Checked) lvFiles.Columns.Add("Time", 80);

            lvFiles.EndUpdate();
            this.Text = (mode == "Verification") ? "SharpSFV - Verify" : $"SharpSFV - Create [{_currentHashType}]";
        }

        private void PerformBatchExport()
        {
            var badItems = lvFiles.Items.Cast<ListViewItem>().Where(i => i.SubItems[2].Text == "BAD" && i.Tag is FileItemData).ToList();
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
                            foreach (var item in badItems) if (item.Tag is FileItemData data) sw.WriteLine($"del \"{data.FullPath}\"");
                            sw.WriteLine("pause");
                        }
                        MessageBox.Show("Script saved.", "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving script: " + ex.Message, "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            }
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
            if (lvFiles.Items.Count == 0)
            {
                MessageBox.Show("No files in the list to save.", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string defaultFileName = "checksums";
            string initialDirectory = "";
            FileItemData? firstData = lvFiles.Items.Cast<ListViewItem>().FirstOrDefault()?.Tag as FileItemData;

            if (firstData != null && !string.IsNullOrEmpty(firstData.BaseDirectory))
            {
                defaultFileName = Path.GetFileName(firstData.BaseDirectory);
                initialDirectory = firstData.BaseDirectory;
            }
            else if (firstData != null && !string.IsNullOrEmpty(firstData.FullPath))
            {
                initialDirectory = Path.GetDirectoryName(firstData.FullPath) ?? "";
            }

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "xxHash3 File (*.xxh3)|*.xxh3|SFV File (*.sfv)|*.sfv|MD5 File (*.md5)|*.md5|SHA1 File (*.sha1)|*.sha1|SHA256 File (*.sha256)|*.sha256|Text File (*.txt)|*.txt",
                FileName = defaultFileName + ".xxh3",
                InitialDirectory = initialDirectory
            })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine($"; Generated by SharpSFV (Signature: {_settings.CustomSignature})");
                            foreach (ListViewItem item in lvFiles.Items)
                            {
                                string hash = item.SubItems.Count > 1 ? item.SubItems[1].Text : "";
                                if (item.Tag is FileItemData data && !hash.Contains("...") && !hash.Equals("Pending") && !string.IsNullOrEmpty(hash))
                                {
                                    string pathToWrite = (_menuOptionsAbsolutePaths != null && _menuOptionsAbsolutePaths.Checked)
                                        ? data.FullPath
                                        : data.RelativePath;

                                    if (string.IsNullOrEmpty(pathToWrite)) pathToWrite = data.FullPath;

                                    sw.WriteLine($"{hash} *{pathToWrite}");
                                }
                            }
                        }
                        MessageBox.Show("Checksum file saved successfully.", "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex) { MessageBox.Show("Error saving checksum file: " + ex.Message, "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            }
        }

        private void ToggleTimeColumn()
        {
            _settings.ShowTimeTab = _menuOptionsTime?.Checked ?? false;
            bool exists = lvFiles.Columns.Cast<ColumnHeader>().Any(ch => ch.Text == "Time");

            if (_settings.ShowTimeTab && !exists) lvFiles.Columns.Add("Time", 80);
            else if (!_settings.ShowTimeTab && exists)
            {
                ColumnHeader? timeColumn = lvFiles.Columns.Cast<ColumnHeader>().FirstOrDefault(ch => ch.Text == "Time");
                if (timeColumn != null) lvFiles.Columns.Remove(timeColumn);
            }
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

            Label lbl = new Label();
            lbl.Text = "SharpSFV v1.0\n\nHigh Performance Hasher.\nInspired by QuickSFV.";
            lbl.AutoSize = false;
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.Dock = DockStyle.Top;
            lbl.Height = 80;
            lbl.Padding = new Padding(0, 20, 0, 0);

            LinkLabel link = new LinkLabel();
            link.Text = "Visit GitHub Repository";
            link.TextAlign = ContentAlignment.MiddleCenter;
            link.Dock = DockStyle.Top;
            link.LinkClicked += (s, e) => {
                try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/your-username/SharpSFV", UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show($"Could not open link: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };

            Button btnOk = new Button();
            btnOk.Text = "OK";
            btnOk.DialogResult = DialogResult.OK;
            btnOk.Location = new Point((credits.ClientSize.Width - 80) / 2, 120);
            btnOk.Size = new Size(80, 30);

            credits.Controls.Add(btnOk);
            credits.Controls.Add(link);
            credits.Controls.Add(lbl);
            credits.ShowDialog();
        }
    }

    // New Helper class for Rename Input
    public static class SimpleInputDialog
    {
        public static string ShowDialog(string caption, string text, string defaultValue = "")
        {
            Form prompt = new Form()
            {
                Width = 400,
                Height = 160,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = caption,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };
            Label textLabel = new Label() { Left = 20, Top = 20, Text = text, AutoSize = true };
            TextBox textBox = new TextBox() { Left = 20, Top = 45, Width = 340, Text = defaultValue };
            Button confirmation = new Button() { Text = "OK", Left = 260, Width = 100, Top = 80, DialogResult = DialogResult.OK };

            prompt.Controls.Add(textLabel);
            prompt.Controls.Add(textBox);
            prompt.Controls.Add(confirmation);
            prompt.AcceptButton = confirmation;

            return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "";
        }
    }
}