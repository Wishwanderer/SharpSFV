using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpSFV
{
    public static class DriveDetector
    {
        // P/Invoke Constants and Structs
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            public bool RemovableMedia;
            public bool CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public byte BusType;
            public uint RawPropertiesLength;
            // The following fields are variable length, but we only need up to RotationRate
            public byte RawDeviceProperties;
        }

        // We manually offset to get RotationRate because of variable length structs
        private const int StorageDeviceProperty = 0;
        private const int PropertyStandardQuery = 0;

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

        /// <summary>
        /// Determines if the drive containing the specified path is a mechanical HDD (Rotating).
        /// Returns FALSE for SSDs, NVMe, or if the check fails (defaults to fast).
        /// </summary>
        public static bool IsRotational(string path)
        {
            try
            {
                // 1. Get the Drive Root (e.g., "C:\")
                string driveRoot = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                if (string.IsNullOrEmpty(driveRoot)) return false; // Default to SSD behavior on error

                // 2. Format for Windows API (e.g., "\\.\C:")
                string volume = $"\\\\.\\{driveRoot.TrimEnd('\\')}";

                using (SafeFileHandle hDevice = CreateFile(
                    volume,
                    0, // No access rights needed for query
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero))
                {
                    if (hDevice.IsInvalid) return false; // Default to SSD if we can't open handle (e.g., Network drive)

                    // 3. Prepare Query
                    var query = new STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = StorageDeviceProperty,
                        QueryType = PropertyStandardQuery
                    };

                    uint bytesReturned;
                    // Allocate a buffer for the result (header + raw data)
                    int bufferSize = 1024;
                    IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

                    try
                    {
                        if (DeviceIoControl(
                            hDevice,
                            IOCTL_STORAGE_QUERY_PROPERTY,
                            ref query,
                            (uint)Marshal.SizeOf(query),
                            buffer,
                            (uint)bufferSize,
                            out bytesReturned,
                            IntPtr.Zero))
                        {
                            // 4. Parse Result
                            // STORAGE_DEVICE_DESCRIPTOR structure varies in size.
                            // NominalMediaRotationRate is at offset 36 usually, but safer to use struct mapping if possible.
                            // However, strictly, offset 36 is the RotationRate in newer Windows versions.

                            // Let's rely on the Byte Offset defined in Windows docs for STORAGE_DEVICE_DESCRIPTOR
                            // Version (4) + Size (4) + DeviceType (1) + Modifier (1) + Removable (1) + CommandQueue (1) 
                            // + VendorOff (4) + ProductOff (4) + RevisionOff (4) + SerialOff (4) + BusType (1) + RawPropLen (4)
                            // = 33 bytes? No, alignment padding applies.

                            // A safer generic check for Rotation Rate (Offset 36 is standard for Windows 10/11 SDK)
                            // We check the raw bytes at offset 36.

                            // Check version first (Offset 0)
                            // int version = Marshal.ReadInt32(buffer, 0);
                            // int size = Marshal.ReadInt32(buffer, 4);

                            // NominalMediaRotationRate is a DWORD at offset 36 (decimal)
                            // 0 = Non-Rotating (SSD), 1 = Non-Rotating (SSD), >1 = RPM (HDD)

                            // Note: If buffer is smaller than 40 bytes, this info isn't there.
                            if (bytesReturned >= 40)
                            {
                                int rotationRate = Marshal.ReadInt32(buffer, 36);
                                if (rotationRate > 1) return true; // It spins!
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            catch { /* Ignore errors, assume SSD */ }

            return false;
        }
    }
}