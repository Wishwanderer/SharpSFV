using SharpSFV.Interop;
using SharpSFV.Utils;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SharpSFV
{
    /// <summary>
    /// The main entry point for the application.
    /// <para>
    /// <b>Responsibilities:</b>
    /// 1. <b>Self-Installation:</b> Checks if the app is running from <c>%LOCALAPPDATA%</c>. If not, installs itself there.
    /// 2. <b>Single Instance Logic:</b> Uses a <see cref="Mutex"/> to prevent multiple "Create" windows.
    /// 3. <b>IPC:</b> Uses Named Pipes to forward arguments from secondary instances to the primary instance.
    /// 4. <b>Headless Mode:</b> Attaches to the parent console for CLI output.
    /// </para>
    /// </summary>
    internal static class Program
    {
        // Unique ID for the Mutex. Global to the operating system session.
        private const string MutexName = "SharpSFV_Instance_Mutex";
        private const string PipeName = "SharpSFV_Pipe";

        [STAThread]
        static void Main(string[] args)
        {
            // --- NEW: Self-Centralization Logic ---
            // Before doing anything else, check if we should install/update 
            // the central copy in %LOCALAPPDATA%\SharpSFV and add it to PATH.
            // This allows future scripts to just call "SharpSFV" without a full path.
            SelfInstaller.EnsureCentralizedInstall();
            // --------------------------------------

            bool isCreateMode = args.Contains("-create", StringComparer.OrdinalIgnoreCase);
            Mutex? mutex = null;

            try
            {
                if (isCreateMode)
                {
                    // Try to grab ownership of the Mutex
                    bool createdNew;
                    mutex = new Mutex(true, MutexName, out createdNew);

                    if (!createdNew)
                    {
                        // Mutex exists -> Another instance is already running.
                        // Forward our arguments (file paths) to that instance via Named Pipe and close.
                        SendArgsToPrimaryInstance(args);
                        return;
                    }
                }

                // Headless Mode: Attach to the parent console (CMD/PowerShell) to print output.
                // WinForms apps detach from the console by default, so we must manually re-attach
                // to write to standard output.
                if (args.Contains("-headless", StringComparer.OrdinalIgnoreCase))
                {
                    Win32Storage.AttachConsole(Win32Storage.ATTACH_PARENT_PROCESS);
                }

                ApplicationConfiguration.Initialize();
                Application.Run(new Form1(args));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Critical Startup Error:\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "SharpSFV Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Clean up Console attachment
                if (args.Contains("-headless", StringComparer.OrdinalIgnoreCase))
                {
                    Win32Storage.FreeConsole();
                }

                // Release Mutex if we own it, allowing future instances to become the primary.
                if (mutex != null)
                {
                    mutex.Dispose();
                }
            }
        }

        /// <summary>
        /// Connects to the Named Pipe server hosted by the Primary Instance and writes arguments to it.
        /// </summary>
        private static void SendArgsToPrimaryInstance(string[] args)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(1000); // 1s timeout to avoid hanging if the server is unresponsive
                    using (var writer = new StreamWriter(client, Encoding.UTF8))
                    {
                        foreach (var arg in args)
                        {
                            writer.WriteLine(arg);
                        }
                    }
                }
            }
            catch
            {
                // Silently fail if connection drops (rare race condition), 
                // preventing annoying popups for the user.
            }
        }
    }
}