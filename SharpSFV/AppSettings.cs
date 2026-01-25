using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SharpSFV
{
    /// <summary>
    /// Manages application configuration and state persistence.
    /// <para>
    /// <b>Design Choice:</b> 
    /// Settings are stored in a simple "SharpSFV.ini" text file adjacent to the executable.
    /// This makes the application fully portable and ensures user preferences (window size, 
    /// column order, defaults) are preserved across version upgrades without relying on the Windows Registry.
    /// </para>
    /// </summary>
    public class AppSettings
    {
        private readonly string _iniPath;

        // --- UI Preferences ---
        public bool ShowTimeTab { get; set; } = false;
        public bool ShowFilterPanel { get; set; } = false;
        public bool ShowThroughputStats { get; set; } = false;
        public bool ShowAdvancedBar { get; set; } = false;
        public bool ShowFullPaths { get; set; } = false;
        public bool ShowHashCol { get; set; } = true;
        public bool ShowExpectedHashCol { get; set; } = true;
        public bool LockColumns { get; set; } = true;

        // --- Operation Defaults ---
        public PathStorageMode PathStorageMode { get; set; } = PathStorageMode.Relative;
        public ProcessingMode ProcessingMode { get; set; } = ProcessingMode.Auto;
        public bool EnableChecksumComments { get; set; } = false;
        public string PathPrefix { get; set; } = "";
        public string IncludePattern { get; set; } = "";
        public string ExcludePattern { get; set; } = "";
        public bool ScanRecursive { get; set; } = true;
        public string CustomSignature { get; set; } = "L33T";
        public HashType DefaultAlgo { get; set; } = HashType.XXHASH3;

        // --- Window Geometry ---
        public Size WindowSize { get; set; } = new Size(800, 600);
        public Point WindowLocation { get; set; } = Point.Empty;
        public bool HasCustomLocation { get; private set; } = false;
        public int SplitterDistance { get; set; } = -1;

        /// <summary>
        /// Maps Column Tags to their user-defined DisplayIndex.
        /// Example: "Name" -> 0, "Hash" -> 1.
        /// </summary>
        public Dictionary<string, int> ColumnOrder { get; set; } = new Dictionary<string, int>();

        public AppSettings(string appExecutablePath)
        {
            string exeDir = Path.GetDirectoryName(appExecutablePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            _iniPath = Path.Combine(exeDir, "SharpSFV.ini");
        }

        /// <summary>
        /// Parses the INI file line-by-line. 
        /// Fails silently on parse errors to ensure the app still launches with default settings.
        /// </summary>
        public void Load()
        {
            if (!File.Exists(_iniPath)) return;

            try
            {
                var lines = File.ReadAllLines(_iniPath);
                int w = 800, h = 600, x = 0, y = 0;
                bool foundX = false, foundY = false;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[")) continue;

                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    // UI Layout
                    if (key.Equals("TimeTab", StringComparison.OrdinalIgnoreCase)) ShowTimeTab = (val == "1");
                    else if (key.Equals("ShowThroughputStats", StringComparison.OrdinalIgnoreCase)) ShowThroughputStats = (val == "1");
                    else if (key.Equals("ShowFilterPanel", StringComparison.OrdinalIgnoreCase)) ShowFilterPanel = (val == "1");
                    else if (key.Equals("ShowAdvancedBar", StringComparison.OrdinalIgnoreCase)) ShowAdvancedBar = (val == "1");
                    else if (key.Equals("ShowFullPaths", StringComparison.OrdinalIgnoreCase)) ShowFullPaths = (val == "1");
                    else if (key.Equals("ShowHashCol", StringComparison.OrdinalIgnoreCase)) ShowHashCol = (val == "1");
                    else if (key.Equals("ShowExpectedHashCol", StringComparison.OrdinalIgnoreCase)) ShowExpectedHashCol = (val == "1");
                    else if (key.Equals("LockColumns", StringComparison.OrdinalIgnoreCase)) LockColumns = (val == "1");

                    // Modes
                    else if (key.Equals("PathStorageMode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<PathStorageMode>(val, true, out var result)) PathStorageMode = result;
                    }
                    else if (key.Equals("UseAbsolutePaths", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val == "1") PathStorageMode = PathStorageMode.Absolute; // Legacy support
                    }
                    else if (key.Equals("ProcessingMode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<ProcessingMode>(val, true, out var result)) ProcessingMode = result;
                    }
                    else if (key.Equals("OptimizeForHDD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val == "1") ProcessingMode = ProcessingMode.HDD; // Legacy support
                    }

                    // Advanced Options
                    else if (key.Equals("EnableChecksumComments", StringComparison.OrdinalIgnoreCase)) EnableChecksumComments = (val == "1");
                    else if (key.Equals("PathPrefix", StringComparison.OrdinalIgnoreCase)) PathPrefix = val;
                    else if (key.Equals("IncludePattern", StringComparison.OrdinalIgnoreCase)) IncludePattern = val;
                    else if (key.Equals("ExcludePattern", StringComparison.OrdinalIgnoreCase)) ExcludePattern = val;
                    else if (key.Equals("ScanRecursive", StringComparison.OrdinalIgnoreCase)) ScanRecursive = (val == "1");
                    else if (key.Equals("Signature", StringComparison.OrdinalIgnoreCase)) CustomSignature = val;

                    // ListView Customization
                    else if (key.Equals("ColumnOrder", StringComparison.OrdinalIgnoreCase)) ParseColumnOrder(val);

                    // Crypto Choice
                    else if (key.Equals("DefaultAlgo", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<HashType>(val, true, out var result)) DefaultAlgo = result;
                    }

                    // Window Geometry
                    else if (key.Equals("WindowSizeW", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out w);
                    else if (key.Equals("WindowSizeH", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out h);
                    else if (key.Equals("WindowPosX", StringComparison.OrdinalIgnoreCase)) { if (int.TryParse(val, out x)) foundX = true; }
                    else if (key.Equals("WindowPosY", StringComparison.OrdinalIgnoreCase)) { if (int.TryParse(val, out y)) foundY = true; }
                    else if (key.Equals("SplitterPos", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out int tempDist)) SplitterDistance = tempDist;
                    }
                }

                WindowSize = new Size(w, h);
                if (foundX && foundY)
                {
                    WindowLocation = new Point(x, y);
                    HasCustomLocation = true;
                }
            }
            catch { }
        }

        /// <summary>
        /// Parses the serialized Column Order string.
        /// Format: "Name:0,Hash:1,Status:2"
        /// </summary>
        private void ParseColumnOrder(string val)
        {
            try
            {
                ColumnOrder.Clear();
                var pairs = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in pairs)
                {
                    var kv = pair.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[1], out int idx))
                    {
                        ColumnOrder[kv[0]] = idx;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Serializes the current application state to the INI file.
        /// </summary>
        public void Save(Form form, bool isTimeTabEnabled, HashType currentAlgo, int currentSplitterDist, ListView lv)
        {
            try
            {
                // Prevent saving minimized window sizes
                Rectangle bounds = (form.WindowState == FormWindowState.Normal) ? form.Bounds : form.RestoreBounds;

                string colOrderStr = "";
                if (lv != null)
                {
                    var orderList = new List<string>();
                    foreach (ColumnHeader ch in lv.Columns)
                    {
                        if (ch.Tag is string tag)
                        {
                            orderList.Add($"{tag}:{ch.DisplayIndex}");
                        }
                    }
                    colOrderStr = string.Join(",", orderList);
                }

                using (StreamWriter sw = new StreamWriter(_iniPath))
                {
                    sw.WriteLine("[SharpSFV]");
                    sw.WriteLine($"TimeTab={(isTimeTabEnabled ? "1" : "0")}");
                    sw.WriteLine($"ShowThroughputStats={(ShowThroughputStats ? "1" : "0")}");
                    sw.WriteLine($"PathStorageMode={PathStorageMode}");
                    sw.WriteLine($"UseAbsolutePaths={(PathStorageMode == PathStorageMode.Absolute ? "1" : "0")}");
                    sw.WriteLine($"ShowFilterPanel={(ShowFilterPanel ? "1" : "0")}");
                    sw.WriteLine($"ShowAdvancedBar={(ShowAdvancedBar ? "1" : "0")}");
                    sw.WriteLine($"EnableChecksumComments={(EnableChecksumComments ? "1" : "0")}");
                    sw.WriteLine($"PathPrefix={PathPrefix}");
                    sw.WriteLine($"IncludePattern={IncludePattern}");
                    sw.WriteLine($"ExcludePattern={ExcludePattern}");
                    sw.WriteLine($"ScanRecursive={(ScanRecursive ? "1" : "0")}");
                    sw.WriteLine($"ProcessingMode={ProcessingMode}");
                    sw.WriteLine($"ShowFullPaths={(ShowFullPaths ? "1" : "0")}");
                    sw.WriteLine($"ShowHashCol={(ShowHashCol ? "1" : "0")}");
                    sw.WriteLine($"ShowExpectedHashCol={(ShowExpectedHashCol ? "1" : "0")}");
                    sw.WriteLine($"LockColumns={(LockColumns ? "1" : "0")}");
                    sw.WriteLine($"ColumnOrder={colOrderStr}");
                    sw.WriteLine($"Signature={CustomSignature}");
                    sw.WriteLine($"DefaultAlgo={currentAlgo}");
                    sw.WriteLine($"WindowSizeW={bounds.Width}");
                    sw.WriteLine($"WindowSizeH={bounds.Height}");
                    sw.WriteLine($"WindowPosX={bounds.X}");
                    sw.WriteLine($"WindowPosY={bounds.Y}");
                    sw.WriteLine($"SplitterPos={currentSplitterDist}");
                }
            }
            catch { }
        }

        public void ResetToDefaults()
        {
            try
            {
                if (File.Exists(_iniPath)) File.Delete(_iniPath);
            }
            catch { }
        }
    }
}