using System;
using System.Drawing;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        private void SetupActiveJobsPanel()
        {
            _mainSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 4,
                Panel1Collapsed = true
            };

            _lvActiveJobs = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Clickable,
                FullRowSelect = true,
                Scrollable = true,
                BackColor = Color.FromArgb(245, 245, 255)
            };

            _lvActiveJobs.Columns.Add("Name", 300);
            _lvActiveJobs.Columns.Add("Progress", 220);

            // Prevent manual resizing of the progress column for cleaner look
            _lvActiveJobs.ColumnWidthChanging += LvActiveJobs_ColumnWidthChanging;

            _mainSplitter.Panel1.Controls.Add(_lvActiveJobs);
        }

        private void ToggleActiveJobsPanel(bool show)
        {
            if (_mainSplitter == null) return;
            if (this.InvokeRequired) { this.Invoke(new Action(() => ToggleActiveJobsPanel(show))); return; }

            if (_mainSplitter.Panel1Collapsed == show)
            {
                _mainSplitter.Panel1Collapsed = !show;

                if (show)
                {
                    int targetDistance;
                    if (_settings.SplitterDistance > 0)
                    {
                        targetDistance = _settings.SplitterDistance;
                    }
                    else
                    {
                        // Default to 20% height or minimum 50px
                        targetDistance = Math.Max(50, _mainSplitter.Height / 5);
                    }

                    if (targetDistance < _mainSplitter.Height && targetDistance > 0)
                    {
                        try { _mainSplitter.SplitterDistance = targetDistance; } catch { }
                    }
                }
            }
        }

        // UPDATED: Accepts Index and Name (Primitive types) instead of Object
        private void AddActiveJob(int index, string fileName)
        {
            if (_lvActiveJobs == null) return;
            if (this.InvokeRequired) { this.Invoke(new Action(() => AddActiveJob(index, fileName))); return; }

            var item = new ListViewItem(fileName);
            item.SubItems.Add("Starting...");
            item.Tag = index; // Store Integer Index
            _lvActiveJobs.Items.Add(item);

            ToggleActiveJobsPanel(true);
        }

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
                // Unbox index
                if (item.Tag is int idx && idx == index)
                {
                    item.SubItems[1].Text = $"{percent:F1}%";
                    return;
                }
            }
        }

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
            e.Cancel = true;
            e.NewWidth = _lvActiveJobs!.Columns[e.ColumnIndex].Width;
        }
    }
}