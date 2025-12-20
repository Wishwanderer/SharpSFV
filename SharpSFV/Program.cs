namespace SharpSFV
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                // Critical catch block for runtime errors (e.g., missing .NET Runtime, missing DLLs)
                MessageBox.Show($"Critical Startup Error:\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "SharpSFV Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}