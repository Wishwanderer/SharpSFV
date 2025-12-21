using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        private async void Form1_Shown(object? sender, EventArgs e)
        {
            await Task.Delay(100);
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) await HandleDroppedPaths(args.Skip(1).ToArray());
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

        private void LvFiles_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            if (_isProcessing) return;

            _listSorter.SortColumn = e.Column;
            _listSorter.Order = (_listSorter.Order == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;

            _displayList.Sort(_listSorter);

            lvFiles.Invalidate();
            UpdateSortVisuals(e.Column, _listSorter.Order);
        }

        private void UpdateSortVisuals(int column, SortOrder order)
        {
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Text.EndsWith(" ▲") || ch.Text.EndsWith(" ▼")) ch.Text = ch.Text.Substring(0, ch.Text.Length - 2);
                if (ch.Index == column) ch.Text += (order == SortOrder.Ascending) ? " ▲" : " ▼";
            }
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
                if (!paths.All(p =>
                {
                    var parts = p.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    return i < parts.Length && parts[i].Equals(currentSegment, StringComparison.OrdinalIgnoreCase);
                })) break;
                commonPath = (i == 0 && isRooted && currentSegment.Contains(":")) ? currentSegment + Path.DirectorySeparatorChar : Path.Combine(commonPath, currentSegment);
            }
            return commonPath.TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}