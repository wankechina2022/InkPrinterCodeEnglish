using System.Collections.Concurrent;

namespace DominoA200Sdk.Core;

/// <summary>
/// A thread-safe record of the jobs the SDK has submitted but not yet seen complete.
///
/// <para>
/// <b>Why the client needs this.</b> The A200+ caps its on-board FIFO at
/// <see cref="CodenetFrame.FIFO_CAPACITY"/> jobs. The SDK mirrors that queue so that
/// <see cref="DominoA200Client.GetFifoQueueCountAsync"/> can answer locally, and so a
/// print-complete event can be mapped back to the job that produced it.
/// </para>
///
/// <para>
/// <b>Ordering.</b> Jobs leave the queue in submission order (FIFO). Completion may
/// arrive out of order in theory, so <see cref="Complete"/> removes by job id rather
/// than by position.
/// </para>
/// </summary>
public sealed class JobQueue
{
    private readonly ConcurrentDictionary<string, JobQueueEntry> _entries = new ConcurrentDictionary<string, JobQueueEntry>();
    private readonly Queue<string> _order = new Queue<string>();
    private readonly object _orderLock = new object();

    /// <summary>Number of jobs currently awaiting completion.</summary>
    public int Count
    {
        get { return _entries.Count; }
    }

    /// <summary>
    /// True when the mirrored queue has reached the printer's on-board capacity,
    /// meaning the next submission is expected to be rejected with NAK.
    /// </summary>
    public bool IsFull
    {
        get { return _entries.Count >= CodenetFrame.FIFO_CAPACITY; }
    }

    /// <summary>
    /// Add a job to the mirror. Called only after the printer acknowledged the job
    /// with <c>0x06</c>, so the mirror never contains jobs the printer refused.
    /// </summary>
    /// <param name="jobId">The job identifier returned to the caller.</param>
    /// <param name="codeValue">The code text associated with the job.</param>
    public void Enqueue(string jobId, string codeValue)
    {
        JobQueueEntry entry = new JobQueueEntry(jobId, codeValue, DateTime.Now);

        lock (_orderLock)
        {
            if (_entries.TryAdd(jobId, entry))
            {
                _order.Enqueue(jobId);
            }
        }
    }

    /// <summary>
    /// Remove a job from the mirror, typically when its print-complete event arrives.
    /// </summary>
    /// <param name="jobId">The job identifier to remove.</param>
    /// <returns>The removed entry, or <c>null</c> when the id was not queued.</returns>
    public JobQueueEntry? Complete(string jobId)
    {
        if (string.IsNullOrEmpty(jobId))
        {
            return null;
        }

        lock (_orderLock)
        {
            if (!_entries.TryRemove(jobId, out JobQueueEntry? entry))
            {
                return null;
            }

            // Rebuild the ordering queue without the removed id. The queue is tiny
            // (capacity 3), so a linear rebuild is cheaper than a linked-list remove.
            int pending = _order.Count;
            for (int i = 0; i < pending; i++)
            {
                string current = _order.Dequeue();
                if (!string.Equals(current, jobId, StringComparison.Ordinal))
                {
                    _order.Enqueue(current);
                }
            }

            return entry;
        }
    }

    /// <summary>
    /// Remove and return the oldest job still awaiting completion, or <c>null</c>
    /// when the queue is empty.
    /// </summary>
    public JobQueueEntry? DequeueOldest()
    {
        lock (_orderLock)
        {
            while (_order.Count > 0)
            {
                string id = _order.Dequeue();
                if (_entries.TryRemove(id, out JobQueueEntry? entry))
                {
                    return entry;
                }
            }

            return null;
        }
    }

    /// <summary>Discard every tracked job. Used on disconnect and on reconnect.</summary>
    public void Clear()
    {
        lock (_orderLock)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    /// <summary>
    /// Snapshot of the queued job identifiers, oldest first. Exposed for diagnostics
    /// and test assertions.
    /// </summary>
    public IReadOnlyList<string> SnapshotIds()
    {
        lock (_orderLock)
        {
            return _order.ToArray();
        }
    }
}

/// <summary>One entry in the <see cref="JobQueue"/> mirror.</summary>
public sealed class JobQueueEntry
{
    /// <summary>Creates a queue entry.</summary>
    public JobQueueEntry(string jobId, string codeValue, DateTime submittedAt)
    {
        JobId = jobId;
        CodeValue = codeValue;
        SubmittedAt = submittedAt;
    }

    /// <summary>The job identifier assigned by the SDK.</summary>
    public string JobId { get; }

    /// <summary>The code text submitted with the job.</summary>
    public string CodeValue { get; }

    /// <summary>Local time at which the printer acknowledged the job.</summary>
    public DateTime SubmittedAt { get; }
}
