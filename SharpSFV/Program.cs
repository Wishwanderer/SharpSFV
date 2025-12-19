namespace SharpSFV
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Wrap the startup in a Try/Catch to catch missing DLLs/Init failures
            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                // This ensures the user sees WHY it failed (e.g., "Could not load file or assembly 'System.IO.Hashing'")
                MessageBox.Show($"Critical Startup Error:\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "SharpSFV Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}