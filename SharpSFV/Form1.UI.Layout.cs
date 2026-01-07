using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        private void SetupLayout()
        {
            this.Controls.Clear();

            // 1. Bottom Progress Bar
            Panel progressPanel = new Panel { Height = 25, Dock = DockStyle.Bottom, Padding = new Padding(2) };
            progressBarTotal.Dock = DockStyle.Fill;
            progressPanel.Controls.Add(progressBarTotal);
            this.Controls.Add(progressPanel);

            // 2. Main Splitter (Active Jobs vs File List)
            if (_mainSplitter != null)
            {
                if (!_mainSplitter.Panel2.Controls.Contains(lvFiles))
                    _mainSplitter.Panel2.Controls.Add(lvFiles);
                this.Controls.Add(_mainSplitter);
            }
            else
            {
                // Fallback if ActiveJobs failed to init
                this.Controls.Add(lvFiles);
            }

            // 3. Top Panels
            if (_filterPanel != null) this.Controls.Add(_filterPanel);
            if (_advancedPanel != null) this.Controls.Add(_advancedPanel);
            if (_statsPanel != null) this.Controls.Add(_statsPanel);
            if (_menuStrip != null) this.Controls.Add(_menuStrip);
        }

        private void SetupStatsPanel()
        {
            _statsPanel = new Panel { Height = 50, Dock = DockStyle.Top, BackColor = SystemColors.ControlLight, Padding = new Padding(10, 5, 10, 5) };

            _lblProgress = new Label { Text = "Ready", AutoSize = true, Location = new Point(10, 8) };
            _lblProgress.Font = _fontBold;

            _lblTotalTime = new Label
            {
                Text = "",
                AutoSize = true,
                Location = new Point(300, 8),
                ForeColor = Color.Blue,
                Font = _fontBold,
                Visible = _settings.ShowTimeTab
            };

            _statsFlowPanel = new FlowLayoutPanel
            {
                Location = new Point(10, 28),
                Size = new Size(600, 20),
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0)
            };

            _lblStatsOK = new Label { Text = "OK: 0", AutoSize = true, ForeColor = Color.DarkGreen, Font = new Font(this.Font, FontStyle.Bold), Margin = new Padding(0, 0, 15, 0) };
            _lblStatsBad = new Label { Text = "BAD: 0", AutoSize = true, ForeColor = Color.Red, Font = new Font(this.Font, FontStyle.Bold), Margin = new Padding(0, 0, 15, 0) };
            _lblStatsMissing = new Label { Text = "MISSING: 0", AutoSize = true, ForeColor = Color.DarkGoldenrod, Font = new Font(this.Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 0) };

            _statsFlowPanel.Controls.Add(_lblStatsOK);
            _statsFlowPanel.Controls.Add(_lblStatsBad);
            _statsFlowPanel.Controls.Add(_lblStatsMissing);

            _btnStop = new Button { Text = "Stop", Location = new Point(700, 10), Size = new Size(75, 30), BackColor = Color.IndianRed, ForeColor = Color.White, Enabled = false, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            _btnStop.FlatStyle = FlatStyle.Flat;
            _btnStop.Click += (s, e) => { _cts?.Cancel(); };

            _statsPanel.Controls.Add(_lblProgress);
            _statsPanel.Controls.Add(_lblTotalTime);
            _statsPanel.Controls.Add(_statsFlowPanel);
            _statsPanel.Controls.Add(_btnStop);
        }

        private void SetupFilterPanel()
        {
            _filterPanel = new Panel { Height = 35, Dock = DockStyle.Top, BackColor = SystemColors.Control, Padding = new Padding(5), Visible = false };

            Label lblSearch = new Label { Text = "Search:", AutoSize = true, Location = new Point(10, 8) };
            _txtFilter = new TextBox { Width = 200, Location = new Point(60, 5) };

            _txtFilter.TextChanged += (s, e) =>
            {
                _filterDebounceTimer?.Change(300, System.Threading.Timeout.Infinite);
            };

            Label lblStatus = new Label { Text = "Status:", AutoSize = true, Location = new Point(280, 8) };
            _cmbStatusFilter = new ComboBox { Width = 100, Location = new Point(330, 5), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbStatusFilter.Items.AddRange(new object[] { "All", "Pending", "OK", "BAD", "MISSING", "Waiting..." });
            _cmbStatusFilter.SelectedIndex = 0;
            _cmbStatusFilter.SelectedIndexChanged += (s, e) => ApplyFilter();

            _chkShowDuplicates = new CheckBox { Text = "Show Duplicates", AutoSize = true, Location = new Point(450, 8) };
            _chkShowDuplicates.CheckedChanged += (s, e) => ApplyFilter();

            _filterPanel.Controls.AddRange(new Control[] { lblSearch, _txtFilter, lblStatus, _cmbStatusFilter, _chkShowDuplicates });

            _filterDebounceTimer = new System.Threading.Timer(OnFilterDebounce, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        private void SetupUIForMode(string mode)
        {
            lvFiles.ColumnWidthChanging -= LvFiles_ColumnWidthChanging;

            lvFiles.Columns.Clear();
            _originalColWidths.Clear();
            _listSorter.SortColumn = -1;
            _listSorter.Order = SortOrder.None;

            _isVerificationMode = (mode == "Verification");

            lvFiles.AllowColumnReorder = !_settings.LockColumns;

            // Local helper to avoid conflict with class-level AddCol
            void AddColLocal(string text, int width, string tag)
            {
                ColumnHeader ch = lvFiles.Columns.Add(text, width);
                ch.Tag = tag;
                _originalColWidths[ch.Index] = width;
            }

            AddColLocal("File Name", 300, "Name");

            if (_settings.ShowHashCol)
                AddColLocal("Hash", 220, "Hash");

            AddColLocal("Status", 100, "Status");

            if (_isVerificationMode && _settings.ShowExpectedHashCol)
                AddColLocal("Expected Hash", 220, "Expected");

            if (_settings.ShowTimeTab)
                AddColLocal("Time", 80, "Time");

            if (_lblTotalTime != null)
            {
                _lblTotalTime.Visible = _settings.ShowTimeTab;
            }

            this.Text = (_isVerificationMode) ? "SharpSFV - Verify" : $"SharpSFV - Create [{_currentHashType}]";

            if (_settings.ColumnOrder.Count > 0)
            {
                foreach (ColumnHeader ch in lvFiles.Columns)
                {
                    if (ch.Tag is string tag && _settings.ColumnOrder.TryGetValue(tag, out int displayIdx))
                    {
                        if (displayIdx < lvFiles.Columns.Count)
                            ch.DisplayIndex = displayIdx;
                    }
                }
            }

            if (_lvActiveJobs != null)
            {
                if (_lvActiveJobs.Columns.Count > 0) _lvActiveJobs.Columns[0].Width = 300;
                if (_lvActiveJobs.Columns.Count > 1) _lvActiveJobs.Columns[1].Width = 220;
            }

            lvFiles.ColumnWidthChanging += LvFiles_ColumnWidthChanging;
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

            // FIXED: Using correct View Menu references
            if (_menuViewTime != null) _menuViewTime.Checked = _settings.ShowTimeTab;
            if (_menuViewShowFullPaths != null) _menuViewShowFullPaths.Checked = _settings.ShowFullPaths;

            if (_menuOptionsFilter != null) _menuOptionsFilter.Checked = _settings.ShowFilterPanel;

            // Apply Advanced Bar Settings
            if (_menuOptionsAdvanced != null) _menuOptionsAdvanced.Checked = _settings.ShowAdvancedBar;
            if (_advancedPanel != null) _advancedPanel.Visible = _settings.ShowAdvancedBar;

            SetProcessingMode(_settings.ProcessingMode);
            SetPathStorageMode(_settings.PathStorageMode);

            if (_menuViewHash != null) _menuViewHash.Checked = _settings.ShowHashCol;
            if (_menuViewExpected != null) _menuViewExpected.Checked = _settings.ShowExpectedHashCol;
            if (_menuViewLockCols != null) _menuViewLockCols.Checked = _settings.LockColumns;

            if (_filterPanel != null) _filterPanel.Visible = _settings.ShowFilterPanel;

            ToggleTimeColumn();
            SetAlgorithm(_settings.DefaultAlgo);
            ToggleShowFullPaths(false);
        }
    }
}