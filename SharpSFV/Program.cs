using SharpSFV.Interop;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SharpSFV
{
    internal static class Program
    {
        // Unique ID for the Mutex. Global to the operating system session.
        private const string MutexName = "SharpSFV_Instance_Mutex";
        private const string PipeName = "SharpSFV_Pipe";

        /// <summary>
        /// The main entry point for the application.
        /// <para>
        /// <b>Logic Flow:</b>
        /// 1. Checks for <c>-create</c> argument (Context Menu mode).
        /// 2. If present, attempts to acquire a global <see cref="Mutex"/>.
        /// 3. If Mutex is already owned by another process, this is a secondary instance.
        ///    It sends its arguments to the primary instance via Named Pipe and exits.
        /// 4. If Mutex is acquired (or not in create mode), it launches the GUI.
        /// </para>
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
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
                        // Forward our arguments (file paths) to that instance and close.
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