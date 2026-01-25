using System;
using System.Drawing;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        /// <summary>
        /// Initializes the SplitContainer and the Active Jobs ListView.
        /// <para>
        /// <b>UI Layout:</b>
        /// The Main Form is split horizontally. 
        /// Panel 1 (Top) contains the <see cref="_lvActiveJobs"/> (Hidden by default).
        /// Panel 2 (Bottom) contains the main <see cref="lvFiles"/>.
        /// </para>
        /// </summary>
        private void SetupActiveJobsPanel()
        {
            _mainSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 4,
                Panel1Collapsed = true // Hidden until needed
            };

            _lvActiveJobs = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Clickable,
                FullRowSelect = true,
                Scrollable = true,
                BackColor = Color.FromArgb(245, 245, 255) // Slight tint to distinguish from main list
            };

            _lvActiveJobs.Columns.Add("Name", 300);
            _lvActiveJobs.Columns.Add("Progress", 220);

            _lvActiveJobs.ColumnWidthChanging += LvActiveJobs_ColumnWidthChanging;

            _mainSplitter.Panel1.Controls.Add(_lvActiveJobs);
        }

        /// <summary>
        /// Dynamically shows or hides the Active Jobs panel.
        /// <para>
        /// <b>Thread Safety:</b> Checks <see cref="Control.InvokeRequired"/> to ensure layout changes 
        /// happen on the UI thread, as this is triggered by background worker threads.
        /// </para>
        /// </summary>
        /// <param name="show">True to slide the panel open, False to collapse it.</param>
        private void ToggleActiveJobsPanel(bool show)
        {
            if (_mainSplitter == null) return;
            if (this.InvokeRequired) { this.Invoke(new Action(() => ToggleActiveJobsPanel(show))); return; }

            if (_mainSplitter.Panel1Collapsed == show)
            {
                _mainSplitter.Panel1Collapsed = !show;

                if (show)
                {
                    // Restore previous splitter distance or calculate a sensible default (20% height)
                    int targetDistance;
                    if (_settings.SplitterDistance > 0)
                    {
                        targetDistance = _settings.SplitterDistance;
                    }
                    else
                    {
                        targetDistance = Math.Max(50, _mainSplitter.Height / 5);
                    }

                    if (targetDistance < _mainSplitter.Height && targetDistance > 0)
                    {
                        try { _mainSplitter.SplitterDistance = targetDistance; } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Adds a file to the Active Jobs view. 
        /// Called when the hashing engine encounters a file larger than <see cref="LargeFileThreshold"/>.
        /// </summary>
        /// <param name="index">The store index of the file (used as Tag).</param>
        /// <param name="fileName">The name to display.</param>
        private void AddActiveJob(int index, string fileName)
        {
            if (_lvActiveJobs == null) return;
            if (this.InvokeRequired) { this.Invoke(new Action(() => AddActiveJob(index, fileName))); return; }

            var item = new ListViewItem(fileName);
            item.SubItems.Add("Starting...");
            item.Tag = index; // Link UI item back to Store Index
            _lvActiveJobs.Items.Add(item);

            // Auto-expand if this is the first active job
            ToggleActiveJobsPanel(true);
        }

        /// <summary>
        /// Updates the percentage text for a specific active job.
        /// <para>
        /// <b>Performance:</b> Uses <c>BeginInvoke</c> to avoid blocking the hashing thread.
        /// Iterates only the few items in _lvActiveJobs (usually &lt; 16), so O(N) lookup is negligible.
        /// </para>
        /// </summary>
        private void UpdateActiveJob(int index, double percent)
        {
            if (_lvActiveJobs == null) return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateActiveJob(index, percent)));
                return;
            }

            foreach (ListViewItem item in _lvActiveJobs.Items)
            {
                if (item.Tag is int idx && idx == index)
                {
                    // Update Format: F1 (e.g., "12.5%")
                    item.SubItems[1].Text = $"{percent:F1}%";
                    return;
                }
            }
        }

        /// <summary>
        /// Removes a file from the Active Jobs view upon completion.
        /// If the list becomes empty, the panel is automatically collapsed.
        /// </summary>
        private void RemoveActiveJob(int index)
        {
            if (_lvActiveJobs == null) return;
            if (this.InvokeRequired) { this.Invoke(new Action(() => RemoveActiveJob(index))); return; }

            foreach (ListViewItem item in _lvActiveJobs.Items)
            {
                if (item.Tag is int idx && idx == index)
                {
                    _lvActiveJobs.Items.Remove(item);
                    break;
                }
            }

            if (_lvActiveJobs.Items.Count == 0) ToggleActiveJobsPanel(false);
        }

        private void LvActiveJobs_ColumnWidthChanging(object? sender, ColumnWidthChangingEventArgs e)
        {
            // Lock column width to prevent user tampering with the layout during processing
            e.Cancel = true;
            e.NewWidth = _lvActiveJobs!.Columns[e.ColumnIndex].Width;
        }
    }
}