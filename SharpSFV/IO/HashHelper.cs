using System;
using System.Buffers;
using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SharpSFV
{
    /// <summary>
    /// Provides low-level wrappers for hashing algorithms, abstracting the difference between 
    /// modern non-allocating algorithms (<see cref="System.IO.Hashing"/>) and legacy crypto providers.
    /// <para>
    /// <b>Optimization:</b>
    /// Designed to operate on pre-allocated buffers (<see cref="ArrayPool{T}"/>) to ensure zero-allocation during the hashing loop.
    /// </para>
    /// </summary>
    public static class HashHelper
    {
        /// <summary>
        /// Computes a hash from a standard <see cref="Stream"/>.
        /// Used primarily for files larger than 50MB where we wrap the stream in a <see cref="ProgressStream"/> 
        /// to report progress to the UI.
        /// </summary>
        /// <param name="inputStream">The stream to read from.</param>
        /// <param name="type">The algorithm to use.</param>
        /// <param name="buffer">A shared, pre-allocated buffer from ArrayPool.</param>
        /// <param name="reusedAlgo">
        /// An optional pre-instantiated <see cref="HashAlgorithm"/> (MD5/SHA) to reuse, 
        /// avoiding the overhead of creating new crypto providers per file.
        /// </param>
        public static byte[]? ComputeHashSync(Stream inputStream, HashType type, byte[] buffer, HashAlgorithm? reusedAlgo)
        {
            int bytesRead;

            try
            {
                switch (type)
                {
                    case HashType.XXHASH3:
                        // XxHash128 is a struct (value type) in .NET 8+, allocated on stack.
                        var xx3 = new XxHash128();
                        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // Append using Span to avoid array allocation
                            xx3.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                        }
                        return xx3.GetCurrentHash();

                    case HashType.Crc32:
                        // Explicitly specify System.IO.Hashing to avoid ambiguity with System.Runtime.Intrinsics.Arm
                        var crc = new System.IO.Hashing.Crc32();
                        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            crc.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                        }
                        return crc.GetCurrentHash();

                    // Legacy Crypto Providers (MD5, SHA1, SHA256)
                    case HashType.MD5:
                    case HashType.SHA1:
                    case HashType.SHA256:
                        if (reusedAlgo == null) return null;

                        // TransformBlock processes data chunk by chunk without loading the whole file into RAM
                        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            reusedAlgo.TransformBlock(buffer, 0, bytesRead, null, 0);
                        }

                        // Finalize the hash computation
                        reusedAlgo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        return reusedAlgo.Hash;

                    default: return null;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Computes a hash using <see cref="RandomAccess"/> on a raw file handle.
        /// Used for small files or parallel SSD processing where stream overhead is undesirable.
        /// <para>
        /// <b>Performance:</b> <see cref="RandomAccess.Read"/> is thread-safe for offset reading 
        /// and avoids the overhead of updating a file pointer position.
        /// </para>
        /// </summary>
        public static byte[]? ComputeHashHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, HashType type, byte[] buffer, HashAlgorithm? reusedAlgo)
        {
            try
            {
                long fileLength = RandomAccess.GetLength(handle);
                long position = 0;
                int bytesRead = 0;

                switch (type)
                {
                    case HashType.XXHASH3:
                        var xx3 = new XxHash128();
                        while (position < fileLength)
                        {
                            // Read from specific offset directly into buffer
                            bytesRead = RandomAccess.Read(handle, buffer, position);
                            if (bytesRead == 0) break;
                            xx3.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            position += bytesRead;
                        }
                        return xx3.GetCurrentHash();

                    case HashType.Crc32:
                        var crc = new System.IO.Hashing.Crc32();
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