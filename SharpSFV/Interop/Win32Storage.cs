using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Windows.Forms;

namespace SharpSFV.Interop
{
    // --- TASKBAR ENUMS ---
    public enum TbpFlag
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    public class TaskbarInstance
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] void HrInit();
        [PreserveSig] void AddTab(IntPtr hwnd);
        [PreserveSig] void DeleteTab(IntPtr hwnd);
        [PreserveSig] void ActivateTab(IntPtr hwnd);
        [PreserveSig] void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        [PreserveSig] void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        [PreserveSig] void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] void SetProgressState(IntPtr hwnd, TbpFlag tbpFlags);
        [PreserveSig] void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        [PreserveSig] void UnregisterTab(IntPtr hwndTab);
        [PreserveSig] void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        [PreserveSig] void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        [PreserveSig] void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        [PreserveSig] void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        [PreserveSig] void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
        [PreserveSig] void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    internal static class Win32Storage
    {
        // Constants
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        // CLI Console Constants
        public const int ATTACH_PARENT_PROCESS = -1;

        // IOCTL Codes
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

        // Property IDs
        public const int StorageDeviceProperty = 0;
        public const int StorageDeviceSeekPenaltyProperty = 7;
        public const int PropertyStandardQuery = 0;

        // Window Messages
        public const int WM_SETREDRAW = 0x000B;

        // Progress Bar Messages
        public const int PBM_SETSTATE = 0x0410;
        public const int PBST_NORMAL = 1; // Green
        public const int PBST_ERROR = 2;  // Red
        public const int PBST_PAUSED = 3; // Yellow

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

        // --- P/Invokes: Console ---
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeConsole();

        // --- P/Invokes: I/O ---
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

        // --- P/Invokes: UI ---
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

        public static void SetProgressBarState(ProgressBar pBar, int state)
        {
            SendMessage(pBar.Handle, PBM_SETSTATE, new IntPtr(state), IntPtr.Zero);
        }
    }
}