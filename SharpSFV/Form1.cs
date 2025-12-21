using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1 : Form
    {
        #region Fields & Constants

        private AppSettings _settings;

        // Data Models
        private List<FileItemData> _allItems = new List<FileItemData>();
        private List<FileItemData> _displayList = new List<FileItemData>();
        private FileListSorter _listSorter = new FileListSorter();

        // State Flags
        private bool _isProcessing = false;
        private HashType _currentHashType = HashType.XxHash3;
        private CancellationTokenSource? _cts;

        // Threading & UI Throttling
        private object _uiLock = new object();
        private long _lastUiUpdateTick = 0;

        // Dynamic UI Controls
        private MenuStrip? _menuStrip;
        private ContextMenuStrip? _ctxMenu;
        private Panel? _statsPanel;
        private Panel? _filterPanel;
        private Button? _btnStop;
        private Label? _lblProgress;
        private Label? _lblStatsRow;
        private TextBox? _txtFilter;
        private ComboBox? _cmbStatusFilter;

        // Menu References
        private ToolStripMenuItem? _menuOptionsTime;
        private ToolStripMenuItem? _menuOptionsAbsolutePaths;
        private ToolStripMenuItem? _menuOptionsFilter;
        private ToolStripMenuItem? _menuOptionsHDD;
        private Dictionary<HashType, ToolStripMenuItem> _algoMenuItems = new Dictionary<HashType, ToolStripMenuItem>();

        // Constants & Colors
        private const long LargeFileThreshold = 1024L * 1024 * 1024; // 1 GB
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

            _settings = new AppSettings(Application.ExecutablePath);
            _settings.Load();

            // Virtual Mode Setup
            this.lvFiles.VirtualMode = true;
            this.lvFiles.RetrieveVirtualItem += LvFiles_RetrieveVirtualItem;
            EnableDoubleBuffer(this.lvFiles);

            // Events
            this.lvFiles.ColumnClick += LvFiles_ColumnClick;
            this.FormClosing += Form1_FormClosing;
            this.Shown += Form1_Shown;

            // Layout Initialization (See Form1.UI.cs & Form1.Menus.cs)
            SetupCustomMenu();
            SetupContextMenu();
            SetupStatsPanel();
            SetupFilterPanel();
            SetupDragDrop();

            ApplySettings();
            SetupLayout();

            this.Text = "SharpSFV";
        }

        // Reflection hack for smooth scrolling
        private void EnableDoubleBuffer(Control control)
        {
            typeof(Control).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, control, new object[] { true });
        }

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
    }
}