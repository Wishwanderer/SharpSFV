using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;

namespace SharpSFV
{
    public partial class Form1
    {
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

            // --- VIEW MENU ---
            var menuView = new ToolStripMenuItem("View");
            _menuViewHash = new ToolStripMenuItem("Show Hash", null, (s, e) => { _settings.ShowHashCol = !_settings.ShowHashCol; SetupUIForMode(_isVerificationMode ? "Verification" : "Creation"); }) { CheckOnClick = true, Checked = true };
            _menuViewExpected = new ToolStripMenuItem("Show Expected Hash", null, (s, e) => { _settings.ShowExpectedHashCol = !_settings.ShowExpectedHashCol; SetupUIForMode(_isVerificationMode ? "Verification" : "Creation"); }) { CheckOnClick = true, Checked = true };
            _menuViewLockCols = new ToolStripMenuItem("Lock Column Order", null, (s, e) => { _settings.LockColumns = !_settings.LockColumns; lvFiles.AllowColumnReorder = !_settings.LockColumns; }) { CheckOnClick = true, Checked = true };

            menuView.DropDownItems.Add(_menuViewHash);
            menuView.DropDownItems.Add(_menuViewExpected);
            menuView.DropDownItems.Add(new ToolStripSeparator());
            menuView.DropDownItems.Add(_menuViewLockCols);

            // --- OPTIONS MENU ---
            var menuOptions = new ToolStripMenuItem("Options");
            _menuOptionsTime = new ToolStripMenuItem("Enable Time Elapsed Tab", null, (s, e) => ToggleTimeColumn()) { CheckOnClick = true };
            _menuOptionsAbsolutePaths = new ToolStripMenuItem("Always Save Absolute Paths", null, (s, e) => { _settings.UseAbsolutePaths = !_settings.UseAbsolutePaths; }) { CheckOnClick = true };
            _menuOptionsFilter = new ToolStripMenuItem("Show Search/Filter Bar", null, (s, e) => ToggleFilterPanel()) { CheckOnClick = true };
            _menuOptionsHDD = new ToolStripMenuItem("Force HDD Mode (Always Sequential)", null, (s, e) => { _settings.OptimizeForHDD = !_settings.OptimizeForHDD; }) { CheckOnClick = true };
            _menuOptionsShowFullPaths = new ToolStripMenuItem("Show Full File Paths", null, (s, e) => ToggleShowFullPaths(true)) { CheckOnClick = true };

            // Script Menus
            _menuGenCopyDups = new ToolStripMenuItem("Generate Copy Duplicates Script", null, (s, e) => PerformGenerateDupCopyScript()) { Enabled = false };
            _menuGenDelDups = new ToolStripMenuItem("Generate Delete Duplicates Script", null, (s, e) => PerformGenerateDupDeleteScript()) { Enabled = false };
            _menuGenBadFiles = new ToolStripMenuItem("Generate 'Delete BAD Files' Script", null, (s, e) => PerformBatchExport()) { Enabled = false };

            // Algo Submenu
            var menuAlgo = new ToolStripMenuItem("Default Hashing Algorithm");
            AddAlgoMenuItem(menuAlgo, "xxHash-3 (128-bit)", HashType.XxHash3);
            AddAlgoMenuItem(menuAlgo, "CRC-32 (SFV)", HashType.Crc32);
            AddAlgoMenuItem(menuAlgo, "MD5", HashType.MD5);
            AddAlgoMenuItem(menuAlgo, "SHA-1", HashType.SHA1);
            AddAlgoMenuItem(menuAlgo, "SHA-256", HashType.SHA256);

            menuOptions.DropDownItems.AddRange(new ToolStripItem[] {
                _menuOptionsTime,
                _menuOptionsAbsolutePaths,
                _menuOptionsShowFullPaths,
                _menuOptionsFilter,
                _menuOptionsHDD,
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
            menuHelp.DropDownItems.Add(new ToolStripMenuItem("Credits", null, (s, e) => ShowCredits()));

            _menuStrip.Items.AddRange(new ToolStripItem[] { menuFile, menuEdit, menuView, menuOptions, menuHelp });
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
                    // UPDATED: Use DisplayIndices and FileStore
                    int storeIdx = _displayIndices[lvFiles.SelectedIndices[0]];
                    // Check file existence (Construct path on demand)
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

        // Toggles
        private void SetAlgorithm(HashType type)
        {
            _currentHashType = type;
            foreach (var kvp in _algoMenuItems) kvp.Value.Checked = (kvp.Key == type);
            if (!_isProcessing) this.Text = (_isVerificationMode) ? "SharpSFV - Verify" : $"SharpSFV - Create [{_currentHashType}]";
        }

        private void ToggleTimeColumn()
        {
            _settings.ShowTimeTab = _menuOptionsTime?.Checked ?? false;
            SetupUIForMode(_isVerificationMode ? "Verification" : "Creation");
        }

        private void ToggleFilterPanel()
        {
            _settings.ShowFilterPanel = _menuOptionsFilter?.Checked ?? false;
            if (_filterPanel != null) _filterPanel.Visible = _settings.ShowFilterPanel;
        }
    }
}