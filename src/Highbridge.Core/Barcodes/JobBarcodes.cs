using System.Text.Json.Serialization;
using Highbridge.Scanning;

namespace Highbridge.Barcodes;

[JsonConverter(typeof(CamelCaseEnumConverter<BarcodeStatus>))]
public enum BarcodeStatus
{
    /// <summary>Queued or being analysed.</summary>
    Pending,

    /// <summary>Every rotation was searched.</summary>
    Done,

    /// <summary>The page's time budget ran out; some rotations were not searched.</summary>
    Partial,

    /// <summary>The page could not be analysed (see the highbridge log).</summary>
    Failed,

    /// <summary>Not analysed: the scan was canceled or detection did not finish in time.</summary>
    Skipped,
}

public sealed record PageBarcodes(BarcodeStatus Status, IReadOnlyList<BarcodeResult> Barcodes)
{
    public static PageBarcodes Pending { get; } = new(BarcodeStatus.Pending, []);

    public static PageBarcodes Skipped { get; } = new(BarcodeStatus.Skipped, []);
}

/// <summary>Barcode results of one scan job, filled in by <see cref="BarcodeWorker"/> as pages arrive.</summary>
public sealed class JobBarcodes
{
    private readonly object _gate = new();
    private readonly SortedDictionary<int, PageBarcodes> _pages = [];
    private readonly List<Task> _work = [];

    public JobBarcodes(BarcodeFormat formats)
    {
        Formats = formats;
    }

    public BarcodeFormat Formats { get; }

    /// <summary>Results for one page; <see cref="PageBarcodes.Pending"/> until it has been analysed.</summary>
    public PageBarcodes ForPage(int index)
    {
        lock (_gate)
        {
            return _pages.GetValueOrDefault(index, PageBarcodes.Pending);
        }
    }

    /// <summary>Every barcode found so far, in page order.</summary>
    public IReadOnlyList<BarcodeResult> All
    {
        get
        {
            lock (_gate)
            {
                return _pages.Values.SelectMany(p => p.Barcodes).ToArray();
            }
        }
    }

    /// <summary>Hands <paramref name="page"/> to the worker. Call with the page as the scanner delivered it.</summary>
    public void Enqueue(ScannedPage page, BarcodeWorker worker, CancellationToken cancellationToken)
    {
        // Copy now, on the scanner's thread: detection runs later, and a later step (a stamp) may
        // change the page bytes. The copy is what was scanned.
        var original = new ScannedPage(page.Index, page.MediaType, page.Width, page.Height, page.Data.ToArray());
        lock (_gate)
        {
            _pages[page.Index] = PageBarcodes.Pending;
            _work.Add(worker.EnqueueAsync(original, Formats, cancellationToken).ContinueWith(
                t => Set(page.Index, t.Result),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
        }
    }

    /// <summary>
    /// Waits for every queued page, at most <paramref name="timeout"/>; pages still pending after
    /// that are marked <see cref="BarcodeStatus.Skipped"/>.
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time)
    {
        Task[] work;
        lock (_gate)
        {
            work = _work.ToArray();
        }

        try
        {
            await Task.WhenAll(work).WaitAsync(timeout, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                foreach (int index in _pages.Where(p => p.Value.Status == BarcodeStatus.Pending).Select(p => p.Key).ToArray())
                {
                    _pages[index] = PageBarcodes.Skipped;
                }
            }
        }
    }

    private void Set(int index, PageBarcodes result)
    {
        lock (_gate)
        {
            if (_pages.TryGetValue(index, out var current) && current.Status == BarcodeStatus.Pending)
            {
                _pages[index] = result;
            }
        }
    }
}
