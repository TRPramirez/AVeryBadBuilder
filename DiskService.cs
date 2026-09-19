using System.IO;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using DiscUtils.Fat;
using DiscUtils.Partitions;
using DiscUtils.Raw;
using DiscUtils.Streams;
using BadBuilder.Configuration;

namespace BadBuilder.Services.Disks;

internal static class DiskService
{
    internal static List<DiskInfo> EnumerateDisks()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        return EnumerateDisksWindows();
    }

    internal static string FormatFAT32(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Formatting is only supported on Windows.");

        using RawDiskStream stream = OpenRawDiskForWrite(disk);
        using Disk virtualDisk = new(stream, Ownership.None);

        BiosPartitionTable.Initialize(virtualDisk, WellKnownPartitionType.WindowsFat);

        using FatFileSystem fs = FatFileSystem.FormatPartition(virtualDisk, 0, "BADUPDATE  ");
        stream.Flush();

        return ReassignWindows(disk);
    }

    [SupportedOSPlatform("windows")]
    private static List<DiskInfo> EnumerateDisksWindows()
    {
        List<DiskInfo> disks = [];

        using ManagementObjectSearcher searcher = new("SELECT * FROM Win32_DiskDrive");

        foreach (ManagementObject drive in searcher.Get())
        {
            int index = Convert.ToInt32(drive["Index"]);
            string deviceId = (string)drive["DeviceID"];
            string model = (drive["Model"] as string)?.Trim() ?? $"Disk {index}";
            long size = drive["Size"] is null ? 0 : Convert.ToInt64(drive["Size"]);
            string interfaceType = drive["InterfaceType"] as string ?? "";
            string mediaType = drive["MediaType"] as string ?? "";

            bool removable =
                interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase);

            disks.Add(new DiskInfo(
                ID: index.ToString(),
                Name: model,
                Size: size,
                Type: removable ? DiskDriveType.Removable : DiskDriveType.Fixed,
                DevicePath: deviceId
            ));
        }

        return disks;
    }

    [SupportedOSPlatform("windows")]
    private static RawDiskStream OpenRawDiskForWrite(DiskInfo disk)
    {
        int diskIndex = int.Parse(disk.ID);
        List<VolumeLock> volumeLocks = LockAndDismountVolumes(diskIndex);
        string path = $@"\\.\PhysicalDrive{diskIndex}";

        SafeFileHandle handle = CreateFile(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero
        );

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            foreach (VolumeLock l in volumeLocks) l.Dispose();
            throw new System.IO.IOException($"Could not open {path} for writing (run elevated, as Administrator). Win32 error {error}.");
        }

        System.IO.FileStream fileStream = new(handle, System.IO.FileAccess.ReadWrite);

        return new RawDiskStream(fileStream, disk.Size, onDisposed: () =>
        {
            foreach (VolumeLock l in volumeLocks) l.Dispose();
        });
    }

    [SupportedOSPlatform("windows")]
    private static string ReassignWindows(DiskInfo disk)
    {
        int diskIndex = int.Parse(disk.ID);

        using (SafeFileHandle handle = CreateFile(
            $@"\\.\PhysicalDrive{diskIndex}",
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero
        ))
        {
            if (!handle.IsInvalid)
                DeviceIoControl(handle, IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }

        // Give Windows a moment to assign a letter
        Thread.Sleep(1500);

        string driveLetter = EnumerateDriveLetters(diskIndex).FirstOrDefault()
            ?? throw new System.IO.IOException($"Formatted PhysicalDrive{diskIndex} but no drive letter was assigned.");

        return driveLetter + @"\";
    }

    [SupportedOSPlatform("windows")]
    private static List<VolumeLock> LockAndDismountVolumes(int diskIndex)
    {
        List<VolumeLock> locks = [];

        foreach (string driveLetter in EnumerateDriveLetters(diskIndex))
        {
            VolumeLock? volumeLock = VolumeLock.TryCreate(driveLetter);
            if (volumeLock is not null)
                locks.Add(volumeLock);
        }

        return locks;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateDriveLetters(int diskIndex)
    {
        using ManagementObjectSearcher partitionSearcher = new(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{diskIndex}'}} " +
            "WHERE AssocClass = Win32_DiskDriveToDiskPartition"
        );

        foreach (ManagementObject partition in partitionSearcher.Get())
        {
            using ManagementObjectSearcher logicalSearcher = new(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition"
            );

            foreach (ManagementObject logicalDisk in logicalSearcher.Get())
                yield return (string)logicalDisk["DeviceID"];
        }
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018;
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    private const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private sealed class VolumeLock : IDisposable
    {
        private readonly SafeFileHandle _handle;

        private VolumeLock(SafeFileHandle handle) => _handle = handle;

        public static VolumeLock? TryCreate(string driveLetter)
        {
            string path = $@"\\.\{driveLetter.TrimEnd('\\')}";

            SafeFileHandle handle = CreateFile(
                path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero
            );

            if (handle.IsInvalid) return null;

            DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            return new VolumeLock(handle);
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed class RawDiskStream : System.IO.Stream
    {
        private readonly System.IO.Stream _inner;
        private readonly long _length;
        private readonly Action? _onDisposed;
        private bool _disposed;

        public RawDiskStream(System.IO.Stream inner, long length, Action? onDisposed = null)
        {
            _inner = inner;
            _length = length;
            _onDisposed = onDisposed;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, System.IO.SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _inner.Dispose();
                _onDisposed?.Invoke();
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
