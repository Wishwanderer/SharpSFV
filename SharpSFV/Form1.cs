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
        #region Fields & Constants

        private AppSettings _settings;
        private ListViewColumnSorter _lvwColumnSorter;

        // State Flags
        private bool _isProcessing = false;
        private HashType _currentHashType = HashType.XxHash3;

        // UI Components
        private MenuStrip? _menuStrip;
        private ContextMenuStrip? _ctxMenu;
        private Panel? _statsPanel;
        private Panel? _filterPanel;
        private ProgressBar progressBarCurrent = new ProgressBar(); // Per-file progress
        private Label? _lblProgress;
        private Label? _lblStatsRow;

        // Menu Items References
        private ToolStripMenuItem? _menuOptionsTime;
        private ToolStripMenuItem? _menuOptionsAbsolutePaths;
        private ToolStripMenuItem? _menuOptionsFilter;
        private Dictionary<HashType, ToolStripMenuItem> _algoMenuItems = new Dictionary<HashType, ToolStripMenuItem>();

        // Filtering & Data
        private TextBox? _txtFilter;
        private ComboBox? _cmbStatusFilter;
        private List<ListViewItem> _allItems = new List<ListViewItem>(); // Master list used for filtering

        // File extensions that trigger Verification mode instead of Creation mode
        private readonly HashSet<string> _verificationExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xxh3", ".xxh", ".sum", ".md5", ".sfv", ".sha1", ".sha256", ".txt"
        };

        // Safety limit to prevent crashes on legacy Windows APIs or specific tools
        private const int MaxWindowsPathLength = 256;

        #endregion

        #region Constructor & Initialization

        public Form1()
        {
            InitializeComponent();

            // Initialize Settings using the Executable Path to ensure INI is found correctly
            _settings = new AppSettings(Application.ExecutablePath);
            _lvwColumnSorter = new ListViewColumnSorter();

            _settings.Load();

            // Setup ListView Sorting
            this.lvFiles.ListViewItemSorter = _lvwColumnSorter;
            this.lvFiles.ColumnClick += LvFiles_ColumnClick;
            this.FormClosing += Form1_FormClosing;

            // Initialize UI Layout
            SetupCustomMenu();
            SetupContextMenu();
            SetupStatsPanel();
            SetupFilterPanel();
            SetupDragDrop();

            // Apply loaded settings (Window size, algorithm, etc.)
            ApplySettings();
            SetupLayout(); // Finalize layout dockings

            this.Text = "SharpSFV";
        }

        private async void Form1_Load(object? sender, EventArgs e)
        {
            // Handle Command Line Arguments (e.g. "Open With" from Explorer)
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) await HandleDroppedPaths(args.Skip(1).ToArray());
        }

        #endregion

        #region Settings Management

        /// <summary>
        /// Applies the configuration loaded from AppSettings to the UI controls.
        /// </summary>
        private void ApplySettings()
        {
            // Restore Window Size/Pos
            if (_settings.WindowSize.Width > 100) this.Size = _settings.WindowSize;

            if (_settings.HasCustomLocation)
            {
                this.StartPosition = FormStartPosition.Manual;
                this.Location = _settings.WindowLocation;

                // Ensure window is actually visible on current screens
                bool isOnScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(this.Bounds));
                if (!isOnScreen) this.StartPosition = FormStartPosition.CenterScreen;
            }

            // Sync Menu Checkboxes
            if (_menuOptionsTime != null) _menuOptionsTime.Checked = _settings.ShowTimeTab;
            if (_menuOptionsAbsolutePaths != null) _menuOptionsAbsolutePaths.Checked = _settings.UseAbsolutePaths;
            if (_menuOptionsFilter != null) _menuOptionsFilter.Checked = _settings.ShowFilterPanel;

            // Set Visibility
            if (_filterPanel != null) _filterPanel.Visible = _settings.ShowFilterPanel;

            ToggleTimeColumn();
            SetAlgorithm(_settings.DefaultAlgo);
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Sync settings back to object before saving to disk
            bool timeEnabled = _menuOptionsTime?.Checked ?? false;
            _settings.UseAbsolutePaths = _menuOptionsAbsolutePaths?.Checked ?? false;
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;

            _settings.Save(this, timeEnabled, _currentHashType);
        }

        #endregion

        #region UI Layout Setup

        /// <summary>
        /// Arranges the panels, progress bars, and listview in the form.
        /// Called during init and updates.
        /// </summary>
        private void SetupLayout()
        {
            this.Controls.Clear();

            // Bottom Panel: Contains Dual Progress Bars
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

            // Center: File List
            lvFiles.Dock = DockStyle.Fill;
            this.Controls.Add(lvFiles);

            // Top Panels (Added in reverse order of DockStyle.Top visual appearance)
            if (_filterPanel != null) this.Controls.Add(_filterPanel); // Bottom-most top panel
            if (_statsPanel != null) this.Controls.Add(_statsPanel);   // Middle top panel
            if (_menuStrip != null) this.Controls.Add(_menuStrip);     // Very top
        }

        private void SetupStatsPanel()
        {
            _statsPanel = new Panel { Height = 50, Dock = DockStyle.Top, BackColor = SystemColors.ControlLight, Padding = new Padding(10, 5, 10, 5) };
            _lblProgress = new Label { Text = "Ready", AutoSize = true, Font = new Font(this.Font, FontStyle.Bold), Location = new Point(10, 8) };
            _lblStatsRow = new Label { Text = "OK: 0     BAD: 0     MISSING: 0", AutoSize = true, Location = new Point(10, 28) };
            _statsPanel.Controls.Add(_lblProgress);
            _statsPanel.Controls.Add(_lblStatsRow);
        }

        private void SetupFilterPanel()
        {
            _filterPanel = new Panel
            {
                Height = 35,
                Dock = DockStyle.Top,
                BackColor = SystemColors.Control,
                Padding = new Padding(5),
                Visible = false // Hidden by default unless enabled in settings
            };

            Label lblSearch = new Label { Text = "Search:", AutoSize = true, Location = new Point(10, 8) };
            _txtFilter = new TextBox { Width = 200, Location = new Point(60, 5) };
            _txtFilter.TextChanged += (s, e) => ApplyFilter();

            Label lblStatus = new Label { Text = "Status:", AutoSize = true, Location = new Point(280, 8) };
            _cmbStatusFilter = new ComboBox { Width = 100, Location = new Point(330, 5), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbStatusFilter.Items.AddRange(new object[] { "All", "Pending", "OK", "BAD", "MISSING", "Waiting..." });
            _cmbStatusFilter.SelectedIndex = 0;
            _cmbStatusFilter.SelectedIndexChanged += (s, e) => ApplyFilter();

            _filterPanel.Controls.AddRange(new Control[] { lblSearch, _txtFilter, lblStatus, _cmbStatusFilter });
        }

        private void SetupCustomMenu()
        {
            _menuStrip = new MenuStrip { Dock = DockStyle.Top };

            // File Menu
            var menuFile = new ToolStripMenuItem("File");
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Open...", null, (s, e) => PerformOpenAction()) { ShortcutKeys = Keys.Control | Keys.O });
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Save As...", null, (s, e) => PerformSaveAction()) { ShortcutKeys = Keys.Control | Keys.S });
            menuFile.DropDownItems.Add(new ToolStripSeparator());
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit()) { ShortcutKeys = Keys.Alt | Keys.F4 });

            // Edit Menu (Clipboard)
            var menuEdit = new ToolStripMenuItem("Edit");
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Copy Details", null, (s, e) => PerformCopyAction()) { ShortcutKeys = Keys.Control | Keys.C });
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Paste Paths", null, (s, e) => PerformPasteAction()) { ShortcutKeys = Keys.Control | Keys.V });
            menuEdit.DropDownItems.Add(new ToolStripSeparator());
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Select All", null, (s, e) => PerformSelectAllAction()) { ShortcutKeys = Keys.Control | Keys.A });

            // Options Menu
            var menuOptions = new ToolStripMenuItem("Options");
            _menuOptionsTime = new ToolStripMenuItem("Enable Time Elapsed Tab", null, (s, e) => ToggleTimeColumn()) { CheckOnClick = true };
            _menuOptionsAbsolutePaths = new ToolStripMenuItem("Always Save Absolute Paths", null, (s, e) => {
                _settings.UseAbsolutePaths = !_settings.UseAbsolutePaths;
            })
            { CheckOnClick = true };
            _menuOptionsFilter = new ToolStripMenuItem("Show Search/Filter Bar", null, (s, e) => ToggleFilterPanel()) { CheckOnClick = true };

            var menuAlgo = new ToolStripMenuItem("Default Hashing Algorithm");
            AddAlgoMenuItem(menuAlgo, "xxHash-3 (128-bit)", HashType.XxHash3);
            AddAlgoMenuItem(menuAlgo, "CRC-32 (SFV)", HashType.Crc32);
            AddAlgoMenuItem(menuAlgo, "MD5", HashType.MD5);
            AddAlgoMenuItem(menuAlgo, "SHA-1", HashType.SHA1);
            AddAlgoMenuItem(menuAlgo, "SHA-256", HashType.SHA256);

            menuOptions.DropDownItems.AddRange(new ToolStripItem[] {
                _menuOptionsTime,
                _menuOptionsAbsolutePaths,
                _menuOptionsFilter,
                menuAlgo,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Generate 'Delete BAD Files' Script", null, (s, e) => PerformBatchExport())
            });

            // Help Menu
            var menuHelp = new ToolStripMenuItem("Help");
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

            _ctxMenu.Items.AddRange(new ToolStripItem[] {
                itemOpen, new ToolStripSeparator(),
                itemCopyPath, itemCopyHash, new ToolStripSeparator(),
                itemRename, itemDelete, itemRemove
            });

            // Dynamically enable/disable context options based on selection
            _ctxMenu.Opening += (s, e) =>
            {
                if (lvFiles.SelectedItems.Count == 0) { e.Cancel = true; return; }

                bool singleSel = lvFiles.SelectedItems.Count == 1;
                bool fileExists = false;
                if (singleSel && lvFiles.SelectedItems[0].Tag is FileItemData data)
                {
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

        private void SetupUIForMode(string mode)
        {
            lvFiles.BeginUpdate();
            lvFiles.Items.Clear();
            lvFiles.Columns.Clear();

            // Reset Sorting
            _lvwColumnSorter.SortColumn = -1;
            _lvwColumnSorter.Order = SortOrder.None;

            lvFiles.Columns.Add("File Name", 300);
            lvFiles.Columns.Add("Hash", 220);
            lvFiles.Columns.Add("Status", 100);

            if (mode == "Verification") lvFiles.Columns.Add("Expected Hash", 220);
            if (_menuOptionsTime != null && _menuOptionsTime.Checked) lvFiles.Columns.Add("Time", 80);

            lvFiles.EndUpdate();
            this.Text = (mode == "Verification") ? "SharpSFV - Verify" : $"SharpSFV - Create [{_currentHashType}]";
        }

        #endregion

        #region Core Logic (Path Handling, Hashing, Verification)

        /// <summary>
        /// Analyzes dropped paths to determine if we should verify a file or create new hashes.
        /// Handles recursive folder scanning.
        /// </summary>
        private async Task HandleDroppedPaths(string[] paths)
        {
            if (paths.Length == 0) return;

            bool containsFolder = paths.Any(p => Directory.Exists(p));

            // Reset backend sorting and visuals
            _lvwColumnSorter.SortColumn = -1;
            _lvwColumnSorter.Order = SortOrder.None;
            this.lvFiles.ListViewItemSorter = null;
            UpdateSortVisuals(-1, SortOrder.None);

            // CASE 1: Verification (Single file dropped with known extension)
            if (!containsFolder && paths.Length == 1 && _verificationExtensions.Contains(Path.GetExtension(paths[0])) && File.Exists(paths[0]))
            {
                await RunVerification(paths[0]);
            }
            // CASE 2: Creation
            else
            {
                string baseDirectory = "";

                // LOGIC CHANGE: Better Base Directory Detection
                if (paths.Length == 1 && Directory.Exists(paths[0]))
                {
                    // If exactly one folder is dropped, make THAT folder the base.
                    // Result: File paths inside will be relative to that folder.
                    baseDirectory = paths[0];
                }
                else
                {
                    // If multiple items (files or folders) are dropped, find the common PARENT.
                    // We get the directory name of every item dropped.
                    var parentDirs = paths.Select(p =>
                    {
                        // If p is a file, GetDirectoryName returns its folder.
                        // If p is a folder, GetDirectoryName returns its parent.
                        // If p is a drive root (C:\), it returns null, so handle that.
                        return Path.GetDirectoryName(p) ?? p;
                    }).ToList();

                    baseDirectory = FindCommonBasePath(parentDirs);
                }

                // Gather all files recursively
                List<string> allFilesToHash = new List<string>();
                foreach (string path in paths)
                {
                    if (File.Exists(path)) allFilesToHash.Add(path);
                    else if (Directory.Exists(path))
                    {
                        try { allFilesToHash.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)); }
                        catch (Exception ex) { MessageBox.Show($"Error accessing folder '{path}': {ex.Message}", "Access Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    }
                }
                await RunHashCreation(allFilesToHash.ToArray(), baseDirectory);
            }

            this.lvFiles.ListViewItemSorter = _lvwColumnSorter;
        }

        private async Task RunHashCreation(string[] filePaths, string baseDirectory)
        {
            _isProcessing = true;
            SetupUIForMode("Creation");
            UpdateStats(0, filePaths.Length, 0, 0, 0);

            _allItems.Clear(); // Clear Master List used for filtering
            List<ListViewItem> items = new List<ListViewItem>();
            int originalIndex = 0;
            int skippedCount = 0;

            // Pre-process files into ListViewItems
            foreach (var fullPath in filePaths)
            {
                if (Directory.Exists(fullPath)) continue;

                if (fullPath.Length > MaxWindowsPathLength)
                {
                    MessageBox.Show($"Skipping file due to excessively long path: {fullPath}", "Path Too Long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    skippedCount++;
                    continue;
                }

                string fileName = Path.GetFileName(fullPath);
                // Validate filename/path characters
                if (fileName.Any(c => Path.GetInvalidFileNameChars().Contains(c)) || fullPath.Any(c => Path.GetInvalidPathChars().Contains(c)))
                {
                    MessageBox.Show($"Skipping file due to invalid characters: {fileName}", "Invalid Characters", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    skippedCount++;
                    continue;
                }

                // Calculate relative path
                string relativePath = "";
                if (!string.IsNullOrEmpty(baseDirectory))
                {
                    try { relativePath = Path.GetRelativePath(baseDirectory, fullPath); }
                    catch { relativePath = fullPath; } // Fallback
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
                _allItems.Add(item); // Add to Master
                lvFiles.Items.Add(item);
            }

            if (items.Count == 0)
            {
                _isProcessing = false;
                UpdateStats(0, 0, 0, 0, 0);
                this.Text = $"SharpSFV - Create [{_currentHashType}]";
                if (skippedCount > 0) MessageBox.Show("No valid files added.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            // Per-file progress reporter
            var progress = new Progress<double>(percent => {
                int val = (int)percent;
                if (val != progressBarCurrent.Value) progressBarCurrent.Value = Math.Min(100, Math.Max(0, val));
            });

            // --- MAIN PROCESSING LOOP ---
            foreach (var item in items)
            {
                // Scroll list to item
                if (lvFiles.Items.Contains(item)) item.EnsureVisible();

                if (item.Tag is FileItemData data)
                {
                    progressBarCurrent.Value = 0;
                    sw.Restart();
                    // Compute Hash
                    string hash = await HashHelper.ComputeHashAsync(data.FullPath, _currentHashType, progress);
                    sw.Stop();

                    item.SubItems[1].Text = hash;
                    if (showTime) item.SubItems[3].Text = $"{sw.ElapsedMilliseconds} ms";

                    // Handle Results
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

            // Auto-detect algorithm based on extension
            HashType verificationAlgo = _currentHashType;
            if (ext == ".sfv") verificationAlgo = HashType.Crc32;
            else if (ext == ".md5") verificationAlgo = HashType.MD5;
            else if (ext == ".sha1") verificationAlgo = HashType.SHA1;
            else if (ext == ".sha256") verificationAlgo = HashType.SHA256;
            else if (ext == ".xxh3") verificationAlgo = HashType.XxHash3;

            // Parse the hash file
            var parsedLines = new List<(string ExpectedHash, string Filename)>();
            try
            {
                foreach (var line in await File.ReadAllLinesAsync(checkFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";")) continue;

                    // Regex to parse "Hash *Filename" or "Hash Filename"
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
            catch (Exception ex) { MessageBox.Show("Error parsing: " + ex.Message); _isProcessing = false; return; }

            _allItems.Clear();
            List<ListViewItem> items = new List<ListViewItem>();
            int originalIndex = 0;
            int okCount = 0, badCount = 0, missingCount = 0;

            // Populate list
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
                _allItems.Add(item);
                lvFiles.Items.Add(item);
            }

            UpdateStats(0, items.Count, 0, 0, missingCount);

            progressBarTotal.Maximum = items.Count;
            progressBarTotal.Value = 0;
            progressBarCurrent.Value = 0;

            Stopwatch sw = new Stopwatch();
            bool showTime = _menuOptionsTime?.Checked ?? false;
            int completed = 0;

            var progress = new Progress<double>(percent => {
                int val = (int)percent;
                if (val != progressBarCurrent.Value) progressBarCurrent.Value = Math.Min(100, Math.Max(0, val));
            });

            // --- MAIN VERIFICATION LOOP ---
            foreach (var item in items)
            {
                if (lvFiles.Items.Contains(item)) item.EnsureVisible();

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

                    // Comparison Logic
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

        #endregion

        #region User Actions (Open, Save, Copy/Paste)

        private void PerformSelectAllAction()
        {
            lvFiles.BeginUpdate();
            foreach (ListViewItem item in lvFiles.Items) item.Selected = true;
            lvFiles.EndUpdate();
        }

        private void PerformCopyAction()
        {
            if (lvFiles.SelectedItems.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
                // Tab-separated for easy Excel pasting
                string line = $"{item.Text}\t{item.SubItems[1].Text}\t{item.SubItems[2].Text}";
                sb.AppendLine(line);
            }
            try { Clipboard.SetText(sb.ToString()); }
            catch (Exception ex) { MessageBox.Show($"Clipboard Error: {ex.Message}"); }
        }

        private async void PerformPasteAction()
        {
            if (!Clipboard.ContainsText()) return;
            string clipboardText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipboardText)) return;

            string[] lines = clipboardText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            List<string> validPaths = new List<string>();

            foreach (string line in lines)
            {
                // Clean quotes and whitespace
                string cleanPath = line.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(cleanPath)) continue;
                try { if (File.Exists(cleanPath) || Directory.Exists(cleanPath)) validPaths.Add(cleanPath); } catch { }
            }

            if (validPaths.Count > 0) await HandleDroppedPaths(validPaths.ToArray());
            else MessageBox.Show("No valid paths found in clipboard.", "Paste", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void PerformBatchExport()
        {
            // Exports BAD files from the Master List (_allItems), ensuring even filtered items are caught
            var badItems = _allItems.Where(i => (i.SubItems[2].Text == "BAD" || i.SubItems[2].Text.Contains("ERROR")) && i.Tag is FileItemData).ToList();
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
                    catch (Exception ex) { MessageBox.Show("Error saving script: " + ex.Message); }
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
            if (_allItems.Count == 0)
            {
                MessageBox.Show("No files to save.", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string defaultFileName = "checksums";
            string initialDirectory = "";
            FileItemData? firstData = _allItems.FirstOrDefault()?.Tag as FileItemData;

            // Set initial directory for the Save Dialog based on the first file's context
            if (firstData != null && !string.IsNullOrEmpty(firstData.BaseDirectory))
            {
                defaultFileName = Path.GetFileName(firstData.BaseDirectory); // E.g., folder name
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
                        // Get the directory where the USER chose to save the hash file
                        string saveDirectory = Path.GetDirectoryName(sfd.FileName) ?? initialDirectory;

                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine($"; Generated by SharpSFV (Signature: {_settings.CustomSignature})");

                            foreach (ListViewItem item in _allItems)
                            {
                                string hash = item.SubItems.Count > 1 ? item.SubItems[1].Text : "";

                                if (item.Tag is FileItemData data && !hash.Contains("...") && !hash.Equals("Pending") && !string.IsNullOrEmpty(hash))
                                {
                                    string pathToWrite;

                                    // LOGIC CHANGE: Calculate path relative to the SAVE LOCATION, not the drop location
                                    if (_menuOptionsAbsolutePaths != null && _menuOptionsAbsolutePaths.Checked)
                                    {
                                        pathToWrite = data.FullPath;
                                    }
                                    else
                                    {
                                        // Dynamically calculate relative path from the save file's location to the target file
                                        // This handles "..\" automatically if needed, or removes it if not.
                                        try
                                        {
                                            pathToWrite = Path.GetRelativePath(saveDirectory, data.FullPath);
                                        }
                                        catch
                                        {
                                            // Fallback if paths are on different drives
                                            pathToWrite = data.FullPath;
                                        }
                                    }

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

        #endregion

        #region Event Handlers & Context Menus

        private void SetupDragDrop()
        {
            this.AllowDrop = true;
            this.DragEnter += (s, e) => { if (e.Data!.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            this.DragDrop += Form1_DragDrop;
        }

        private async void Form1_DragDrop(object? sender, DragEventArgs e)
        {
            if (_isProcessing) return;
            string[]? paths = (string[]?)e.Data!.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0) await HandleDroppedPaths(paths);
        }

        private void CtxOpenFolder_Click(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count != 1) return;
            if (lvFiles.SelectedItems[0].Tag is FileItemData data)
            {
                try
                {
                    if (File.Exists(data.FullPath)) Process.Start("explorer.exe", $"/select,\"{data.FullPath}\"");
                    else if (Directory.Exists(Path.GetDirectoryName(data.FullPath))) Process.Start("explorer.exe", Path.GetDirectoryName(data.FullPath)!);
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
                string hash = lvFiles.SelectedItems[0].SubItems[1].Text;
                if (!string.IsNullOrEmpty(hash) && !hash.Contains("...")) Clipboard.SetText(hash);
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
                        data.FullPath = newPath;
                        if (!string.IsNullOrEmpty(data.RelativePath))
                            data.RelativePath = Path.Combine(Path.GetDirectoryName(data.RelativePath) ?? "", newName);
                        item.Text = newName;
                    }
                    catch (Exception ex) { MessageBox.Show("Error renaming file: " + ex.Message); }
                }
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
                        _allItems.Remove(item);
                    }
                    catch (Exception ex) { MessageBox.Show("Error deleting file: " + ex.Message); }
                }
            }
        }

        private void CtxRemoveList_Click(object? sender, EventArgs e)
        {
            var selected = lvFiles.SelectedItems.Cast<ListViewItem>().ToList();
            foreach (ListViewItem item in selected)
            {
                lvFiles.Items.Remove(item);
                _allItems.Remove(item);
            }
        }

        #endregion

        #region Helpers (Filtering, Sorting, Dialogs)

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
            else if (!_settings.ShowTimeTab && exists)
            {
                ColumnHeader? timeColumn = lvFiles.Columns.Cast<ColumnHeader>().FirstOrDefault(ch => ch.Text == "Time");
                if (timeColumn != null) lvFiles.Columns.Remove(timeColumn);
            }
        }

        private void ToggleFilterPanel()
        {
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;
            if (_filterPanel != null) _filterPanel.Visible = _settings.ShowFilterPanel;
        }

        /// <summary>
        /// Adds visual arrows (▲/▼) to the ListView column headers.
        /// </summary>
        private void UpdateSortVisuals(int column, SortOrder order)
        {
            const string AscArrow = " ▲";
            const string DescArrow = " ▼";
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Text.EndsWith(AscArrow)) ch.Text = ch.Text.Substring(0, ch.Text.Length - AscArrow.Length);
                else if (ch.Text.EndsWith(DescArrow)) ch.Text = ch.Text.Substring(0, ch.Text.Length - DescArrow.Length);
                if (ch.Index == column)
                {
                    if (order == SortOrder.Ascending) ch.Text += AscArrow;
                    else if (order == SortOrder.Descending) ch.Text += DescArrow;
                }
            }
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
                _lvwColumnSorter.Order = (_lvwColumnSorter.Order == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;
            }
            this.lvFiles.Sort();
            UpdateSortVisuals(e.Column, _lvwColumnSorter.Order);
        }

        private void ApplyFilter()
        {
            if (_txtFilter == null || _cmbStatusFilter == null) return;
            string searchText = _txtFilter.Text.Trim();
            string statusFilter = _cmbStatusFilter.SelectedItem?.ToString() ?? "All";

            lvFiles.BeginUpdate();
            lvFiles.Items.Clear();
            foreach (var item in _allItems)
            {
                bool matchName = string.IsNullOrEmpty(searchText) || item.Text.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                string itemStatus = item.SubItems[2].Text;
                bool matchStatus = statusFilter == "All";
                if (!matchStatus)
                {
                    if (statusFilter == "BAD") matchStatus = itemStatus == "BAD" || itemStatus.Contains("ERROR") || itemStatus.Contains("NOT_FOUND");
                    else matchStatus = itemStatus.Equals(statusFilter, StringComparison.OrdinalIgnoreCase);
                }
                if (matchName && matchStatus) lvFiles.Items.Add(item);
            }
            lvFiles.EndUpdate();
        }

        /// <summary>
        /// Attempts to find the longest common path prefix for a list of directories.
        /// </summary>
        private string FindCommonBasePath(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return "";
            if (paths.Count == 1) return paths[0];

            // Split the first path into parts
            string[] shortestPathParts = paths.OrderBy(p => p.Length).First().Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string commonPath = "";

            // If on Windows, we need to preserve the Drive Letter (e.g. "C:") which Split removes the separator from
            bool isRooted = Path.IsPathRooted(paths[0]);

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

                if (allMatch)
                {
                    // Reconstruct path
                    if (i == 0 && isRooted && currentSegment.Contains(":"))
                    {
                        commonPath = currentSegment + Path.DirectorySeparatorChar; // "C:\"
                    }
                    else
                    {
                        commonPath = Path.Combine(commonPath, currentSegment);
                    }
                }
                else break;
            }

            return commonPath.TrimEnd(Path.DirectorySeparatorChar);
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

        private void ShowCredits()
        {
            Form credits = new Form();
            credits.Text = "About SharpSFV";
            credits.Size = new Size(300, 200);
            credits.StartPosition = FormStartPosition.CenterParent;
            credits.FormBorderStyle = FormBorderStyle.FixedDialog;
            credits.MaximizeBox = false;
            credits.MinimizeBox = false;

            Label lbl = new Label { Text = "SharpSFV v2.08\n\nHigh Performance Hasher.\nInspired by QuickSFV.", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 80, Padding = new Padding(0, 20, 0, 0) };
            LinkLabel link = new LinkLabel { Text = "Visit GitHub Repository", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top };
            link.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/Wishwanderer/SharpSFV", UseShellExecute = true }); } catch { } };
            Button btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point((credits.ClientSize.Width - 80) / 2, 120), Size = new Size(80, 30) };

            credits.Controls.Add(btnOk);
            credits.Controls.Add(link);
            credits.Controls.Add(lbl);
            credits.ShowDialog();
        }

        #endregion
    }

    /// <summary>
    /// A simple input dialog for text entry (e.g., renaming files).
    /// Used to avoid adding Visual Basic references.
    /// </summary>
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
