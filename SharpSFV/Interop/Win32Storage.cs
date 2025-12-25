using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Windows.Forms;

namespace SharpSFV.Interop
{
    internal static class Win32Storage
    {
        // Constants
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        // IOCTL Codes
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

        // Property IDs
        public const int StorageDeviceProperty = 0;
        public const int StorageDeviceSeekPenaltyProperty = 7;
        public const int PropertyStandardQuery = 0;

        // Window Messages
        public const int WM_SETREDRAW = 0x000B;

        // Structures
        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_EXTENT
        {
            public int DiskNumber;
            public long StartingOffset;
            public long ExtentLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VOLUME_DISK_EXTENTS
        {
            public int NumberOfDiskExtents;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public DISK_EXTENT[] Extents;
        }

        // P/Invokes
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // UI P/Invokes
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Optimization Helpers
        public static void SuspendDrawing(Control control)
        {
            SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        public static void ResumeDrawing(Control control)
        {
            SendMessage(control.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            control.Refresh();
        }
    }
}