using System;
using System.Buffers;
using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SharpSFV
{
    public static class HashHelper
    {
        // Synchronous version for Stream (Fallback for large files)
        public static byte[]? ComputeHashSync(Stream inputStream, HashType type, byte[] buffer, HashAlgorithm? reusedAlgo)
        {
            int bytesRead;

            try
            {
                switch (type)
                {
                    case HashType.XxHash3:
                        var xx3 = new XxHash128();
                        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            xx3.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                        }
                        return xx3.GetCurrentHash();

                    case HashType.Crc32:
                        var crc = new Crc32();
                        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            crc.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                        }
                        return crc.GetCurrentHash();

                    case HashType.MD5:
                    case HashType.SHA1:
                    case HashType.SHA256:
                        if (reusedAlgo == null) return null;

                        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            reusedAlgo.TransformBlock(buffer, 0, bytesRead, null, 0);
                        }

                        reusedAlgo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        return reusedAlgo.Hash;

                    default: return null;
                }
            }
            catch { return null; }
        }

        // NEW: Zero-Allocation version for RandomAccess (SafeFileHandle)
        public static byte[]? ComputeHashHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, HashType type, byte[] buffer, HashAlgorithm? reusedAlgo)
        {
            try
            {
                long fileLength = RandomAccess.GetLength(handle);
                long position = 0;
                int bytesRead = 0;

                switch (type)
                {
                    case HashType.XxHash3:
                        var xx3 = new XxHash128();
                        while (position < fileLength)
                        {
                            bytesRead = RandomAccess.Read(handle, buffer, position);
                            if (bytesRead == 0) break;
                            xx3.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            position += bytesRead;
                        }
                        return xx3.GetCurrentHash();

                    case HashType.Crc32:
                        var crc = new Crc32();
                        while (position < fileLength)
                        {
                            bytesRead = RandomAccess.Read(handle, buffer, position);
                            if (bytesRead == 0) break;
                            crc.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            position += bytesRead;
                        }
                        return crc.GetCurrentHash();

                    case HashType.MD5:
                    case HashType.SHA1:
                    case HashType.SHA256:
                        if (reusedAlgo == null) return null;

                        while (position < fileLength)
                        {
                            bytesRead = RandomAccess.Read(handle, buffer, position);
                            if (bytesRead == 0) break;
                            reusedAlgo.TransformBlock(buffer, 0, bytesRead, null, 0);
                            position += bytesRead;
                        }
                        reusedAlgo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        return reusedAlgo.Hash;

                    default: return null;
                }
            }
            catch { return null; }
        }
    }
}