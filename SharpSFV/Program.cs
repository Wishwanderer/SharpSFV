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
        private const string MutexName = "SharpSFV_Instance_Mutex";
        private const string PipeName = "SharpSFV_Pipe";

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
                        SendArgsToPrimaryInstance(args);
                        return; 
                    }
                }

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
                if (args.Contains("-headless", StringComparer.OrdinalIgnoreCase))
                {
                    Win32Storage.FreeConsole();
                }

                // Release Mutex if we own it
                if (mutex != null)
                {
                    mutex.Dispose();
                }
            }
        }

        private static void SendArgsToPrimaryInstance(string[] args)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(1000); // 1s timeout
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
            }
        }
    }
}