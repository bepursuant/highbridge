using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Highbridge.Imaging;
using Highbridge.Scanning;

namespace Highbridge.Barcodes;

/// <summary>
/// Runs barcode detection in the background, on its own below-normal-priority threads, so the
/// TWAIN thread and the next pages are never held up. At most <see cref="Parallelism"/> pages are
/// analysed at once and each gets a time budget. Threads start on first use.
/// </summary>
public sealed class BarcodeWorker : IDisposable
{
    public static readonly TimeSpan DefaultPageBudget = TimeSpan.FromSeconds(3);

    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleWriter = false });
    private readonly ILogger _logger;
    private readonly object _startLock = new();
    private bool _started;

    public BarcodeWorker(int? parallelism = null, TimeSpan? pageBudget = null, ILogger<BarcodeWorker>? logger = null)
    {
        Parallelism = parallelism ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 2);
        PageBudget = pageBudget ?? DefaultPageBudget;
        _logger = logger ?? NullLogger<BarcodeWorker>.Instance;
    }

    public int Parallelism { get; }

    public TimeSpan PageBudget { get; }

    /// <summary>Queues <paramref name="page"/>; the task completes with its barcodes and never faults.</summary>
    public Task<PageBarcodes> EnqueueAsync(ScannedPage page, BarcodeFormat formats, CancellationToken cancellationToken)
    {
        EnsureStarted();
        var item = new Item(page, formats, cancellationToken, new TaskCompletionSource<PageBarcodes>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_queue.Writer.TryWrite(item))
        {
            item.Result.TrySetResult(PageBarcodes.Skipped);
        }

        return item.Result.Task;
    }

    public void Dispose() => _queue.Writer.TryComplete();

    private void EnsureStarted()
    {
        lock (_startLock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            for (int i = 0; i < Parallelism; i++)
            {
                new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"highbridge-barcodes-{i + 1}" }.Start();
            }
        }
    }

    private void Run()
    {
        var reader = _queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var item))
            {
                item.Result.TrySetResult(Process(item));
            }
        }
    }

    private PageBarcodes Process(Item item)
    {
        if (item.Cancellation.IsCancellationRequested)
        {
            return PageBarcodes.Skipped;
        }

        try
        {
            var image = PngDecoder.DecodeLuminance(item.Page.Data);
            var detection = BarcodeDetector.Detect(image, item.Page.Index, item.Formats, PageBudget, item.Cancellation);
            var status = detection.Outcome == BarcodeDetectionOutcome.Complete ? BarcodeStatus.Done : BarcodeStatus.Partial;
            _logger.LogInformation("Barcodes on page {Page}: {Count} in {Milliseconds:F0} ms ({Passes} passes, {Status})",
                item.Page.Index + 1, detection.Barcodes.Count, detection.Elapsed.TotalMilliseconds, detection.Passes, status);
            return new PageBarcodes(status, detection.Barcodes);
        }
        catch (OperationCanceledException)
        {
            return PageBarcodes.Skipped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Barcode detection failed on page {Page}", item.Page.Index + 1);
            return new PageBarcodes(BarcodeStatus.Failed, []);
        }
    }

    private sealed record Item(ScannedPage Page, BarcodeFormat Formats, CancellationToken Cancellation, TaskCompletionSource<PageBarcodes> Result);
}
