using SharpSFV.Models;
using SharpSFV.Interop;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Media;
using System.IO.Pipes;
using System.Text;
using System.IO;

namespace SharpSFV
{
    public partial class Form1 : Form
    {
        #region Fields & State

        private AppSettings _settings;
        private bool _skipSaveOnClose = false;

        // UI Throttling
        private int _uiBusy = 0;

        // PAUSE / RESUME LOGIC
        private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
        private bool _isPaused = false;

        // Standard File Store (SoA)
        private FileStore _fileStore = new FileStore();

        // Job Store (SoA)
        private JobStore _jobStore = new JobStore();
        private bool _isJobMode = false;
        private bool _isJobQueueRunning = false;

        // Holds indices pointing to _fileStore for the Virtual ListView
        private List<int> _displayIndices = new List<int>();

        // Sorter
        private FileListSorter _listSorter;

        // UI Constraints
        private Dictionary<int, int> _originalColWidths = new Dictionary<int, int>();
        private int _cachedFullPathWidth = 400;
        private int _cachedFileNameWidth = 300;

        // State Flags
        private bool _isProcessing = false;
        private bool _isVerificationMode = false;
        private HashType _currentHashType = HashType.XXHASH3;
        private CancellationTokenSource? _cts;
        private volatile bool _legacySfvDetected = false; // Track endian issues

        // Headless Mode Flag
        private bool _isHeadless = false;
        private string[] _startupArgs;

        // Taskbar Progress
        private ITaskbarList3? _taskbarList;

        // Context Menu Create Mode (IPC)
        private bool _isCreateMode = false;
        private List<string> _collectedPaths = new List<string>();
        private System.Windows.Forms.Timer? _debounceTimer;
        private string? _autoSavePath;

        // Threading & UI Throttling
        private object _uiLock = new object();
        private long _lastUiUpdateTick = 0;

        // GDI Cache
        private Font _fontBold;
        private Font _fontStrike;

        // Dynamic UI Controls
        private MenuStrip? _menuStrip;
        private ContextMenuStrip? _ctxMenu;

        // Layout Controls
        private SplitContainer? _mainSplitter;
        private Panel? _statsPanel;
        private Panel? _filterPanel;

        // Comments Panel
        private Panel? _commentsPanel;
        private TextBox? _txtComments;

        // Advanced Options Bar Controls
        private Panel? _advancedPanel;
        private TextBox? _txtPathPrefix;
        private TextBox? _txtInclude;
        private TextBox? _txtExclude;
        private CheckBox? _chkRecursive;

        // Stats Controls
        private Label? _lblProgress;
        private Label? _lblTotalTime;
        private Label? _lblLegacyWarning;
        private FlowLayoutPanel? _statsFlowPanel;
        private Label? _lblStatsOK;
        private Label? _lblStatsBad;
        private Label? _lblStatsMissing;

        // Control Buttons
        private Button? _btnStop;
        private Button? _btnPause;

        // Active Jobs Controls
        private ListView? _lvActiveJobs;

        // Filter Controls
        private TextBox? _txtFilter;
        private ComboBox? _cmbStatusFilter;
        private CheckBox? _chkShowDuplicates;
        private System.Threading.Timer? _filterDebounceTimer;

        // Menu References
        private ToolStripMenuItem? _menuViewTime;
        private ToolStripMenuItem? _menuViewShowFullPaths;
        private ToolStripMenuItem? _menuOptionsFilter;
        private ToolStripMenuItem? _menuOptionsAdvanced;
        private ToolStripMenuItem? _menuProcAuto;
        private ToolStripMenuItem? _menuProcHDD;
        private ToolStripMenuItem? _menuProcSSD;
        private ToolStripMenuItem? _menuPathRelative;
        private ToolStripMenuItem? _menuPathAbsolute;
        private ToolStripMenuItem? _menuGenCopyDups;
        private ToolStripMenuItem? _menuGenDelDups;
        private ToolStripMenuItem? _menuGenBadFiles;
        private ToolStripMenuItem? _menuViewHash;
        private ToolStripMenuItem? _menuViewExpected;
        private ToolStripMenuItem? _menuViewLockCols;

        private Dictionary<HashType, ToolStripMenuItem> _algoMenuItems = new Dictionary<HashType, ToolStripMenuItem>();

        // Constants & Colors
        private const long LargeFileThreshold = 50 * 1024 * 1024; // 50MB
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

        public Form1(string[] args)
        {
            InitializeComponent();
            _startupArgs = args;

            // Create Mode (Shell Integration)
            if (args.Contains("-create", StringComparer.OrdinalIgnoreCase))
            {
                _isCreateMode = true;
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                this.WindowState = FormWindowState.Normal;

                _debounceTimer = new System.Windows.Forms.Timer { Interval = 200 };
                _debounceTimer.Tick += OnDebounceTick;

                StartPipeServer();
            }
            // Check Headless Mode
            else if (args.Contains("-headless", StringComparer.OrdinalIgnoreCase))
            {
                _isHeadless = true;
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                this.WindowState = FormWindowState.Minimized;
            }

            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("SharpSFV.SharpSFV.ico"))
                {
                    if (stream != null) this.Icon = new Icon(stream);
                }
            }
            catch { }

            _settings = new AppSettings(Application.ExecutablePath);
            _settings.Load();

            if (!_isHeadless && !_isCreateMode)
            {
                try
                {
                    Guid clsid = Guid.Parse("56FDF344-FD6D-11d0-958A-006097C9A090");
                    Type? type = Type.GetTypeFromCLSID(clsid);
                    if (type != null)
                    {
                        _taskbarList = (ITaskbarList3?)Activator.CreateInstance(type);
                        _taskbarList?.HrInit();
                    }
                }
                catch { }
            }

            _listSorter = new FileListSorter(_fileStore);

            _fontBold = new Font(this.Font, FontStyle.Bold);
            _fontStrike = new Font(this.Font, FontStyle.Strikeout);

            this.lvFiles.VirtualMode = true;
            this.lvFiles.RetrieveVirtualItem += LvFiles_RetrieveVirtualItem;
            this.lvFiles.Scrollable = true;
            EnableDoubleBuffer(this.lvFiles);

            this.lvFiles.ColumnClick += LvFiles_ColumnClick;
            this.FormClosing += Form1_FormClosing;
            this.Shown += Form1_Shown;

            SetupLayout();
            SetupContextMenu();
            SetupDragDrop();

            if (!_isHeadless && !_isCreateMode) ApplySettings();

            SetAlgorithm(_currentHashType);
        }

        private async void StartPipeServer()
        {
            while (!_isProcessing && _isCreateMode)
            {
                try
                {
                    using (var server = new NamedPipeServerStream("SharpSFV_Pipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        await server.WaitForConnectionAsync();
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string? line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("-"))
                                {
                                    this.Invoke(new Action(() => {
                                        _collectedPaths.Add(line);
                                        _debounceTimer?.Stop();
                                        _debounceTimer?.Start();
                                    }));
                                }
                            }
                        }
                    }
                }
                catch { await Task.Delay(100); }
            }
        }

        private void OnDebounceTick(object? sender, EventArgs e)
        {
            _debounceTimer?.Stop();
            LaunchCreateMode();
        }

        private void LaunchCreateMode()
        {
            if (_collectedPaths.Count == 0)
            {
                Application.Exit();
                return;
            }

            string baseDir = "";
            if (_collectedPaths.Count == 1 && Directory.Exists(_collectedPaths[0])) baseDir = _collectedPaths[0];
            else if (_collectedPaths.Count == 1) baseDir = Path.GetDirectoryName(_collectedPaths[0]) ?? "";
            else baseDir = FindCommonBasePath(_collectedPaths);

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "SharpSFV - Create Checksum";
                sfd.InitialDirectory = baseDir;
                sfd.FileName = "checksum";
                sfd.Filter = "SFV File (*.sfv)|*.sfv|MD5 File (*.md5)|*.md5|SHA1 File (*.sha1)|*.sha1|SHA256 File (*.sha256)|*.sha256|xxHash3 (*.xxh3)|*.xxh3";

                sfd.FilterIndex = _settings.DefaultAlgo switch
                {
                    HashType.MD5 => 2,
                    HashType.SHA1 => 3,
                    HashType.SHA256 => 4,
                    HashType.XXHASH3 => 5,
                    _ => 1
                };

                this.TopMost = true;
                if (sfd.ShowDialog() != DialogResult.OK)
                {
                    Application.Exit();
                    return;
                }
                this.TopMost = false;
                _autoSavePath = sfd.FileName;
            }

            SetupMiniLayout();

            string ext = Path.GetExtension(_autoSavePath).ToLower();
            if (ext == ".md5") _currentHashType = HashType.MD5;
            else if (ext == ".sha1") _currentHashType = HashType.SHA1;
            else if (ext == ".sha256") _currentHashType = HashType.SHA256;
            else if (ext == ".xxh3") _currentHashType = HashType.XXHASH3;
            else _currentHashType = HashType.Crc32;

            _settings.DefaultAlgo = _currentHashType;
            _settings.Save(this, false, _currentHashType, -1, null!);

            this.Opacity = 1;
            this.ShowInTaskbar = true;
            this.CenterToScreen();

            try
            {
                if (_taskbarList == null)
                {
                    Guid clsid = Guid.Parse("56FDF344-FD6D-11d0-958A-006097C9A090");
                    Type? type = Type.GetTypeFromCLSID(clsid);
                    if (type != null)
                    {
                        _taskbarList = (ITaskbarList3?)Activator.CreateInstance(type);
                        _taskbarList?.HrInit();
                    }
                }
            }
            catch { }

            _ = RunHashCreation(_collectedPaths.ToArray(), baseDir);
        }

        private void EnableDoubleBuffer(Control control)
        {
            typeof(Control).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, control, new object[] { true });
        }

        private async void Form1_Shown(object? sender, EventArgs e)
        {
            if (_isCreateMode) return;

            await Task.Delay(100);

            var pathsToProcess = _startupArgs.Where(a => !a.StartsWith("-")).ToArray();

            if (pathsToProcess.Length > 0)
            {
                await HandleDroppedPaths(pathsToProcess);
            }
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cts?.Cancel();
            _pauseEvent?.Dispose();
            _debounceTimer?.Dispose();

            if (_skipSaveOnClose || _isHeadless || _isCreateMode) return;

            bool timeEnabled = _menuViewTime?.Checked ?? false;
            _settings.ShowFullPaths = _menuViewShowFullPaths?.Checked ?? false;
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;
            _settings.ShowAdvancedBar = _menuOptionsAdvanced?.Checked ?? false;
            if (_txtPathPrefix != null) _settings.PathPrefix = _txtPathPrefix.Text;
            if (_txtInclude != null) _settings.IncludePattern = _txtInclude.Text;
            if (_txtExclude != null) _settings.ExcludePattern = _txtExclude.Text;
            if (_chkRecursive != null) _settings.ScanRecursive = _chkRecursive.Checked;
            if (_chkComments != null) _chkComments.Checked = _settings.EnableChecksumComments;

            _settings.ShowHashCol = _menuViewHash?.Checked ?? true;
            _settings.ShowExpectedHashCol = _menuViewExpected?.Checked ?? true;
            _settings.LockColumns = _menuViewLockCols?.Checked ?? true;

            int distToSave = _settings.SplitterDistance;
            if (_mainSplitter != null && !_mainSplitter.Panel1Collapsed && _mainSplitter.SplitterDistance > 0)
                distToSave = _mainSplitter.SplitterDistance;

            _settings.Save(this, timeEnabled, _currentHashType, distToSave, lvFiles);

            _fontBold?.Dispose();
            _fontStrike?.Dispose();
            _filterDebounceTimer?.Dispose();

            StringPool.Clear();
        }

        private void SetProgressBarColor(int state)
        {
            if (_isHeadless) return;
            Win32Storage.SetProgressBarState(progressBarTotal, state);
        }

        private void HandleCompletion(int completed, int ok, int bad, int missing, Stopwatch sw, bool verifyMode = false)
        {
            bool isCancelled = _cts?.Token.IsCancellationRequested ?? false;

            if (!isCancelled && !_isHeadless)
            {
                if (bad > 0 || (missing > 0 && verifyMode)) SystemSounds.Exclamation.Play();
                else SystemSounds.Asterisk.Play();
            }

            SetProcessingState(false);

            if (_isHeadless)
            {
                Console.WriteLine();
                Console.WriteLine("------------------------------------------------");
                Console.WriteLine($"Total: {completed}");
                Console.WriteLine($"OK: {ok}");
                Console.WriteLine($"BAD: {bad}");
                Console.WriteLine($"MISSING: {missing}");
                Console.WriteLine($"Time: {sw.ElapsedMilliseconds} ms");
                Console.WriteLine("------------------------------------------------");
                Application.Exit();
                return;
            }

            this.Invoke(new Action(() =>
            {
                RecalculateColumnWidths();
                UpdateStats(completed, _fileStore.Count, ok, bad, missing);

                if (progressBarTotal != null)
                {
                    int safeTotal = Math.Max(1, _fileStore.Count);
                    progressBarTotal.Maximum = safeTotal;
                    progressBarTotal.Value = Math.Min(completed, safeTotal);

                    int pbState = Win32Storage.PBST_NORMAL;
                    if (bad > 0 || missing > 0) pbState = Win32Storage.PBST_ERROR;
                    else if (isCancelled) pbState = Win32Storage.PBST_PAUSED;

                    SetProgressBarColor(pbState);

                    if (_taskbarList != null)
                    {
                        try
                        {
                            _taskbarList.SetProgressValue(this.Handle, (ulong)completed, (ulong)safeTotal);

                            var flag = (pbState == Win32Storage.PBST_ERROR) ? TbpFlag.Error : TbpFlag.Normal;
                            if (isCancelled) flag = TbpFlag.Paused;
                            _taskbarList.SetProgressState(this.Handle, flag);
                        }
                        catch { }
                    }
                }

                if (verifyMode)
                {
                    string algoName = Enum.GetName(typeof(HashType), _currentHashType) ?? "Hash";
                    this.Text = (bad == 0 && missing == 0)
                        ? $"SharpSFV [{algoName}] - All Files OK"
                        : $"SharpSFV [{algoName}] - {bad} Bad, {missing} Missing";
                }
                else
                {
                    this.Text = $"SharpSFV - Creation Complete [{_currentHashType}]";

                    if (_isCreateMode && !string.IsNullOrEmpty(_autoSavePath) && !isCancelled)
                    {
                        SaveResultsToFile(_autoSavePath);
                        Application.Exit();
                    }
                }

                if (_settings.ShowTimeTab && !isCancelled && _lblTotalTime != null && !_isJobMode)
                {
                    _lblTotalTime.Text = $"Total Elapsed Time: {sw.ElapsedMilliseconds} ms";
                    _lblTotalTime.Visible = true;
                }

                if (_legacySfvDetected && _lblLegacyWarning != null && !_isJobMode)
                {
                    _lblLegacyWarning.Visible = true;
                }

                lvFiles.Invalidate();
            }));
        }

        private void SaveResultsToFile(string path)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(path))
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
                        if (!hash.Contains("...") && !string.IsNullOrEmpty(hash))
                        {
                            string fullPath = _fileStore.GetFullPath(i);
                            string relPath = _fileStore.RelativePaths[i] ?? Path.GetFileName(fullPath);
                            sw.WriteLine($"{hash} *{relPath}");
                        }
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("Error saving: " + ex.Message); }
        }

        private void PerformClearCompletedJobs()
        {
            if (!_isJobMode) return;
            _jobStore.RemoveCompleted();
            lvFiles.VirtualListSize = _jobStore.Count;
            lvFiles.Invalidate();
            UpdateJobStats();
        }
    }
}