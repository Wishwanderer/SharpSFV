using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpSFV
{
    public class ProgressStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly IProgress<double> _progress;
        private readonly long _totalLength;
        private long _bytesRead;

        // OPTIMIZATION: Integer math for reporting thresholds
        private readonly long _bytesPerPercent;
        private long _bytesSinceLastReport;

        public ProgressStream(Stream innerStream, IProgress<double> progress, long length)
        {
            _innerStream = innerStream;
            _progress = progress;
            _totalLength = length;

            // Report every 1% or 1MB, whichever is larger, to avoid event spam on fast SSDs
            _bytesPerPercent = _totalLength / 100;
            if (_bytesPerPercent == 0) _bytesPerPercent = 1024 * 1024;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _innerStream.Read(buffer, offset, count);
            UpdateProgress(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _innerStream.Read(buffer);
            UpdateProgress(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _innerStream.ReadAsync(buffer, cancellationToken);
            UpdateProgress(read);
            return read;
        }

        private void UpdateProgress(int read)
        {
            if (read == 0) return;
            _bytesRead += read;
            _bytesSinceLastReport += read;

            // OPTIMIZATION: Only calc double division when we cross the threshold
            if (_bytesSinceLastReport >= _bytesPerPercent)
            {
                _bytesSinceLastReport = 0;
                double percent = (double)_bytesRead / _totalLength * 100.0;
                _progress.Report(percent);
            }
        }

        // Pass-through implementation
        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }
        public override void Flush() => _innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);
    }
}