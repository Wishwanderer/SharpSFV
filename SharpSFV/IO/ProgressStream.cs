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

        // Optimization: Throttling
        private readonly long _bytesPerStep; // 0.1% resolution
        private long _bytesSinceLastReport;
        private long _lastReportTick;
        private const int ReportIntervalMs = 100; // Cap updates to 10fps

        public ProgressStream(Stream innerStream, IProgress<double> progress, long length)
        {
            _innerStream = innerStream;
            _progress = progress;
            _totalLength = length;

            // Target 0.1% resolution (1000 steps)
            _bytesPerStep = _totalLength / 1000;
            if (_bytesPerStep == 0) _bytesPerStep = 64 * 1024; // Min reporting chunk 64KB

            _lastReportTick = Environment.TickCount64;
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

            // 1. Check if we have processed enough data for a 0.1% increment
            if (_bytesSinceLastReport >= _bytesPerStep)
            {
                // 2. Check if enough time has passed (Throttling)
                long now = Environment.TickCount64;
                if ((now - _lastReportTick) >= ReportIntervalMs)
                {
                    _bytesSinceLastReport = 0;
                    _lastReportTick = now;

                    double percent = (double)_bytesRead / _totalLength * 100.0;
                    _progress.Report(percent);
                }
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