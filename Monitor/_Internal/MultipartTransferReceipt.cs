using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Monitor._Internal
{
  // A receipt describes transmission by the server, never a claim that the
  // browser wrote the attachment to its final local file.
  public sealed class MultipartTransferReceipt
  {
    public sealed record State(string Status, long BytesSent, long TotalBytes);
    private readonly long _totalBytes;
    private long _bytesSent;
    private int _state; // 0 sending, 1 sent, 2 failed

    public MultipartTransferReceipt(long totalBytes)
    {
      if (totalBytes < 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
      _totalBytes = totalBytes;
    }

    public bool IsSending => Volatile.Read(ref _state) == 0;
    public State Snapshot()
    {
      int state = Volatile.Read(ref _state);
      return new State(state == 1 ? "sent" : state == 2 ? "failed" : "sending",
        Interlocked.Read(ref _bytesSent), _totalBytes);
    }

    public void Fail() => Interlocked.CompareExchange(ref _state, 2, 0);

    public async Task SendAsync(Stream source, Stream destination, Func<Task> completeResponse,
      CancellationToken cancellationToken)
    {
      try
      {
        var buffer = new byte[81920];
        long remaining = _totalBytes;
        while (remaining > 0)
        {
          cancellationToken.ThrowIfCancellationRequested();
          int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
            cancellationToken).ConfigureAwait(false);
          if (read == 0) throw new EndOfStreamException("Prepared ZIP shortened during transmission.");
          await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
          remaining -= read;
          Interlocked.Exchange(ref _bytesSent, _totalBytes - remaining);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        await completeResponse().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.CompareExchange(ref _state, 1, 0);
      }
      catch
      {
        Fail();
        throw;
      }
    }
  }
}
