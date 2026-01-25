using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpSFV.Models;
using SharpSFV.Interop;

namespace SharpSFV
{
    public partial class Form1
    {
        /// <summary>
        /// The Virtual Mode Renderer.
        /// <para>
        /// <b>Architecture:</b>
        /// WinForms <see cref="ListView"/> in Virtual Mode does not store data. 
        /// It requests data on-demand via the <see cref="RetrieveVirtualItem"/> event.
        /// This method acts as the translation layer between the raw SoA data (<see cref="FileStore"/>)
        /// and the Visual Elements (<see cref="ListViewItem"/>).
        /// </para>
        /// </summary>
        private void LvFiles_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
        {
            try
            {
                if (_isJobMode)
                {
                    // --- JOB MODE RENDER ---
                    // Direct mapping: index -> JobStore arrays.
                    // Safety check: Return placeholder if index is out of bounds (race condition safety).
                    if (e.ItemIndex < 0 || e.ItemIndex >= _jobStore.Count)
                    {
                        e.Item = new ListViewItem("Loading...");
                        return;
                    }

                    int idx = e.ItemIndex;

                    // Column 0: Job Name
                    var item = new ListViewItem(_jobStore.Names[idx]);

                    // Column 1: Root Path
                    item.SubItems.Add(_jobStore.RootPaths[idx]);

                    // Column 2: Progress
                    JobStatus status = _jobStore.Statuses[idx];
                    if (status == JobStatus.InProgress)
                        item.SubItems.Add($"{_jobStore.Progress[idx]:F1}%");
                    else if (status == JobStatus.Paused)
                        item.SubItems.Add($"{_jobStore.Progress[idx]:F1}% (Paused)");
                    else if (status == JobStatus.Queued)
                        item.SubItems.Add("Pending");
                    else
                        item.SubItems.Add("100%");

                    // Column 3: Status
                    string statusStr = status switch
                    {
                        JobStatus.Done => "DONE",
                        JobStatus.InProgress => "IN PROGRESS",
                        JobStatus.Paused => "PAUSED",
                        JobStatus.Error => "ERROR",
                        _ => "QUEUED"
                    };
                    item.SubItems.Add(statusStr);

                    // Column 4: Time
                    item.SubItems.Add(_jobStore.TimeStrs[idx]);

                    // Conditional Formatting (Color Coding)
                    switch (status)
                    {
                        case JobStatus.Done:
                            item.ForeColor = ColGreenText;
                            item.BackColor = ColGreenBack;
                            break;
                        case JobStatus.Error:
                            item.ForeColor = ColRedText;
                            item.BackColor = ColRedBack;
                            break;
                        case JobStatus.InProgress:
                            item.BackColor = Color.AliceBlue;
                            break;
                        case JobStatus.Paused:
                            item.ForeColor = ColYellowText;
                            item.BackColor = ColYellowBack;
                            break;
                    }

                    e.Item = item;
                }
                else
                {
                    // --- STANDARD MODE RENDER ---
                    // Indirection mapping: View Index -> Display List -> Store Index.
                    // This allows sorting/filtering without modifying the underlying FileStore.
                    if (e.ItemIndex < 0 || e.ItemIndex >= _displayIndices.Count)
                    {
                        e.Item = new ListViewItem("Loading...");
                        return;
                    }

                    int storeIdx = _displayIndices[e.ItemIndex];

                    string displayName = _settings.ShowFullPaths
                        ? _fileStore.GetFullPath(storeIdx)
                        : (_fileStore.FileNames[storeIdx] ?? "");

                    var item = new ListViewItem(displayName);

                    // Conditionally add sub-items based on View settings to avoid overhead
                    if (_settings.ShowHashCol)
                        item.SubItems.Add(_fileStore.GetCalculatedHashString(storeIdx));

                    var status = _fileStore.Statuses[storeIdx];
                    item.SubItems.Add(status.ToString());

                    if (_isVerificationMode && _settings.ShowExpectedHashCol)
                        item.SubItems.Add(_fileStore.GetExpectedHashString(storeIdx));

                    if (_settings.ShowTimeTab)
                        item.SubItems.Add(_fileStore.TimeStrs[storeIdx]);

                    // Visual Feedback Logic
                    if (status == ItemStatus.Error || status == ItemStatus.Bad)
                    {
                        item.ForeColor = ColRedText;
                        item.BackColor = ColRedBack;
                    }
                    else if (status == ItemStatus.OK)
                    {
                        if (_fileStore.IsSummaryRows[storeIdx]) item.ForeColor = Color.Blue;
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

                    if (_fileStore.IsSummaryRows[storeIdx]) item.Font = _fontBold;

                    e.Item = item;
                }
            }
            catch
            {
                // Absolute fallback to prevent app crash during high-speed updates
                e.Item = new ListViewItem("Error");
            }
        }

        /// <summary>
        /// Handles column header clicks to trigger sorting.
        /// </summary>
        private void LvFiles_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            // Disable sorting while active to prevent index misalignment
            if (_isProcessing || _isJobMode) return;

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

            // Optimization:
            // Sorting List<int> of pointers is extremely fast (even for 500k items, <100ms).
            // Done on UI thread to ensure atomicity with RetrieveVirtualItem.
            try
            {
                _displayIndices.Sort(_listSorter);
                lvFiles.Invalidate(); // Trigger repaint
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void UpdateSortVisuals(int column, SortOrder order)
        {
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                // Strip existing arrows
                if (ch.Text.EndsWith(" ▲") || ch.Text.EndsWith(" ▼"))
                    ch.Text = ch.Text.Substring(0, ch.Text.Length - 2);

                // Add arrow to current column
                if (ch.Index == column)
                    ch.Text += (order == SortOrder.Ascending) ? " ▲" : " ▼";
            }
        }

        /// <summary>
        /// Synchronizes the VirtualListSize with the underlying data count.
        /// Uses <see cref="Win32Storage.SuspendDrawing"/> to prevent flickering during the update.
        /// </summary>
        private void UpdateDisplayList()
        {
            Win32Storage.SuspendDrawing(lvFiles);
            try
            {
                if (!_isJobMode)
                {
                    if (lvFiles.VirtualListSize != _displayIndices.Count)
                        lvFiles.VirtualListSize = _displayIndices.Count;
                }
                else
                {
                    if (lvFiles.VirtualListSize != _jobStore.Count)
                        lvFiles.VirtualListSize = _jobStore.Count;
                }
                lvFiles.Invalidate();
            }
            finally { Win32Storage.ResumeDrawing(lvFiles); }
        }

        /// <summary>
        /// Timer callback for the Filter input.
        /// Prevents filtering from running on every keystroke, which would freeze the UI on large datasets.
        /// </summary>
        private void OnFilterDebounce(object? state)
        {
            this.Invoke(new Action(() => ApplyFilter()));
        }

        /// <summary>
        /// Applies the text filter and status filter to the <see cref="_displayIndices"/>.
        /// Runs on a background thread to avoid blocking the UI, then marshals the result back.
        /// </summary>
        private void ApplyFilter()
        {
            if (_isJobMode) return;
            if (_txtFilter == null || _cmbStatusFilter == null) return;

            string searchText = _txtFilter.Text.Trim();
            string statusFilter = _cmbStatusFilter.SelectedItem?.ToString() ?? "All";
            bool showDuplicates = _chkShowDuplicates?.Checked ?? false;

            Task.Run(() =>
            {
                var filteredIndices = new List<int>(_fileStore.Count);

                if (showDuplicates)
                {
                    // 1. Duplicate Detection Logic
                    var validIndices = new List<int>();
                    for (int i = 0; i < _fileStore.Count; i++)
                    {
                        if (_fileStore.CalculatedHashes[i] != null) validIndices.Add(i);
                    }

                    // Group by Hash String
                    var groups = validIndices.GroupBy(i => _fileStore.GetCalculatedHashString(i));
                    foreach (var grp in groups)
                    {
                        if (grp.Count() > 1) filteredIndices.AddRange(grp);
                    }

                    // Sort duplicates by hash for easier viewing
                    filteredIndices.Sort((a, b) =>
                        string.Compare(_fileStore.GetCalculatedHashString(a),
                                     _fileStore.GetCalculatedHashString(b),
                                     StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // 2. Standard Search/Status Filtering
                    for (int i = 0; i < _fileStore.Count; i++)
                    {
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

                    // Re-apply sort order if active
                    if (_listSorter.SortColumn != -1) filteredIndices.Sort(_listSorter);
                }

                // UI Update
                this.Invoke(new Action(() =>
                {
                    _displayIndices = filteredIndices;

                    // Enable/Disable Duplicate Tools
                    if (_menuGenCopyDups != null) _menuGenCopyDups.Enabled = showDuplicates && filteredIndices.Count > 0;
                    if (_menuGenDelDups != null) _menuGenDelDups.Enabled = showDuplicates && filteredIndices.Count > 0;

                    UpdateDisplayList();
                }));
            });
        }

        /// <summary>
        /// Limits column resizing to reasonable bounds to prevent the UI from looking broken.
        /// </summary>
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

            // Special handling for the Name column which can expand significantly
            int nameColIndex = -1;
            foreach (ColumnHeader ch in lvFiles.Columns)
            {
                if (ch.Tag as string == "Name" || ch.Tag as string == "JobName") { nameColIndex = ch.Index; break; }
            }

            if (e.ColumnIndex == nameColIndex && _settings.ShowFullPaths && !_isJobMode)
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