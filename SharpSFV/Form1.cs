using SharpSFV.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1 : Form
    {
        #region Fields & State

        private AppSettings _settings;
        private bool _skipSaveOnClose = false;

        // NEW: Atomic flag for UI Throttling (0 = Free, 1 = Busy)
        private int _uiBusy = 0;

        // Standard File Store (SoA)
        private FileStore _fileStore = new FileStore();

        // Job Store (SoA)
        private JobStore _jobStore = new JobStore();
        private bool _isJobMode = false;
        private bool _isJobQueueRunning = false;

        // Holds indices pointing to _fileStore for the Virtual ListView
        private List<int> _displayIndices = new List<int>();

        // Sorter now compares integers (indices) via the Store
        private FileListSorter _listSorter;

        // UI Constraints
        private Dictionary<int, int> _originalColWidths = new Dictionary<int, int>();
        private int _cachedFullPathWidth = 400;
        private int _cachedFileNameWidth = 300;

        // State Flags
        private bool _isProcessing = false;
        private bool _isVerificationMode = false;
        private HashType _currentHashType = HashType.XxHash3;
        private CancellationTokenSource? _cts;

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

        // Advanced Options Bar Controls
        private Panel? _advancedPanel;
        private TextBox? _txtPathPrefix;
        private TextBox? _txtInclude;
        private TextBox? _txtExclude;
        private CheckBox? _chkRecursive;

        // Stats Controls
        private Label? _lblProgress;
        private Label? _lblTotalTime;
        private FlowLayoutPanel? _statsFlowPanel;
        private Label? _lblStatsOK;
        private Label? _lblStatsBad;
        private Label? _lblStatsMissing;
        private Button? _btnStop;

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

        // Processing Mode Menus
        private ToolStripMenuItem? _menuProcAuto;
        private ToolStripMenuItem? _menuProcHDD;
        private ToolStripMenuItem? _menuProcSSD;

        // Path Storage Menus
        private ToolStripMenuItem? _menuPathRelative;
        private ToolStripMenuItem? _menuPathAbsolute;

        // Script Menus
        private ToolStripMenuItem? _menuGenCopyDups;
        private ToolStripMenuItem? _menuGenDelDups;
        private ToolStripMenuItem? _menuGenBadFiles;

        // View Menu References
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

        public Form1()
        {
            InitializeComponent();

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

            SetupCustomMenu();
            SetupContextMenu();
            SetupStatsPanel();
            SetupFilterPanel();
            SetupAdvancedPanel();
            SetupDragDrop();
            SetupActiveJobsPanel();

            ApplySettings();
            SetupLayout();

            SetupUIForMode("Creation");

            this.Text = "SharpSFV";
        }

        private void EnableDoubleBuffer(Control control)
        {
            typeof(Control).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, control, new object[] { true });
        }

        private async void Form1_Shown(object? sender, EventArgs e)
        {
            await Task.Delay(100);
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) await HandleDroppedPaths(args.Skip(1).ToArray());
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cts?.Cancel();

            if (_skipSaveOnClose) return;

            bool timeEnabled = _menuViewTime?.Checked ?? false;
            _settings.ShowFullPaths = _menuViewShowFullPaths?.Checked ?? false;

            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;

            _settings.ShowAdvancedBar = _menuOptionsAdvanced?.Checked ?? false;
            if (_txtPathPrefix != null) _settings.PathPrefix = _txtPathPrefix.Text;
            if (_txtInclude != null) _settings.IncludePattern = _txtInclude.Text;
            if (_txtExclude != null) _settings.ExcludePattern = _txtExclude.Text;
            if (_chkRecursive != null) _settings.ScanRecursive = _chkRecursive.Checked;

            _settings.ShowHashCol = _menuViewHash?.Checked ?? true;
            _settings.ShowExpectedHashCol = _menuViewExpected?.Checked ?? true;
            _settings.LockColumns = _menuViewLockCols?.Checked ?? true;

            int distToSave = _settings.SplitterDistance;
            if (_mainSplitter != null && !_mainSplitter.Panel1Collapsed && _mainSplitter.SplitterDistance > 0)
            {
                distToSave = _mainSplitter.SplitterDistance;
            }

            _settings.Save(this, timeEnabled, _currentHashType, distToSave, lvFiles);

            _fontBold?.Dispose();
            _fontStrike?.Dispose();
            _filterDebounceTimer?.Dispose();

            StringPool.Clear();
        }

        // --- JOB LOGIC ---

        private void PerformClearCompletedJobs()
        {
            if (!_isJobMode) return;

            _jobStore.RemoveCompleted();
            lvFiles.VirtualListSize = _jobStore.Count;
            lvFiles.Invalidate();
            UpdateJobStats();
        }

        private void UpdateJobStats()
        {
            if (!_isJobMode) return;

            int done = 0;
            int error = 0;
            int inProgress = 0;

            for (int i = 0; i < _jobStore.Count; i++)
            {
                switch (_jobStore.Statuses[i])
                {
                    case JobStatus.Done: done++; break;
                    case JobStatus.Error: error++; break;
                    case JobStatus.InProgress: inProgress++; break;
                }
            }

            if (_lblProgress != null) _lblProgress.Text = $"Jobs Completed: {done} / {_jobStore.Count}";

            if (_lblStatsOK != null)
            {
                _lblStatsOK.Text = $"DONE: {done}";
                _lblStatsOK.ForeColor = ColGreenText;
            }

            if (_lblStatsBad != null)
            {
                _lblStatsBad.Text = $"ERROR: {error}";
                _lblStatsBad.ForeColor = ColRedText;
            }

            if (_lblStatsMissing != null)
            {
                _lblStatsMissing.Text = $"IN PROGRESS: {inProgress}";
                _lblStatsMissing.ForeColor = Color.Blue;
            }
        }

        private void SetAppMode(bool jobMode)
        {
            if (_isJobMode == jobMode) return;
            _isJobMode = jobMode;

            if (_menuJobsEnable != null) _menuJobsEnable.Checked = _isJobMode;

            lvFiles.BeginUpdate();
            lvFiles.Columns.Clear();
            _originalColWidths.Clear();

            if (_isJobMode)
            {
                AddCol("Job Name", 250, "JobName");
                AddCol("Root Path", 350, "RootPath");
                AddCol("Progress", 100, "Progress");
                AddCol("Status", 100, "Status");

                if (_mainSplitter != null) _mainSplitter.Panel1Collapsed = true;
                if (_filterPanel != null) _filterPanel.Visible = false;
                if (_advancedPanel != null) _advancedPanel.Visible = false;

                lvFiles.VirtualListSize = _jobStore.Count;
                this.Text = "SharpSFV - Job Queue";

                UpdateJobStats();
            }
            else
            {
                SetupUIForMode("Creation");
                if (_settings.ShowFilterPanel && _filterPanel != null) _filterPanel.Visible = true;
                if (_settings.ShowAdvancedBar && _advancedPanel != null) _advancedPanel.Visible = true;

                lvFiles.VirtualListSize = _displayIndices.Count;
                SetAlgorithm(_currentHashType);

                UpdateStats(0, 0, 0, 0, 0);
            }

            lvFiles.EndUpdate();
            lvFiles.Invalidate();
        }

        private void UpdateStats(int current, int total, int ok, int bad, int missing)
        {
            if (_isJobMode) return;

            if (_lblProgress != null)
            {
                if (total == 0 && current == 0) _lblProgress.Text = "Ready";
                else _lblProgress.Text = $"Completed files: {current} / {total}";
                _lblProgress.Update();
            }

            if (_lblStatsOK != null)
            {
                _lblStatsOK.Text = $"OK: {ok}";
                _lblStatsOK.ForeColor = ColGreenText;
            }
            if (_lblStatsBad != null)
            {
                _lblStatsBad.Text = $"BAD: {bad}";
                _lblStatsBad.ForeColor = ColRedText;
            }
            if (_lblStatsMissing != null)
            {
                _lblStatsMissing.Text = $"MISSING: {missing}";
                _lblStatsMissing.ForeColor = ColYellowText;
            }

            if (_menuGenBadFiles != null)
            {
                _menuGenBadFiles.Enabled = (bad > 0);
            }

            _statsFlowPanel?.Update();
        }

        private void AddCol(string text, int width, string tag)
        {
            ColumnHeader ch = lvFiles.Columns.Add(text, width);
            ch.Tag = tag;
            _originalColWidths[ch.Index] = width;
        }

        private void ToggleShowFullPaths(bool toggle = true)
        {
            if (toggle) _settings.ShowFullPaths = !_settings.ShowFullPaths;

            if (_menuViewShowFullPaths != null) _menuViewShowFullPaths.Checked = _settings.ShowFullPaths;

            RecalculateColumnWidths();

            int nameColIdx = -1;
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Tag as string == "Name") { nameColIdx = ch.Index; break; }
            }

            if (nameColIdx != -1)
            {
                if (_settings.ShowFullPaths) lvFiles.Columns[nameColIdx].Width = _cachedFullPathWidth;
                else
                {
                    _originalColWidths[nameColIdx] = _cachedFileNameWidth;
                    lvFiles.Columns[nameColIdx].Width = _cachedFileNameWidth;
                }
            }
            lvFiles.Invalidate();
        }

        private void ToggleAdvancedBar()
        {
            _settings.ShowAdvancedBar = _menuOptionsAdvanced?.Checked ?? false;
            if (_advancedPanel != null) _advancedPanel.Visible = _settings.ShowAdvancedBar;
        }

        private void RecalculateColumnWidths()
        {
            if (_fileStore.Count == 0)
            {
                _cachedFullPathWidth = 400;
                _cachedFileNameWidth = 300;
                return;
            }

            string longestPath = "";
            string longestName = "";
            int maxPathLen = -1;
            int maxNameLen = -1;

            for (int i = 0; i < _fileStore.Count; i++)
            {
                string? baseDir = _fileStore.BaseDirectories[i];
                string? relPath = _fileStore.RelativePaths[i];
                string? name = _fileStore.FileNames[i];

                if (baseDir != null && relPath != null)
                {
                    int estimatedLen = baseDir.Length + relPath.Length + 1;
                    if (estimatedLen > maxPathLen)
                    {
                        maxPathLen = estimatedLen;
                        longestPath = _fileStore.GetFullPath(i);
                    }
                }

                if (name != null && name.Length > maxNameLen)
                {
                    maxNameLen = name.Length;
                    longestName = name;
                }
            }

            if (!string.IsNullOrEmpty(longestPath))
                _cachedFullPathWidth = TextRenderer.MeasureText(longestPath, lvFiles.Font).Width + 25;
            else _cachedFullPathWidth = 400;

            if (!string.IsNullOrEmpty(longestName))
                _cachedFileNameWidth = TextRenderer.MeasureText(longestName, lvFiles.Font).Width + 25;
            else _cachedFileNameWidth = 300;
        }
    }
}