using System;
using System.Drawing;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        private void SetupAdvancedPanel()
        {
            _advancedPanel = new Panel
            {
                Height = 35,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(240, 240, 245),
                Padding = new Padding(2),
                Visible = _settings.ShowAdvancedBar
            };

            // Initialize ToolTip with better visibility settings
            ToolTip tt = new ToolTip
            {
                AutoPopDelay = 15000, // Keep visible for 15 seconds
                InitialDelay = 500,
                ReshowDelay = 500,
                ShowAlways = true,
                IsBalloon = false
            };

            // 1. Path Prefix
            Label lblPrefix = new Label { Text = "Path Prefix:", AutoSize = true, Location = new Point(5, 10) };
            _txtPathPrefix = new TextBox { Width = 150, Location = new Point(75, 7), Text = _settings.PathPrefix };
            tt.SetToolTip(_txtPathPrefix, "Virtual folder structure to prepend to saved paths.\nExample: incoming\\iso\\");

            // 2. Include Pattern
            Label lblInc = new Label { Text = "Include:", AutoSize = true, Location = new Point(240, 10) };
            _txtInclude = new TextBox { Width = 80, Location = new Point(290, 7), Text = _settings.IncludePattern };

            // Detailed Tooltip for Include
            tt.SetToolTip(_txtInclude,
                "Only process files matching these patterns.\n" +
                "Separate multiple patterns with a semicolon (;).\n" +
                "Example: *.iso;*.mkv;*.rar");

            // 3. Exclude Pattern
            Label lblExc = new Label { Text = "Exclude:", AutoSize = true, Location = new Point(380, 10) };
            _txtExclude = new TextBox { Width = 80, Location = new Point(430, 7), Text = _settings.ExcludePattern };

            // Detailed Tooltip for Exclude
            tt.SetToolTip(_txtExclude,
                "Skip files matching these patterns.\n" +
                "Separate multiple patterns with a semicolon (;).\n" +
                "Example: *.txt;*.nfo;*.sfv");

            // 4. Recursive Toggle
            _chkRecursive = new CheckBox { Text = "Scan Subfolders", AutoSize = true, Location = new Point(530, 9), Checked = _settings.ScanRecursive };
            tt.SetToolTip(_chkRecursive, "If checked, all subdirectories will be scanned recursively.\nIf unchecked, only files in the root folder are processed.");

            _advancedPanel.Controls.AddRange(new Control[] {
                lblPrefix, _txtPathPrefix,
                lblInc, _txtInclude,
                lblExc, _txtExclude,
                _chkRecursive
            });
        }
    }
}