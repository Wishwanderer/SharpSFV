using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpSFV.Interop;

namespace SharpSFV
{
    public partial class Form1
    {
        private void LvFiles_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
        {
            // SoA: Map UI Index -> Store Index
            if (e.ItemIndex < 0 || e.ItemIndex >= _displayIndices.Count) return;
            int storeIdx = _displayIndices[e.ItemIndex];

            // Access Raw Arrays
            // Fix CS8600: Handle null filename
            string displayName = _settings.ShowFullPaths
                ? _fileStore.GetFullPath(storeIdx)
                : (_fileStore.FileNames[storeIdx] ?? "");

            var item = new ListViewItem(displayName);

            if (_settings.ShowHashCol)
                item.SubItems.Add(_fileStore.GetCalculatedHashString(storeIdx));

            // Status Enum
            var status = _fileStore.Statuses[storeIdx];
            item.SubItems.Add(status.ToString());

            if (_isVerificationMode && _settings.ShowExpectedHashCol)
                item.SubItems.Add(_fileStore.GetExpectedHashString(storeIdx));

            if (_settings.ShowTimeTab)
                item.SubItems.Add(_fileStore.TimeStrs[storeIdx]);

            // Visual Logic (Recalculated on fly to avoid storing Color objects)
            // This is cheap compared to object overhead
            if (status == ItemStatus.Error || status == ItemStatus.Bad)
            {
                item.ForeColor = ColRedText;
                item.BackColor = ColRedBack;
            }
            else if (status == ItemStatus.OK)
            {
                if (_fileStore.IsSummaryRows[storeIdx])
                {
                    item.ForeColor = Color.Blue;
                }
                else
                {
                    item.ForeColor = ColGreenText;
                    item.BackColor = ColGreenBack;
                }
            }
            else if (status == ItemStatus.Missing)
            {
                item.ForeColor = ColYellowText;
                item.BackColor = ColYellowBack;
                item.Font = _fontStrike;
            }

            if (_fileStore.IsSummaryRows[storeIdx])
                item.Font = _fontBold;

            e.Item = item;
        }

        private async void LvFiles_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            if (_isProcessing) return;

            if (e.Column != _listSorter.SortColumn)
            {
                _listSorter.SortColumn = e.Column;
                _listSorter.Order = SortOrder.Ascending;
            }
            else
            {
                _listSorter.Order = (_listSorter.Order == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;
            }

            UpdateSortVisuals(e.Column, _listSorter.Order);
            Cursor = Cursors.WaitCursor;

            // Sort the indices, not the data
            // Since _displayIndices is List<int> and _listSorter is IComparer<int>, this works efficiently
            await Task.Run(() => { _displayIndices.Sort(_listSorter); });

            lvFiles.Invalidate();
            Cursor = Cursors.Default;
        }

        private void UpdateSortVisuals(int column, SortOrder order)
        {
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Text.EndsWith(" ▲") || ch.Text.EndsWith(" ▼"))
                    ch.Text = ch.Text.Substring(0, ch.Text.Length - 2);

                if (ch.Index == column)
                    ch.Text += (order == SortOrder.Ascending) ? " ▲" : " ▼";
            }
        }

        private void OnFilterDebounce(object? state)
        {
            this.Invoke(new Action(() => ApplyFilter()));
        }

        private void ApplyFilter()
        {
            if (_txtFilter == null || _cmbStatusFilter == null) return;
            string searchText = _txtFilter.Text.Trim();
            string statusFilter = _cmbStatusFilter.SelectedItem?.ToString() ?? "All";
            bool showDuplicates = _chkShowDuplicates?.Checked ?? false;

            Task.Run(() =>
            {
                var filteredIndices = new List<int>(_fileStore.Count);

                if (showDuplicates)
                {
                    // Group by Hash
                    // We can just iterate indices where Hash is not null
                    var validIndices = new List<int>();
                    for (int i = 0; i < _fileStore.Count; i++)
                    {
                        if (_fileStore.CalculatedHashes[i] != null) validIndices.Add(i);
                    }

                    var groups = validIndices.GroupBy(i => _fileStore.GetCalculatedHashString(i));
                    foreach (var grp in groups)
                    {
                        if (grp.Count() > 1) filteredIndices.AddRange(grp);
                    }

                    // Sort dupes by Hash to group them visually
                    filteredIndices.Sort((a, b) =>
                        string.Compare(_fileStore.GetCalculatedHashString(a),
                                     _fileStore.GetCalculatedHashString(b),
                                     StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // Linear scan over arrays - extremely fast due to prefetching
                    for (int i = 0; i < _fileStore.Count; i++)
                    {
                        // Fix CS8600: Handle nullable name
                        string? name = _fileStore.FileNames[i];
                        bool matchName = string.IsNullOrEmpty(searchText) ||
                                       (name != null && name.Contains(searchText, StringComparison.OrdinalIgnoreCase));

                        bool matchStatus = statusFilter == "All";
                        if (!matchStatus)
                        {
                            ItemStatus s = _fileStore.Statuses[i];
                            if (statusFilter == "BAD")
                                matchStatus = s == ItemStatus.Bad || s == ItemStatus.Error || s == ItemStatus.Missing;
                            else
                                matchStatus = s.ToString().Equals(statusFilter, StringComparison.OrdinalIgnoreCase);
                        }

                        if (matchName && matchStatus) filteredIndices.Add(i);
                    }

                    if (_listSorter.SortColumn != -1) filteredIndices.Sort(_listSorter);
                }

                this.Invoke(new Action(() =>
                {
                    _displayIndices = filteredIndices;

                    if (_menuGenCopyDups != null) _menuGenCopyDups.Enabled = showDuplicates && filteredIndices.Count > 0;
                    if (_menuGenDelDups != null) _menuGenDelDups.Enabled = showDuplicates && filteredIndices.Count > 0;

                    UpdateDisplayList();
                }));
            });
        }

        private void UpdateDisplayList()
        {
            Win32Storage.SuspendDrawing(lvFiles);
            try
            {
                if (lvFiles.VirtualListSize != _displayIndices.Count)
                    lvFiles.VirtualListSize = _displayIndices.Count;
                lvFiles.Invalidate();
            }
            finally { Win32Storage.ResumeDrawing(lvFiles); }
        }

        private void LvFiles_ColumnWidthChanging(object? sender, ColumnWidthChangingEventArgs e)
        {
            int orig = 0;
            if (_originalColWidths.ContainsKey(e.ColumnIndex))
            {
                orig = _originalColWidths[e.ColumnIndex];
            }
            else
            {
                if (lvFiles.Columns.Count > e.ColumnIndex)
                {
                    orig = lvFiles.Columns[e.ColumnIndex].Width;
                    _originalColWidths[e.ColumnIndex] = orig;
                }
                else return;
            }

            int min = (int)(orig * 0.75);
            int max = (int)(orig * 1.5);

            int nameColIndex = -1;
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Tag as string == "Name") { nameColIndex = ch.Index; break; }
            }

            if (e.ColumnIndex == nameColIndex && _settings.ShowFullPaths)
            {
                max = Math.Max(min, _cachedFullPathWidth);
            }

            if (e.NewWidth < min)
            {
                e.NewWidth = min;
                e.Cancel = true;
            }
            else if (e.NewWidth > max)
            {
                e.NewWidth = max;
                e.Cancel = true;
            }
        }
    }
}