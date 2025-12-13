using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace SharpSFV
{
    public class AppSettings
    {
        private readonly string _iniPath;

        // Settings Properties with Defaults
        public bool ShowTimeTab { get; set; } = false;
        public string CustomSignature { get; set; } = "L33T";
        public HashType DefaultAlgo { get; set; } = HashType.XxHash3;
        public Size WindowSize { get; set; } = new Size(800, 600);
        public Point WindowLocation { get; set; } = Point.Empty;
        public bool HasCustomLocation { get; private set; } = false;

        // NEW SETTING
        public bool UseAbsolutePaths { get; set; } = false;

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

                    if (key.Equals("TimeTab", StringComparison.OrdinalIgnoreCase))
                        ShowTimeTab = (val == "1");
                    else if (key.Equals("Signature", StringComparison.OrdinalIgnoreCase))
                        CustomSignature = val;
                    else if (key.Equals("UseAbsolutePaths", StringComparison.OrdinalIgnoreCase)) // Load New Setting
                        UseAbsolutePaths = (val == "1");
                    else if (key.Equals("DefaultAlgo", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<HashType>(val, true, out var result))
                            DefaultAlgo = result;
                    }
                    else if (key.Equals("WindowSizeW", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(val, out w);
                    else if (key.Equals("WindowSizeH", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(val, out h);
                    else if (key.Equals("WindowPosX", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out x)) foundX = true;
                    }
                    else if (key.Equals("WindowPosY", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out y)) foundY = true;
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

        public void Save(Form form, bool isTimeTabEnabled, HashType currentAlgo)
        {
            try
            {
                Rectangle bounds = (form.WindowState == FormWindowState.Normal) ? form.Bounds : form.RestoreBounds;
                string timeTab = isTimeTabEnabled ? "1" : "0";
                string absPaths = UseAbsolutePaths ? "1" : "0"; // Save New Setting

                using (StreamWriter sw = new StreamWriter(_iniPath))
                {
                    sw.WriteLine("[SharpSFV]");
                    sw.WriteLine($"TimeTab={timeTab}");
                    sw.WriteLine($"UseAbsolutePaths={absPaths}");
                    sw.WriteLine($"Signature={CustomSignature}");
                    sw.WriteLine($"DefaultAlgo={currentAlgo}");
                    sw.WriteLine($"WindowSizeW={bounds.Width}");
                    sw.WriteLine($"WindowSizeH={bounds.Height}");
                    sw.WriteLine($"WindowPosX={bounds.X}");
                    sw.WriteLine($"WindowPosY={bounds.Y}");
                }
            }
            catch { }
        }
    }
}