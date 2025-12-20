using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Media; // Required for SystemSounds

namespace SharpSFV
{
    public partial class Form1 : Form
    {
        #region Fields & Constants

        private AppSettings _settings;

        // --- Virtual Mode Collections ---
        private List<FileItemData> _allItems = new List<FileItemData>();
        private List<FileItemData> _displayList = new List<FileItemData>();
        private FileListSorter _listSorter = new FileListSorter();

        // Processing State
        private bool _isProcessing = false;
        private HashType _currentHashType = HashType.XxHash3;
        private CancellationTokenSource? _cts;

        // UI Update Throttling
        private object _uiLock = new object();
        private long _lastUiUpdateTick = 0;

        // UI Controls
        private MenuStrip? _menuStrip;
        private ContextMenuStrip? _ctxMenu;
        private Panel? _statsPanel;
        private Panel? _filterPanel;
        private Button? _btnStop;
        private Label? _lblProgress;
        private Label? _lblStatsRow;

        // Menu Items
        private ToolStripMenuItem? _menuOptionsTime;
        private ToolStripMenuItem? _menuOptionsAbsolutePaths;
        private ToolStripMenuItem? _menuOptionsFilter;
        private ToolStripMenuItem? _menuOptionsHDD;
        private Dictionary<HashType, ToolStripMenuItem> _algoMenuItems = new Dictionary<HashType, ToolStripMenuItem>();

        // Filter Controls
        private TextBox? _txtFilter;
        private ComboBox? _cmbStatusFilter;

        // Config Constants
        private const long LargeFileThreshold = 1024L * 1024 * 1024; // 1 GB

        // Colors for Status
        private readonly Color ColGreenBack = Color.FromArgb(220, 255, 220);
        private readonly Color ColGreenText = Color.DarkGreen;
        private readonly Color ColRedBack = Color.FromArgb(255, 220, 220);
        private readonly Color ColRedText = Color.Red;
        private readonly Color ColYellowBack = Color.FromArgb(255, 255, 224);
        private readonly Color ColYellowText = Color.DarkGoldenrod;

        private readonly HashSet<string> _verificationExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xxh3", ".xxh", ".sum", ".md5", ".sfv", ".sha1", ".sha256", ".txt"
        };

        #endregion

        #region Constructor & Initialization

        public Form1()
        {
            InitializeComponent();

            _settings = new AppSettings(Application.ExecutablePath);
            _settings.Load();

            // Enable Virtual Mode for high performance with 100k+ items
            this.lvFiles.VirtualMode = true;
            this.lvFiles.RetrieveVirtualItem += LvFiles_RetrieveVirtualItem;
            EnableDoubleBuffer(this.lvFiles);

            // Event Wiring
            this.lvFiles.ColumnClick += LvFiles_ColumnClick;
            this.FormClosing += Form1_FormClosing;
            this.Shown += Form1_Shown;

            // UI Construction
            SetupCustomMenu();
            SetupContextMenu();
            SetupStatsPanel();
            SetupFilterPanel();
            SetupDragDrop();

            ApplySettings();
            SetupLayout();

            this.Text = "SharpSFV";
        }

        // Reflection hack to enable double buffering on ListView to prevent flickering
        private void EnableDoubleBuffer(Control control)
        {
            typeof(Control).InvokeMember("DoubleBuffered",
                BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.NonPublic,
                null, control, new object[] { true });
        }

        private async void Form1_Shown(object? sender, EventArgs e)
        {
            // Delay slightly to let the window render before processing CLI args
            await Task.Delay(100);
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) await HandleDroppedPaths(args.Skip(1).ToArray());
        }

        #endregion

        #region Settings Management (Restored Methods)

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

            if (_menuOptionsTime != null) _menuOptionsTime.Checked = _settings.ShowTimeTab;
            if (_menuOptionsAbsolutePaths != null) _menuOptionsAbsolutePaths.Checked = _settings.UseAbsolutePaths;
            if (_menuOptionsFilter != null) _menuOptionsFilter.Checked = _settings.ShowFilterPanel;
            if (_menuOptionsHDD != null) _menuOptionsHDD.Checked = _settings.OptimizeForHDD;

            if (_filterPanel != null) _filterPanel.Visible = _settings.ShowFilterPanel;

            ToggleTimeColumn();
            SetAlgorithm(_settings.DefaultAlgo);
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cts?.Cancel();
            bool timeEnabled = _menuOptionsTime?.Checked ?? false;
            _settings.UseAbsolutePaths = _menuOptionsAbsolutePaths?.Checked ?? false;
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;
            _settings.OptimizeForHDD = _menuOptionsHDD?.Checked ?? false;
            _settings.Save(this, timeEnabled, _currentHashType);
        }

        #endregion

        #region Virtual Mode Handling

        private void LvFiles_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _displayList.Count) return;

            var data = _displayList[e.ItemIndex];

            var item = new ListViewItem(data.FileName);
            item.SubItems.Add(data.CalculatedHash);
            item.SubItems.Add(data.Status);

            if (!string.IsNullOrEmpty(data.ExpectedHash))
                item.SubItems.Add(data.ExpectedHash);

            if (_settings.ShowTimeTab)
                item.SubItems.Add(data.TimeStr);

            item.ForeColor = data.ForeColor;
            item.BackColor = data.BackColor;

            // Apply Strikethrough for missing files
            if (data.FontStyle != FontStyle.Regular)
                item.Font = new Font(lvFiles.Font, data.FontStyle);

            e.Item = item;
        }

        private void UpdateDisplayList()
        {
            lvFiles.VirtualListSize = _displayList.Count;
            lvFiles.Invalidate();
        }

        #endregion

        #region Core Processing Logic

        private async Task HandleDroppedPaths(string[] paths)
        {
            if (paths.Length == 0) return;
            bool containsFolder = paths.Any(p => Directory.Exists(p));

            _listSorter.SortColumn = -1;
            _listSorter.Order = SortOrder.None;
            UpdateSortVisuals(-1, SortOrder.None);

            // Case 1: Single Checksum File -> Verification Mode
            if (!containsFolder && paths.Length == 1 && _verificationExtensions.Contains(Path.GetExtension(paths[0])) && File.Exists(paths[0]))
            {
                await RunVerification(paths[0]);
            }
            // Case 2: Files/Folders -> Creation Mode
            else
            {
                string baseDirectory = "";
                if (paths.Length == 1 && Directory.Exists(paths[0])) baseDirectory = paths[0];
                else
                {
                    var parentDirs = paths.Select(p => Path.GetDirectoryName(p) ?? p).ToList();
                    baseDirectory = FindCommonBasePath(parentDirs);
                }

                List<string> allFilesToHash = new List<string>();
                foreach (string path in paths)
                {
                    if (File.Exists(path)) allFilesToHash.Add(path);
                    else if (Directory.Exists(path))
                    {
                        try { allFilesToHash.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)); }
                        catch (Exception ex) { MessageBox.Show($"Error accessing folder '{path}': {ex.Message}"); }
                    }
                }
                await RunHashCreation(allFilesToHash.ToArray(), baseDirectory);
            }
        }

        private void SetProcessingState(bool processing)
        {
            _isProcessing = processing;
            if (_btnStop != null) _btnStop.Enabled = processing;
        }

        // --- PINNING LOGIC ---
        private void PinItemToTop(FileItemData data)
        {
            if (this.InvokeRequired) { this.Invoke(new Action(() => PinItemToTop(data))); return; }
            data.IsPinned = true;
            _displayList.Remove(data);
            _displayList.Insert(0, data);
            lvFiles.Invalidate();
        }

        private void UnpinAndRestore(FileItemData data)
        {
            if (this.InvokeRequired) { this.Invoke(new Action(() => UnpinAndRestore(data))); return; }
            data.IsPinned = false;
            _displayList.Sort(_listSorter);
            lvFiles.Invalidate();
        }

        // --- ENDIANNESS HELPER ---
        private string ReverseHexBytes(string hex)
        {
            if (hex.Length % 2 != 0) return hex;
            char[] charArray = new char[hex.Length];
            for (int i = 0; i < hex.Length; i += 2)
            {
                charArray[i] = hex[hex.Length - i - 2];
                charArray[i + 1] = hex[hex.Length - i - 1];
            }
            return new string(charArray);
        }

        private async Task RunHashCreation(string[] filePaths, string baseDirectory)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();

            SetupUIForMode("Creation");
            UpdateStats(0, filePaths.Length, 0, 0, 0);

            _allItems.Clear();
            _displayList.Clear();
            int originalIndex = 0;

            foreach (var fullPath in filePaths)
            {
                if (Directory.Exists(fullPath)) continue;

                string relativePath = "";
                if (!string.IsNullOrEmpty(baseDirectory))
                {
                    try { relativePath = Path.GetRelativePath(baseDirectory, fullPath); }
                    catch { relativePath = fullPath; }
                }
                else relativePath = Path.GetFileName(fullPath);

                var data = new FileItemData
                {
                    FullPath = fullPath,
                    FileName = Path.GetFileName(fullPath),
                    RelativePath = relativePath,
                    BaseDirectory = baseDirectory,
                    OriginalIndex = originalIndex++
                };
                _allItems.Add(data);
            }

            _displayList.AddRange(_allItems);
            UpdateDisplayList();

            if (_allItems.Count == 0) { SetProcessingState(false); return; }

            progressBarTotal.Maximum = _allItems.Count;
            progressBarTotal.Value = 0;

            int completed = 0, okCount = 0, errorCount = 0;

            // Threading Strategy
            int maxThreads = Environment.ProcessorCount;
            if (_settings.OptimizeForHDD) maxThreads = 1;
            else if (_allItems.Count > 0 && DriveDetector.IsRotational(_allItems[0].FullPath)) maxThreads = 1;

            try
            {
                using (var semaphore = new SemaphoreSlim(maxThreads))
                {
                    var processingList = _displayList.ToList();
                    var tasks = processingList.Select(async data =>
                    {
                        if (_cts.Token.IsCancellationRequested) return;

                        await semaphore.WaitAsync(_cts.Token);
                        try
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            IProgress<double>? progress = null;

                            try
                            {
                                long len = new FileInfo(data.FullPath).Length;
                                if (len > LargeFileThreshold)
                                {
                                    PinItemToTop(data);
                                    data.Status = "0%";
                                    progress = new Progress<double>(p =>
                                    {
                                        int pct = (int)p;
                                        if (data.Status != $"{pct}%") { data.Status = $"{pct}%"; lvFiles.Invalidate(); }
                                    });
                                }
                            }
                            catch { }

                            string hash = await HashHelper.ComputeHashAsync(data.FullPath, _currentHashType, progress, _cts.Token);
                            sw.Stop();

                            if (data.IsPinned) UnpinAndRestore(data);

                            if (hash == "CANCELLED") return;

                            data.CalculatedHash = hash;
                            if (_settings.ShowTimeTab) data.TimeStr = $"{sw.ElapsedMilliseconds} ms";

                            if (hash == "FILE_NOT_FOUND" || hash == "ACCESS_DENIED" || hash == "ERROR")
                            {
                                data.Status = hash;
                                data.ForeColor = ColRedText;
                                data.BackColor = ColRedBack;
                                Interlocked.Increment(ref errorCount);
                            }
                            else
                            {
                                data.Status = "Done";
                                data.ForeColor = ColGreenText;
                                data.BackColor = ColGreenBack;
                                Interlocked.Increment(ref okCount);
                            }

                            int currentCompleted = Interlocked.Increment(ref completed);
                            ThrottledUiUpdate(currentCompleted, processingList.Count, okCount, 0, errorCount);
                        }
                        catch (OperationCanceledException) { }
                        finally { semaphore.Release(); }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) { MessageBox.Show("Operation Stopped."); }

            // PLAY SOUND ON COMPLETION
            if (!_cts.Token.IsCancellationRequested)
            {
                if (errorCount > 0) SystemSounds.Exclamation.Play();
                else SystemSounds.Asterisk.Play();
            }

            SetProcessingState(false);
            this.Text = $"SharpSFV - Creation Complete [{_currentHashType}]";
            UpdateStats(completed, _allItems.Count, okCount, 0, errorCount);
            progressBarTotal.Value = completed;
            lvFiles.Invalidate();
        }

        private async Task RunVerification(string checkFilePath)
        {
            SetProcessingState(true);
            _cts = new CancellationTokenSource();
            SetupUIForMode("Verification");

            string baseFolder = Path.GetDirectoryName(checkFilePath) ?? "";
            string ext = Path.GetExtension(checkFilePath).ToLowerInvariant();

            HashType verificationAlgo = _currentHashType;
            if (ext == ".sfv") verificationAlgo = HashType.Crc32;
            else if (ext == ".md5") verificationAlgo = HashType.MD5;
            else if (ext == ".sha1") verificationAlgo = HashType.SHA1;
            else if (ext == ".sha256") verificationAlgo = HashType.SHA256;
            else if (ext == ".xxh3") verificationAlgo = HashType.XxHash3;

            SetAlgorithm(verificationAlgo);

            var parsedLines = new List<(string ExpectedHash, string Filename)>();
            try
            {
                foreach (var line in await File.ReadAllLinesAsync(checkFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(";")) continue;

                    string expectedHash = "", filename = "";
                    Match matchA = Regex.Match(line, @"^\s*([0-9a-fA-F]+)\s+\*?(.*?)\s*$");
                    Match matchB = Regex.Match(line, @"^\s*(.*?)\s+([0-9a-fA-F]{8})\s*$");

                    if (verificationAlgo == HashType.Crc32 && matchB.Success)
                    {
                        filename = matchB.Groups[1].Value.Trim();
                        expectedHash = matchB.Groups[2].Value;
                    }
                    else if (matchA.Success)
                    {
                        expectedHash = matchA.Groups[1].Value.Trim();
                        filename = matchA.Groups[2].Value.Trim();
                    }

                    if (!string.IsNullOrEmpty(expectedHash) && !string.IsNullOrEmpty(filename))
                        parsedLines.Add((expectedHash, filename));
                }
            }
            catch (Exception ex) { MessageBox.Show("Error parsing: " + ex.Message); SetProcessingState(false); return; }

            _allItems.Clear();
            _displayList.Clear();
            int originalIndex = 0;
            int missingCount = 0;

            foreach (var entry in parsedLines)
            {
                string fullPath = Path.GetFullPath(Path.Combine(baseFolder, entry.Filename));
                bool exists = File.Exists(fullPath);

                var data = new FileItemData
                {
                    FullPath = fullPath,
                    FileName = entry.Filename,
                    RelativePath = entry.Filename,
                    BaseDirectory = baseFolder,
                    ExpectedHash = entry.ExpectedHash,
                    OriginalIndex = originalIndex++,
                    CalculatedHash = "Waiting...",
                    Status = exists ? "Pending" : "MISSING"
                };

                if (!exists)
                {
                    data.ForeColor = ColYellowText;
                    data.BackColor = ColYellowBack;
                    data.FontStyle = FontStyle.Strikeout;
                    missingCount++;
                }
                _allItems.Add(data);
            }

            _displayList.AddRange(_allItems);
            UpdateDisplayList();
            UpdateStats(0, _allItems.Count, 0, 0, missingCount);
            progressBarTotal.Maximum = _allItems.Count;
            progressBarTotal.Value = 0;

            int completed = 0, okCount = 0, badCount = 0;

            // Threading Strategy
            int maxThreads = Environment.ProcessorCount;
            if (_settings.OptimizeForHDD) maxThreads = 1;
            else
            {
                var firstFile = _displayList.FirstOrDefault(x => File.Exists(x.FullPath));
                if (firstFile != null && DriveDetector.IsRotational(firstFile.FullPath)) maxThreads = 1;
            }

            try
            {
                using (var semaphore = new SemaphoreSlim(maxThreads))
                {
                    var processingList = _displayList.ToList();
                    var tasks = processingList.Select(async data =>
                    {
                        if (data.Status == "MISSING") return;
                        if (_cts.Token.IsCancellationRequested) return;

                        await semaphore.WaitAsync(_cts.Token);
                        try
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            IProgress<double>? progress = null;

                            try
                            {
                                if (new FileInfo(data.FullPath).Length > LargeFileThreshold)
                                {
                                    PinItemToTop(data);
                                    data.Status = "0%";
                                    progress = new Progress<double>(p =>
                                    {
                                        int pct = (int)p;
                                        if (data.Status != $"{pct}%") { data.Status = $"{pct}%"; lvFiles.Invalidate(); }
                                    });
                                }
                            }
                            catch { }

                            string calculatedHash = await HashHelper.ComputeHashAsync(data.FullPath, verificationAlgo, progress, _cts.Token);
                            sw.Stop();

                            if (data.IsPinned) UnpinAndRestore(data);
                            if (calculatedHash == "CANCELLED") return;

                            data.CalculatedHash = calculatedHash;
                            if (_settings.ShowTimeTab) data.TimeStr = $"{sw.ElapsedMilliseconds} ms";

                            bool isMatch = false;

                            if (calculatedHash == "FILE_NOT_FOUND" || calculatedHash == "ACCESS_DENIED" || calculatedHash == "ERROR")
                            {
                                // Error string
                            }
                            else
                            {
                                if (calculatedHash.Equals(data.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                }
                                else if (verificationAlgo == HashType.Crc32)
                                {
                                    string reversed = ReverseHexBytes(calculatedHash);
                                    if (reversed.Equals(data.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isMatch = true;
                                    }
                                }
                            }

                            if (isMatch)
                            {
                                data.Status = "OK";
                                data.ForeColor = ColGreenText;
                                data.BackColor = ColGreenBack;
                                Interlocked.Increment(ref okCount);
                            }
                            else if (calculatedHash.Length > 20)
                            {
                                data.Status = calculatedHash;
                                data.ForeColor = ColYellowText;
                                data.BackColor = ColYellowBack;
                                Interlocked.Increment(ref badCount);
                            }
                            else
                            {
                                data.Status = "BAD";
                                data.ForeColor = ColRedText;
                                data.BackColor = ColRedBack;
                                Interlocked.Increment(ref badCount);
                            }

                            int currentCompleted = Interlocked.Increment(ref completed);
                            ThrottledUiUpdate(currentCompleted, processingList.Count, okCount, badCount, missingCount);
                        }
                        catch (OperationCanceledException) { }
                        finally { semaphore.Release(); }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) { MessageBox.Show("Operation Stopped."); }

            // PLAY SOUND ON COMPLETION
            if (!_cts.Token.IsCancellationRequested)
            {
                if (badCount > 0 || missingCount > 0) SystemSounds.Exclamation.Play();
                else SystemSounds.Asterisk.Play();
            }

            SetProcessingState(false);
            UpdateStats(completed, _allItems.Count, okCount, badCount, missingCount);
            progressBarTotal.Value = completed;
            lvFiles.Invalidate();

            string algoName = Enum.GetName(typeof(HashType), verificationAlgo) ?? "Hash";
            this.Text = (badCount == 0 && missingCount == 0)
                ? $"SharpSFV [{algoName}] - All Files OK"
                : $"SharpSFV [{algoName}] - {badCount} Bad, {missingCount} Missing";
        }

        private void ThrottledUiUpdate(int current, int total, int ok, int bad, int missing)
        {
            long now = DateTime.Now.Ticks;
            if (now - Interlocked.Read(ref _lastUiUpdateTick) > 1000000)
            {
                lock (_uiLock)
                {
                    if (now - _lastUiUpdateTick > 1000000)
                    {
                        _lastUiUpdateTick = now;
                        this.Invoke(new Action(() =>
                        {
                            progressBarTotal.Value = Math.Min(current, progressBarTotal.Maximum);
                            UpdateStats(current, total, ok, bad, missing);
                            lvFiles.Invalidate();
                        }));
                    }
                }
            }
        }

        #endregion

        #region User Actions & Setup

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

        private void UpdateStats(int current, int total, int ok, int bad, int missing)
        {
            if (_lblProgress != null) { _lblProgress.Text = $"Completed files: {current} / {total}"; _lblProgress.Update(); }
            if (_lblStatsRow != null)
            {
                _lblStatsRow.Text = $"OK: {ok}     BAD: {bad}     MISSING: {missing}";
                if (bad > 0 || missing > 0) _lblStatsRow.ForeColor = Color.Red;
                else if (ok > 0) _lblStatsRow.ForeColor = Color.DarkGreen;
                else _lblStatsRow.ForeColor = Color.Black;
                _lblStatsRow.Update();
            }
        }

        private void SetupLayout()
        {
            this.Controls.Clear();
            Panel progressPanel = new Panel { Height = 25, Dock = DockStyle.Bottom, Padding = new Padding(2) };
            progressBarTotal.Dock = DockStyle.Fill;
            progressPanel.Controls.Add(progressBarTotal);
            this.Controls.Add(progressPanel);
            lvFiles.Dock = DockStyle.Fill;
            this.Controls.Add(lvFiles);
            if (_filterPanel != null) this.Controls.Add(_filterPanel);
            if (_statsPanel != null) this.Controls.Add(_statsPanel);
            if (_menuStrip != null) this.Controls.Add(_menuStrip);
        }

        private void SetupStatsPanel()
        {
            _statsPanel = new Panel { Height = 50, Dock = DockStyle.Top, BackColor = SystemColors.ControlLight, Padding = new Padding(10, 5, 10, 5) };
            _lblProgress = new Label { Text = "Ready", AutoSize = true, Font = new Font(this.Font, FontStyle.Bold), Location = new Point(10, 8) };
            _lblStatsRow = new Label { Text = "OK: 0     BAD: 0     MISSING: 0", AutoSize = true, Location = new Point(10, 28) };
            _btnStop = new Button { Text = "Stop", Location = new Point(700, 10), Size = new Size(75, 30), BackColor = Color.IndianRed, ForeColor = Color.White, Enabled = false, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            _btnStop.FlatStyle = FlatStyle.Flat;
            _btnStop.Click += (s, e) => { _cts?.Cancel(); };
            _statsPanel.Controls.Add(_lblProgress); _statsPanel.Controls.Add(_lblStatsRow); _statsPanel.Controls.Add(_btnStop);
        }

        private void SetupFilterPanel()
        {
            _filterPanel = new Panel { Height = 35, Dock = DockStyle.Top, BackColor = SystemColors.Control, Padding = new Padding(5), Visible = false };
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

        private void SetupUIForMode(string mode)
        {
            lvFiles.Columns.Clear();
            _listSorter.SortColumn = -1;
            _listSorter.Order = SortOrder.None;

            lvFiles.Columns.Add("File Name", 300);
            lvFiles.Columns.Add("Hash", 220);
            lvFiles.Columns.Add("Status", 100);

            if (mode == "Verification") lvFiles.Columns.Add("Expected Hash", 220);
            if (_settings.ShowTimeTab) lvFiles.Columns.Add("Time", 80);

            this.Text = (mode == "Verification") ? "SharpSFV - Verify" : $"SharpSFV - Create [{_currentHashType}]";
        }

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
                                if (!hash.Contains("...") && !hash.Equals("Pending") && !string.IsNullOrEmpty(hash))
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
            if (validPaths.Count > 0) _ = HandleDroppedPaths(validPaths.ToArray()); // Fire and forget
        }

        private void PerformOpenAction()
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Hash Files (*.xxh3;*.md5;*.sfv;*.sha1;*.sha256;*.txt)|*.xxh3;*.md5;*.sfv;*.sha1;*.sha256;*.txt|All Files (*.*)|*.*" })
            {
                if (ofd.ShowDialog() == DialogResult.OK) _ = HandleDroppedPaths(new string[] { ofd.FileName });
            }
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

        private void UpdateSortVisuals(int column, SortOrder order)
        {
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Text.EndsWith(" ▲") || ch.Text.EndsWith(" ▼")) ch.Text = ch.Text.Substring(0, ch.Text.Length - 2);
                if (ch.Index == column) ch.Text += (order == SortOrder.Ascending) ? " ▲" : " ▼";
            }
        }

        private void LvFiles_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            if (_isProcessing) return;

            _listSorter.SortColumn = e.Column;
            _listSorter.Order = (_listSorter.Order == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;

            _displayList.Sort(_listSorter);

            lvFiles.Invalidate();
            UpdateSortVisuals(e.Column, _listSorter.Order);
        }

        private void ApplyFilter()
        {
            if (_txtFilter == null || _cmbStatusFilter == null) return;
            string searchText = _txtFilter.Text.Trim();
            string statusFilter = _cmbStatusFilter.SelectedItem?.ToString() ?? "All";

            _displayList.Clear();

            if (string.IsNullOrEmpty(searchText) && statusFilter == "All")
            {
                _displayList.AddRange(_allItems);
            }
            else
            {
                foreach (var item in _allItems)
                {
                    bool matchName = string.IsNullOrEmpty(searchText) || item.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase);

                    bool matchStatus = statusFilter == "All";
                    if (!matchStatus)
                    {
                        if (statusFilter == "BAD") matchStatus = item.Status == "BAD" || item.Status.Contains("ERROR") || item.Status.Contains("NOT_FOUND");
                        else matchStatus = item.Status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase);
                    }

                    if (matchName && matchStatus) _displayList.Add(item);
                }
            }

            if (_listSorter.SortColumn != -1) _displayList.Sort(_listSorter);

            UpdateDisplayList();
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
                if (!paths.All(p => {
                    var parts = p.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    return i < parts.Length && parts[i].Equals(currentSegment, StringComparison.OrdinalIgnoreCase);
                })) break;
                commonPath = (i == 0 && isRooted && currentSegment.Contains(":")) ? currentSegment + Path.DirectorySeparatorChar : Path.Combine(commonPath, currentSegment);
            }
            return commonPath.TrimEnd(Path.DirectorySeparatorChar);
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

            Label lbl = new Label { Text = "SharpSFV v2.20\n\nCreated by L33T.\nInspired by QuickSFV.", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 80, Padding = new Padding(0, 20, 0, 0) };
            LinkLabel link = new LinkLabel { Text = "Visit GitHub Repository", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top };
            link.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/Wishwanderer/SharpSFV", UseShellExecute = true }); } catch { } };
            Button btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point((credits.ClientSize.Width - 80) / 2, 120), Size = new Size(80, 30) };

            credits.Controls.Add(btnOk);
            credits.Controls.Add(link);
            credits.Controls.Add(lbl);
            credits.ShowDialog();
        }

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

    /// <summary>
    /// Simple Input Dialog to replace Visual Basic interaction
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