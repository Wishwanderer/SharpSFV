using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SharpSFV
{
    public class AppSettings
    {
        private readonly string _iniPath;

        // Existing
        public bool ShowTimeTab { get; set; } = false;
        public bool UseAbsolutePaths { get; set; } = false;
        public bool ShowFilterPanel { get; set; } = false;
        public bool OptimizeForHDD { get; set; } = false;
        public bool ShowFullPaths { get; set; } = false;

        public string CustomSignature { get; set; } = "L33T";
        public HashType DefaultAlgo { get; set; } = HashType.XxHash3;

        public Size WindowSize { get; set; } = new Size(800, 600);
        public Point WindowLocation { get; set; } = Point.Empty;
        public bool HasCustomLocation { get; private set; } = false;
        public int SplitterDistance { get; set; } = -1;

        // NEW: View Settings
        public bool ShowHashCol { get; set; } = true;
        public bool ShowExpectedHashCol { get; set; } = true;
        public bool LockColumns { get; set; } = true; // Enabled by default as requested

        // Dictionary to store "ColumnTag" -> "DisplayIndex"
        public Dictionary<string, int> ColumnOrder { get; set; } = new Dictionary<string, int>();

        public AppSettings(string appExecutablePath)
        {
            string exeDir = Path.GetDirectoryName(appExecutablePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            _iniPath = Path.Combine(exeDir, "SharpSFV.ini");
        }

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

                    if (key.Equals("TimeTab", StringComparison.OrdinalIgnoreCase)) ShowTimeTab = (val == "1");
                    else if (key.Equals("UseAbsolutePaths", StringComparison.OrdinalIgnoreCase)) UseAbsolutePaths = (val == "1");
                    else if (key.Equals("ShowFilterPanel", StringComparison.OrdinalIgnoreCase)) ShowFilterPanel = (val == "1");
                    else if (key.Equals("OptimizeForHDD", StringComparison.OrdinalIgnoreCase)) OptimizeForHDD = (val == "1");
                    else if (key.Equals("ShowFullPaths", StringComparison.OrdinalIgnoreCase)) ShowFullPaths = (val == "1");
                    // New View Keys
                    else if (key.Equals("ShowHashCol", StringComparison.OrdinalIgnoreCase)) ShowHashCol = (val == "1");
                    else if (key.Equals("ShowExpectedHashCol", StringComparison.OrdinalIgnoreCase)) ShowExpectedHashCol = (val == "1");
                    else if (key.Equals("LockColumns", StringComparison.OrdinalIgnoreCase)) LockColumns = (val == "1");
                    else if (key.Equals("ColumnOrder", StringComparison.OrdinalIgnoreCase)) ParseColumnOrder(val);

                    else if (key.Equals("Signature", StringComparison.OrdinalIgnoreCase)) CustomSignature = val;
                    else if (key.Equals("DefaultAlgo", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<HashType>(val, true, out var result)) DefaultAlgo = result;
                    }
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

        public void Save(Form form, bool isTimeTabEnabled, HashType currentAlgo, int currentSplitterDist, ListView lv)
        {
            try
            {
                Rectangle bounds = (form.WindowState == FormWindowState.Normal) ? form.Bounds : form.RestoreBounds;

                // Serialize Column Order
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
                    sw.WriteLine($"UseAbsolutePaths={(UseAbsolutePaths ? "1" : "0")}");
                    sw.WriteLine($"ShowFilterPanel={(ShowFilterPanel ? "1" : "0")}");
                    sw.WriteLine($"OptimizeForHDD={(OptimizeForHDD ? "1" : "0")}");
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
    }
}