using System;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;

namespace SharpSFV.Utils
{
    public static class SelfInstaller
    {
        // TARGET: C:\Users\<User>\AppData\Local\SharpSFV
        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpSFV");

        private const string ExeName = "SharpSFV.exe";
        private static readonly string InstallPath = Path.Combine(InstallDir, ExeName);

        public static void EnsureCentralizedInstall()
        {
            try
            {
                // 1. Check if we are ALREADY running from the install location
                if (string.Equals(Application.ExecutablePath, InstallPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 2. Ensure Directory Exists (No Admin required for AppData)
                if (!Directory.Exists(InstallDir))
                {
                    Directory.CreateDirectory(InstallDir);
                }

                // 3. Smart Copy / Update
                // Only copy if the installed file is missing OR older than the current running copy.
                bool shouldCopy = true;
                if (File.Exists(InstallPath))
                {
                    DateTime installedTime = File.GetLastWriteTimeUtc(InstallPath);
                    DateTime myTime = File.GetLastWriteTimeUtc(Application.ExecutablePath);

                    // If local exe is older or same age as installed one, don't overwrite
                    if (myTime <= installedTime) shouldCopy = false;
                }

                if (shouldCopy)
                {
                    // Copy current running EXE to AppData
                    File.Copy(Application.ExecutablePath, InstallPath, true);
                }

                // 4. Update User PATH Variable
                // EnvironmentVariableTarget.User does NOT require Admin privileges.
                EnsurePathEnvironmentVariable();
            }
            catch (Exception ex)
            {
                // Fail silently (log to debug). 
                // We never want the installer logic to crash the actual hashing job.
                Debug.WriteLine($"Self-Install Error: {ex.Message}");
            }
        }

        private static void EnsurePathEnvironmentVariable()
        {
            const string pathVar = "PATH";
            var target = EnvironmentVariableTarget.User;

            string? currentPath = Environment.GetEnvironmentVariable(pathVar, target);
            if (currentPath == null) currentPath = "";

            // Check if AppData path is already in PATH
            if (!currentPath.Contains(InstallDir, StringComparison.OrdinalIgnoreCase))
            {
                string newPath = string.IsNullOrEmpty(currentPath)
                    ? InstallDir
                    : currentPath + ";" + InstallDir;

                Environment.SetEnvironmentVariable(pathVar, newPath, target);
            }
        }
    }
}