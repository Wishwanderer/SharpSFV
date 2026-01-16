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
        private ToolStripMenuItem? _menuJobsEnable;

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
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Copy Details", null, (s, e) => PerformCopyAction()) { ShortcutKeys = Keys.Control | Keys.C });
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Paste Paths", null, (s, e) => PerformPasteAction()) { ShortcutKeys = Keys.Control | Keys.V });
            menuEdit.DropDownItems.Add(new ToolStripSeparator());
            menuEdit.DropDownItems.Add(new ToolStripMenuItem("Select All", null, (s, e) => PerformSelectAllAction()) { ShortcutKeys = Keys.Control | Keys.A });

            // --- JOBS MENU ---
            var menuJobs = new ToolStripMenuItem("Jobs");
            _menuJobsEnable = new ToolStripMenuItem("Enable Job Queue", null, (s, e) => SetAppMode(!_isJobMode)) { CheckOnClick = true };
            var menuJobsClear = new ToolStripMenuItem("Clear Completed Jobs", null, (s, e) => PerformClearCompletedJobs());

            menuJobs.DropDownItems.Add(_menuJobsEnable);
            menuJobs.DropDownItems.Add(new ToolStripSeparator());
            menuJobs.DropDownItems.Add(menuJobsClear);

            // --- VIEW MENU ---
            var menuView = new ToolStripMenuItem("View");

            // FIX: Check for Job Mode to prevent column reset
            _menuViewHash = new ToolStripMenuItem("Show Hash", null, (s, e) => {
                _settings.ShowHashCol = !_settings.ShowHashCol;
                if (_isJobMode) lvFiles.Invalidate();
                else SetupUIForMode(_isVerificationMode ? "Verification" : "Creation");
            })
            { CheckOnClick = true, Checked = true };

            // FIX: Check for Job Mode to prevent column reset
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
            _menuViewShowFullPaths = new ToolStripMenuItem("Show Full File Paths", null, (s, e) => ToggleShowFullPaths(true)) { CheckOnClick = true };

            menuView.DropDownItems.Add(_menuViewHash);
            menuView.DropDownItems.Add(_menuViewExpected);
            menuView.DropDownItems.Add(_menuViewTime);
            menuView.DropDownItems.Add(_menuViewShowFullPaths);
            menuView.DropDownItems.Add(new ToolStripSeparator());
            menuView.DropDownItems.Add(_menuViewLockCols);

            // --- OPTIONS MENU ---
            var menuOptions = new ToolStripMenuItem("Options");

            // Path Storage Submenu
            var menuPathStorage = new ToolStripMenuItem("Path Storage");
            _menuPathRelative = new ToolStripMenuItem("Relative Paths (Default)", null, (s, e) => SetPathStorageMode(PathStorageMode.Relative));
            _menuPathAbsolute = new ToolStripMenuItem("Absolute Paths", null, (s, e) => SetPathStorageMode(PathStorageMode.Absolute));
            menuPathStorage.DropDownItems.AddRange(new ToolStripItem[] { _menuPathRelative, _menuPathAbsolute });

            // Advanced Bar Toggle
            _menuOptionsAdvanced = new ToolStripMenuItem("Show Advanced Options Bar", null, (s, e) => ToggleAdvancedBar()) { CheckOnClick = true };

            _menuOptionsFilter = new ToolStripMenuItem("Show Search/Filter Bar", null, (s, e) => ToggleFilterPanel()) { CheckOnClick = true };

            // Processing Mode Submenu
            var menuProcMode = new ToolStripMenuItem("Processing Mode");
            _menuProcAuto = new ToolStripMenuItem("Automatic Detection (Default)", null, (s, e) => SetProcessingMode(ProcessingMode.Auto));
            _menuProcHDD = new ToolStripMenuItem("HDD Mode (Sequential)", null, (s, e) => SetProcessingMode(ProcessingMode.HDD));
            _menuProcSSD = new ToolStripMenuItem("SSD Mode (Parallel)", null, (s, e) => SetProcessingMode(ProcessingMode.SSD));
            menuProcMode.DropDownItems.AddRange(new ToolStripItem[] { _menuProcAuto, _menuProcHDD, _menuProcSSD });

            _menuGenCopyDups = new ToolStripMenuItem("Generate Copy Duplicates Script", null, (s, e) => PerformGenerateDupCopyScript()) { Enabled = false };
            _menuGenDelDups = new ToolStripMenuItem("Generate Delete Duplicates Script", null, (s, e) => PerformGenerateDupDeleteScript()) { Enabled = false };
            _menuGenBadFiles = new ToolStripMenuItem("Generate 'Delete BAD Files' Script", null, (s, e) => PerformBatchExport()) { Enabled = false };

            var menuAlgo = new ToolStripMenuItem("Default Hashing Algorithm");
            AddAlgoMenuItem(menuAlgo, "xxHash-3 (128-bit)", HashType.XXHASH3);
            AddAlgoMenuItem(menuAlgo, "Crc32 (SFV)", HashType.Crc32);
            AddAlgoMenuItem(menuAlgo, "MD5", HashType.MD5);
            AddAlgoMenuItem(menuAlgo, "SHA-1", HashType.SHA1);
            AddAlgoMenuItem(menuAlgo, "SHA-256", HashType.SHA256);

            menuOptions.DropDownItems.AddRange(new ToolStripItem[] {
                menuPathStorage,
                _menuOptionsAdvanced,
                _menuOptionsFilter,
                menuProcMode,
                menuAlgo,
                new ToolStripSeparator(),
                _menuGenBadFiles,
                _menuGenCopyDups,
                _menuGenDelDups
            });

            // --- HELP MENU ---
            var menuHelp = new ToolStripMenuItem("Help");
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("View current Disk Data", null, (s, e) => ShowDiskDebugInfo()));
            menuHelp.DropDownItems.Add(new ToolStripSeparator());
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("Reset to Default Values", null, (s, e) => PerformResetDefaults()));
            menuHelp.DropDownItems.Add(new ToolStripSeparator());
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("Credits", null, (s, e) => ShowCredits()));

            _menuStrip.Items.AddRange(new ToolStripItem[] { menuFile, menuEdit, menuJobs, menuView, menuOptions, menuHelp });
        }

        private void SetPathStorageMode(PathStorageMode mode)
        {
            _settings.PathStorageMode = mode;
            if (_menuPathRelative != null) _menuPathRelative.Checked = (mode == PathStorageMode.Relative);
            if (_menuPathAbsolute != null) _menuPathAbsolute.Checked = (mode == PathStorageMode.Absolute);
        }

        private void SetProcessingMode(ProcessingMode mode)
        {
            _settings.ProcessingMode = mode;
            if (_menuProcAuto != null) _menuProcAuto.Checked = (mode == ProcessingMode.Auto);
            if (_menuProcHDD != null) _menuProcHDD.Checked = (mode == ProcessingMode.HDD);
            if (_menuProcSSD != null) _menuProcSSD.Checked = (mode == ProcessingMode.SSD);
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
                if (_isJobMode) { e.Cancel = true; return; }

                if (lvFiles.SelectedIndices.Count == 0) { e.Cancel = true; return; }
                bool singleSel = lvFiles.SelectedIndices.Count == 1;
                bool fileExists = false;

                if (singleSel)
                {
                    int storeIdx = _displayIndices[lvFiles.SelectedIndices[0]];
                    string fullPath = _fileStore.GetFullPath(storeIdx);
                    if (!string.IsNullOrEmpty(fullPath))
                        fileExists = File.Exists(fullPath);
                }

                itemOpen.Enabled = singleSel;
                itemRename.Enabled = singleSel && fileExists;
                itemDelete.Enabled = singleSel && fileExists;
                itemCopyPath.Enabled = singleSel;
                itemCopyHash.Enabled = singleSel;
            };
            lvFiles.ContextMenuStrip = _ctxMenu;
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
            if (!_isProcessing)
            {
                if (_isJobMode)
                {
                    this.Text = $"SharpSFV - Job Queue [{_currentHashType}]";
                }
                else if (_isVerificationMode)
                {
                    this.Text = $"SharpSFV - Verify [{_currentHashType}]";
                }
                else
                {
                    this.Text = $"SharpSFV [{_currentHashType}]";
                }
            }
        }

        private void ToggleTimeColumn()
        {
            _settings.ShowTimeTab = _menuViewTime?.Checked ?? false;
            // FIX: Prevent UI Setup in Job Mode
            if (!_isJobMode) SetupUIForMode(_isVerificationMode ? "Verification" : "Creation");
        }

        private void ToggleFilterPanel()
        {
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;
            if (_filterPanel != null && !_isJobMode) _filterPanel.Visible = _settings.ShowFilterPanel;
        }
    }
}