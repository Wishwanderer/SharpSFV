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

        // OPTIMIZATION #2: SoA Data Models
        // Replaces List<FileItemData>
        private FileStore _fileStore = new FileStore();

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

        // Stats Controls
        private Label? _lblProgress;
        private Label? _lblTotalTime; // NEW: Time Label
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
        private ToolStripMenuItem? _menuOptionsTime;
        private ToolStripMenuItem? _menuOptionsAbsolutePaths;
        private ToolStripMenuItem? _menuOptionsFilter;
        private ToolStripMenuItem? _menuOptionsHDD;
        private ToolStripMenuItem? _menuOptionsShowFullPaths;

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
                // "SharpSFV" is the Default Namespace.
                // "SharpSFV.ico" is the file name.
                // Combined Resource ID: "SharpSFV.SharpSFV.ico"
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("SharpSFV.SharpSFV.ico"))
                {
                    if (stream != null) this.Icon = new Icon(stream);
                }
            }
            catch { /* Fail silently to default icon if missing */ }
            // ----------------------------------------

            _settings = new AppSettings(Application.ExecutablePath);
            _settings.Load();

            // Pass the store to the sorter
            _listSorter = new FileListSorter(_fileStore);

            // Init GDI Cache
            _fontBold = new Font(this.Font, FontStyle.Bold);
            _fontStrike = new Font(this.Font, FontStyle.Strikeout);

            // Virtual Mode Setup
            this.lvFiles.VirtualMode = true;
            this.lvFiles.RetrieveVirtualItem += LvFiles_RetrieveVirtualItem;
            this.lvFiles.Scrollable = true;
            EnableDoubleBuffer(this.lvFiles);

            // Events
            this.lvFiles.ColumnClick += LvFiles_ColumnClick;
            this.FormClosing += Form1_FormClosing;
            this.Shown += Form1_Shown;

            // Layout Initialization
            SetupCustomMenu();
            SetupContextMenu();
            SetupStatsPanel();
            SetupFilterPanel();
            SetupDragDrop();

            // Initialize Active Jobs (SSD Optimization)
            SetupActiveJobsPanel();

            ApplySettings();
            SetupLayout();

            // Initialize Columns and Constraints
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
            // Async command line handling to ensure UI renders first
            await Task.Delay(100);
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) await HandleDroppedPaths(args.Skip(1).ToArray());
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cts?.Cancel();
            bool timeEnabled = _menuOptionsTime?.Checked ?? false;

            // Sync Menu State to Settings
            _settings.UseAbsolutePaths = _menuOptionsAbsolutePaths?.Checked ?? false;
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;
            _settings.OptimizeForHDD = _menuOptionsHDD?.Checked ?? false;
            _settings.ShowFullPaths = _menuOptionsShowFullPaths?.Checked ?? false;

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

            // Clean up static pools
            StringPool.Clear();
        }

        // --- Column Logic (View Constraints) ---

        private void ToggleShowFullPaths(bool toggle = true)
        {
            if (toggle) _settings.ShowFullPaths = !_settings.ShowFullPaths;
            if (_menuOptionsShowFullPaths != null) _menuOptionsShowFullPaths.Checked = _settings.ShowFullPaths;

            RecalculateColumnWidths();

            // Find the File Name column using tag "Name"
            int nameColIdx = -1;
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Tag as string == "Name")
                {
                    nameColIdx = ch.Index;
                    break;
                }
            }

            if (nameColIdx != -1)
            {
                if (_settings.ShowFullPaths)
                {
                    lvFiles.Columns[nameColIdx].Width = _cachedFullPathWidth;
                }
                else
                {
                    _originalColWidths[nameColIdx] = _cachedFileNameWidth;
                    lvFiles.Columns[nameColIdx].Width = _cachedFileNameWidth;
                }
            }

            lvFiles.Invalidate();
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

            // Direct Array Access - Fast O(N)
            for (int i = 0; i < _fileStore.Count; i++)
            {
                // Safety check for nulls
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
            {
                _cachedFullPathWidth = TextRenderer.MeasureText(longestPath, lvFiles.Font).Width + 25;
            }
            else _cachedFullPathWidth = 400;

            if (!string.IsNullOrEmpty(longestName))
            {
                _cachedFileNameWidth = TextRenderer.MeasureText(longestName, lvFiles.Font).Width + 25;
            }
            else _cachedFileNameWidth = 300;
        }
    }
}