using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace SharpSFV
{
    public static class DriveDetector
    {
        // P/Invoke Constants
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        // IOCTL Codes
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

        // Property IDs
        private const int StorageDeviceProperty = 0;
        private const int StorageDeviceSeekPenaltyProperty = 7;
        private const int PropertyStandardQuery = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISK_EXTENT
        {
            public int DiskNumber;
            public long StartingOffset;
            public long ExtentLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VOLUME_DISK_EXTENTS
        {
            public int NumberOfDiskExtents;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public DISK_EXTENT[] Extents;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        /// <summary>
        /// Determines if the drive is a mechanical HDD.
        /// Returns TRUE for HDD/Network (Sequential preferred).
        /// Returns FALSE for SSD (Parallel preferred).
        /// </summary>
        public static bool IsRotational(string path)
        {
            try
            {
                string driveRoot = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                if (string.IsNullOrEmpty(driveRoot)) return true; // Safety: Default to HDD

                // 1. Network Check
                try
                {
                    DriveInfo di = new DriveInfo(driveRoot);
                    if (di.DriveType == DriveType.Network) return true;
                }
                catch { }

                string volume = $"\\\\.\\{driveRoot.TrimEnd('\\')}";

                using (SafeFileHandle hDevice = CreateFile(volume, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (hDevice.IsInvalid) return true; // Default to HDD on error

                    // 2. Direct Volume Check (Works for unencrypted internal drives)
                    int volRpm = GetRotationRate(hDevice);
                    if (volRpm > 1) return true;
                    if (GetSeekPenalty(hDevice) == 1) return true;

                    // 3. Physical Drive Mapping (For Encrypted/Virtual Volumes)
                    int diskNumber = GetPhysicalDriveNumber(hDevice);
                    if (diskNumber >= 0)
                    {
                        // 4. Try opening Physical Drive (Requires Admin)
                        string physicalPath = $"\\\\.\\PhysicalDrive{diskNumber}";
                        using (SafeFileHandle hPhysical = CreateFile(physicalPath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                        {
                            if (!hPhysical.IsInvalid)
                            {
                                int physRpm = GetRotationRate(hPhysical);
                                if (physRpm > 1) return true;
                                if (GetSeekPenalty(hPhysical) == 1) return true;
                            }
                        }

                        // 5. Registry String Heuristics (Robust Fallback)
                        // If hardware flags failed or reported 0 (Unknown), check the Model Name.
                        string friendlyName = GetFriendlyNameFromRegistry(diskNumber);
                        if (!string.IsNullOrEmpty(friendlyName))
                        {
                            if (IsSsdByName(friendlyName)) return false; // Explicitly SSD
                            return true; // If not explicitly SSD, treat as HDD (Safe Default)
                        }
                    }
                }
            }
            catch { /* Safety Default */ }
            return true; // Default to HDD (Sequential) prevents system lockup
        }

        private static bool IsSsdByName(string name)
        {
            string n = name.ToUpperInvariant();
            if (n.Contains("SSD")) return true;
            if (n.Contains("NVME")) return true;
            if (n.Contains("FLASH")) return true;
            if (n.Contains("M.2")) return true;
            return false;
        }

        private static int GetRotationRate(SafeFileHandle hDevice)
        {
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty, QueryType = PropertyStandardQuery };
            uint bytesReturned;
            IntPtr buffer = Marshal.AllocHGlobal(1024);
            try
            {
                if (DeviceIoControl(hDevice, IOCTL_STORAGE_QUERY_PROPERTY, ref query, (uint)Marshal.SizeOf(query), buffer, 1024, out bytesReturned, IntPtr.Zero))
                {
                    if (bytesReturned >= 40) return Marshal.ReadInt32(buffer, 36);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return -1;
        }

        private static int GetSeekPenalty(SafeFileHandle hDevice)
        {
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
            uint bytesReturned;
            IntPtr buffer = Marshal.AllocHGlobal(12);
            try
            {
                if (DeviceIoControl(hDevice, IOCTL_STORAGE_QUERY_PROPERTY, ref query, (uint)Marshal.SizeOf(query), buffer, 12, out bytesReturned, IntPtr.Zero))
                {
                    return Marshal.ReadByte(buffer, 8) != 0 ? 1 : 0;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return -1;
        }

        private static int GetPhysicalDriveNumber(SafeFileHandle hDevice)
        {
            int size = Marshal.SizeOf(typeof(VOLUME_DISK_EXTENTS));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            uint bytesReturned;
            try
            {
                // IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS
                if (DeviceIoControl(hDevice, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, ptr, (uint)size, out bytesReturned, IntPtr.Zero))
                {
                    var extents = Marshal.PtrToStructure<VOLUME_DISK_EXTENTS>(ptr);
                    if (extents.NumberOfDiskExtents > 0) return extents.Extents[0].DiskNumber;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return -1;
        }

        private static string GetFriendlyNameFromRegistry(int diskNumber)
        {
            try
            {
                // 1. Get PnP Device ID from the Disk Service Enum
                using (var keyEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum"))
                {
                    if (keyEnum == null) return "";
                    string? deviceId = keyEnum.GetValue(diskNumber.ToString()) as string;

                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        // 2. Get FriendlyName from the Device Enum
                        using (var keyDevice = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}"))
                        {
                            if (keyDevice != null)
                            {
                                object? val = keyDevice.GetValue("FriendlyName") ?? keyDevice.GetValue("DeviceDesc");
                                if (val is string s) return s;
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        public static string GetDebugInfo(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Probing path: {path}");

            try
            {
                string driveRoot = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                sb.AppendLine($"Drive Root: {driveRoot}");

                try { sb.AppendLine($"Drive Type: {new DriveInfo(driveRoot).DriveType}"); } catch { }

                string volume = $"\\\\.\\{driveRoot.TrimEnd('\\')}";
                sb.AppendLine($"Volume: {volume}");

                using (SafeFileHandle hDevice = CreateFile(volume, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (hDevice.IsInvalid)
                    {
                        sb.AppendLine($"ERROR: Invalid Handle (Err: {Marshal.GetLastWin32Error()}).");
                        return sb.ToString();
                    }

                    // Volume Check
                    int volRpm = GetRotationRate(hDevice);
                    int volSeek = GetSeekPenalty(hDevice);
                    sb.AppendLine($"[Volume IOCTL] RPM: {volRpm}, SeekPenalty: {volSeek}");

                    // Extents Check
                    int diskNum = GetPhysicalDriveNumber(hDevice);
                    if (diskNum >= 0)
                    {
                        sb.AppendLine($"Mapped to PhysicalDisk{diskNum}");

                        // Physical Handle Check
                        string physPath = $"\\\\.\\PhysicalDrive{diskNum}";
                        using (SafeFileHandle hPhysical = CreateFile(physPath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                        {
                            if (!hPhysical.IsInvalid)
                            {
                                int physRpm = GetRotationRate(hPhysical);
                                int physSeek = GetSeekPenalty(hPhysical);
                                sb.AppendLine($"[Physical IOCTL] RPM: {physRpm}, SeekPenalty: {physSeek}");
                            }
                            else
                            {
                                sb.AppendLine($"[Physical IOCTL] Access Denied (Requires Admin)");
                            }
                        }

                        // Registry Check
                        string name = GetFriendlyNameFromRegistry(diskNum);
                        sb.AppendLine($"[Registry] FriendlyName: '{name}'");

                        bool isSsd = IsSsdByName(name);
                        sb.AppendLine($"[Heuristic] Name implies SSD? {isSsd}");

                        bool finalDecision = IsRotational(path);
                        sb.AppendLine($"\n>>> FINAL DECISION: {(finalDecision ? "HDD Mode (Sequential)" : "SSD Mode (Parallel)")}");
                    }
                    else
                    {
                        sb.AppendLine("Could not map to Physical Disk.");
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine($"CRITICAL: {ex.Message}"); }
            return sb.ToString();
        }
    }
}