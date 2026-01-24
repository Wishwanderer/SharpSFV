using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using SharpSFV.Models;

namespace SharpSFV
{
    public partial class Form1
    {
        // Menu References
        private ToolStripMenuItem? _menuModeStandard;
        private ToolStripMenuItem? _menuModeJob;
        private ToolStripMenuItem? _menuViewThroughput;

        // Bad File Tool References
        private ToolStripMenuItem? _menuMoveBad;
        private ToolStripMenuItem? _menuRenameBad;

        private void SetupCustomMenu()
        {
            _menuStrip = new MenuStrip { Dock = DockStyle.Top };

            // --- FILE MENU ---
            var menuFile = new ToolStripMenuItem("File");
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Open...", null, (s, e) => PerformOpenAction()) { ShortcutKeys = Keys.Control | Keys.O });
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Save As...", null, (s, e) => PerformSaveAction()) { ShortcutKeys = Keys.Control | Keys.S });
            menuFile.DropDownItems.Add(new ToolStripSeparator());
            menuFile.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit()) { ShortcutKeys = Keys.Alt | Keys.F4 });

            // --- EDIT MENU ---
            var menuEdit = new ToolStripMenuItem("Edit");
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Copy Entry", null, (s, e) => PerformCopyAction()) { ShortcutKeys = Keys.Control | Keys.C });
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Paste Paths", null, (s, e) => PerformPasteAction()) { ShortcutKeys = Keys.Control | Keys.V });
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Compare with Clipboard", null, (s, e) => PerformCompareClipboard()));
            menuEdit.DropDownItems.Add(new ToolStripSeparator());
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Select All", null, (s, e) => PerformSelectAllAction()) { ShortcutKeys = Keys.Control | Keys.A });

            // --- MODE MENU ---
            var menuMode = new ToolStripMenuItem("Mode");
            _menuModeStandard = new ToolStripMenuItem("Standard Mode", null, (s, e) => SetAppMode(false));
            _menuModeJob = new ToolStripMenuItem("Job Queue Mode", null, (s, e) => SetAppMode(true));
            _menuModeStandard.Checked = !_isJobMode;
            _menuModeJob.Checked = _isJobMode;
            menuMode.DropDownItems.AddRange(new ToolStripItem[] { _menuModeStandard, _menuModeJob });

            // --- VIEW MENU ---
            var menuView = new ToolStripMenuItem("View");
            _menuViewHash = new ToolStripMenuItem("Show Hash", null, (s, e) => {
                _settings.ShowHashCol = !_settings.ShowHashCol;
                if (_isJobMode) lvFiles.Invalidate();
                else SetupUIForMode(_isVerificationMode ? "Verification" : "Creation");
            })
            { CheckOnClick = true, Checked = true };

            _menuViewExpected = new ToolStripMenuItem("Show Expected Hash", null, (s, e) => {
                _settings.ShowExpectedHashCol = !_settings.ShowExpectedHashCol;
                if (_isJobMode) lvFiles.Invalidate();
                else SetupUIForMode(_isVerificationMode ? "Verification" : "Creation");
            })
            { CheckOnClick = true, Checked = true };

            _menuViewLockCols = new ToolStripMenuItem("Lock Column Order", null, (s, e) => {
                _settings.LockColumns = !_settings.LockColumns;
                lvFiles.AllowColumnReorder = !_settings.LockColumns;
            })
            { CheckOnClick = true, Checked = true };

            _menuViewTime = new ToolStripMenuItem("Show Time Elapsed Tab", null, (s, e) => ToggleTimeColumn()) { CheckOnClick = true };

            _menuViewThroughput = new ToolStripMenuItem("Show Throughput && ETA", null, (s, e) => {
                _settings.ShowThroughputStats = !_settings.ShowThroughputStats;
                if (_isJobMode) UpdateJobStats(); else UpdateStats(0, 0, 0, 0, 0);
            })
            { CheckOnClick = true, Checked = _settings.ShowThroughputStats };

            _menuViewShowFullPaths = new ToolStripMenuItem("Show Full File Paths", null, (s, e) => ToggleShowFullPaths(true)) { CheckOnClick = true };

            menuView.DropDownItems.Add(_menuViewHash);
            menuView.DropDownItems.Add(_menuViewExpected);
            menuView.DropDownItems.Add(_menuViewTime);
            menuView.DropDownItems.Add(_menuViewThroughput);
            menuView.DropDownItems.Add(_menuViewShowFullPaths);
            menuView.DropDownItems.Add(new ToolStripSeparator());
            menuView.DropDownItems.Add(_menuViewLockCols);

            // --- OPTIONS MENU ---
            var menuOptions = new ToolStripMenuItem("Options");

            var menuPathStorage = new ToolStripMenuItem("Path Storage");
            _menuPathRelative = new ToolStripMenuItem("Relative Paths (Default)", null, (s, e) => SetPathStorageMode(PathStorageMode.Relative));
            _menuPathAbsolute = new ToolStripMenuItem("Absolute Paths", null, (s, e) => SetPathStorageMode(PathStorageMode.Absolute));
            menuPathStorage.DropDownItems.AddRange(new ToolStripItem[] { _menuPathRelative, _menuPathAbsolute });

            _menuOptionsAdvanced = new ToolStripMenuItem("Show Advanced Options Bar", null, (s, e) => ToggleAdvancedBar()) { CheckOnClick = true };
            _menuOptionsFilter = new ToolStripMenuItem("Show Search/Filter Bar", null, (s, e) => ToggleFilterPanel()) { CheckOnClick = true };

            var menuProcMode = new ToolStripMenuItem("Processing Mode");
            _menuProcAuto = new ToolStripMenuItem("Automatic Detection (Default)", null, (s, e) => SetProcessingMode(ProcessingMode.Auto));
            _menuProcHDD = new ToolStripMenuItem("HDD Mode (Sequential)", null, (s, e) => SetProcessingMode(ProcessingMode.HDD));
            _menuProcSSD = new ToolStripMenuItem("SSD Mode (Parallel)", null, (s, e) => SetProcessingMode(ProcessingMode.SSD));
            menuProcMode.DropDownItems.AddRange(new ToolStripItem[] { _menuProcAuto, _menuProcHDD, _menuProcSSD });

            var menuAlgo = new ToolStripMenuItem("Default Hashing Algorithm");
            AddAlgoMenuItem(menuAlgo, "xxHash-3 (128-bit)", HashType.XXHASH3);
            AddAlgoMenuItem(menuAlgo, "Crc32 (SFV)", HashType.Crc32);
            AddAlgoMenuItem(menuAlgo, "MD5", HashType.MD5);
            AddAlgoMenuItem(menuAlgo, "SHA-1", HashType.SHA1);
            AddAlgoMenuItem(menuAlgo, "SHA-256", HashType.SHA256);

            var menuSystem = new ToolStripMenuItem("System Integration");
            menuSystem.DropDownItems.Add(new ToolStripMenuItem("Register Explorer Context Menu", null, (s, e) => PerformRegisterShell()));
            menuSystem.DropDownItems.Add(new ToolStripMenuItem("Unregister Explorer Context Menu", null, (s, e) => PerformUnregisterShell()));

            var menuBadTools = new ToolStripMenuItem("Bad File Tools");
            _menuMoveBad = new ToolStripMenuItem("Move Bad Files to '_BAD_FILES'...", null, (s, e) => PerformMoveBadFiles()) { Enabled = false };
            _menuRenameBad = new ToolStripMenuItem("Rename Bad Files (*.CORRUPT)...", null, (s, e) => PerformRenameBadFiles()) { Enabled = false };
            menuBadTools.DropDownItems.Add(_menuMoveBad);
            menuBadTools.DropDownItems.Add(_menuRenameBad);
            menuBadTools.DropDownItems.Add(new ToolStripSeparator());
            _menuGenBadFiles = new ToolStripMenuItem("Generate 'Delete BAD Files' Script", null, (s, e) => PerformBatchExport()) { Enabled = false };
            menuBadTools.DropDownItems.Add(_menuGenBadFiles);

            var menuDupTools = new ToolStripMenuItem("Duplicate Tools");
            _menuGenCopyDups = new ToolStripMenuItem("Generate Copy Duplicates Script", null, (s, e) => PerformGenerateDupCopyScript()) { Enabled = false };
            _menuGenDelDups = new ToolStripMenuItem("Generate Delete Duplicates Script", null, (s, e) => PerformGenerateDupDeleteScript()) { Enabled = false };
            menuDupTools.DropDownItems.AddRange(new ToolStripItem[] { _menuGenCopyDups, _menuGenDelDups });

            menuOptions.DropDownItems.AddRange(new ToolStripItem[] {
                menuPathStorage,
                _menuOptionsAdvanced,
                _menuOptionsFilter,
                menuProcMode,
                menuAlgo,
                new ToolStripSeparator(),
                menuSystem,
                menuBadTools,
                menuDupTools
            });

            // --- HELP MENU ---
            var menuHelp = new ToolStripMenuItem("Help");
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("View current Disk Data", null, (s, e) => ShowDiskDebugInfo()));
            menuHelp.DropDownItems.Add(new ToolStripSeparator());
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("Reset to Default Values", null, (s, e) => PerformResetDefaults()));
            menuHelp.DropDownItems.Add(new ToolStripSeparator());
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("Credits", null, (s, e) => ShowCredits()));

            _menuStrip.Items.AddRange(new ToolStripItem[] { menuFile, menuEdit, menuMode, menuView, menuOptions, menuHelp });
        }

        private void AddAlgoMenuItem(ToolStripMenuItem parent, string text, HashType type)
        {
            var item = new ToolStripMenuItem(text, null, (s, e) => SetAlgorithm(type));
            parent.DropDownItems.Add(item);
            _algoMenuItems[type] = item;
        }

        private void SetupContextMenu()
        {
            _ctxMenu = new ContextMenuStrip();
            var itemOpen = new ToolStripMenuItem("Open Containing Folder", null, CtxOpenFolder_Click);
            var itemCopyEntry = new ToolStripMenuItem("Copy Entry", null, (s, e) => PerformCopyAction());
            var itemRename = new ToolStripMenuItem("Rename File...", null, CtxRename_Click);
            var itemDelete = new ToolStripMenuItem("Delete File", null, CtxDelete_Click);
            var itemRemove = new ToolStripMenuItem("Remove from List", null, CtxRemoveList_Click);

            _ctxMenu.Items.AddRange(new ToolStripItem[] {
                itemOpen,
                new ToolStripSeparator(),
                itemCopyEntry,
                new ToolStripSeparator(),
                itemRename,
                itemDelete,
                itemRemove
            });

            _ctxMenu.Opening += (s, e) =>
            {
                if (_isJobMode) { e.Cancel = true; return; }
                if (lvFiles.SelectedIndices.Count == 0) { e.Cancel = true; return; }

                int selCount = lvFiles.SelectedIndices.Count;
                bool singleSel = selCount == 1;
                bool fileExists = false;

                if (singleSel)
                {
                    int storeIdx = _displayIndices[lvFiles.SelectedIndices[0]];
                    string fullPath = _fileStore.GetFullPath(storeIdx);
                    if (!string.IsNullOrEmpty(fullPath)) fileExists = File.Exists(fullPath);
                }

                // Dynamic Text for Multi-Select
                itemCopyEntry.Text = singleSel ? "Copy Entry" : "Copy Entries";
                itemDelete.Text = singleSel ? "Delete File" : "Delete Files";

                // Single Select Actions
                itemOpen.Enabled = singleSel;
                itemRename.Enabled = singleSel && fileExists;

                // Multi Select Actions
                itemCopyEntry.Enabled = selCount > 0;
                itemDelete.Enabled = selCount > 0;
                itemRemove.Enabled = selCount > 0;
            };
            lvFiles.ContextMenuStrip = _ctxMenu;
        }
    }
}