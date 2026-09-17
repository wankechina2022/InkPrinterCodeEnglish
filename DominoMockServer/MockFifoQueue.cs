using System.Collections.Concurrent;

namespace DominoMockServer;

/// <summary>
/// Simulates the A200+ on-board FIFO queue.
///
/// <para>
/// The real printer holds at most three print jobs. A fourth submission is refused
/// with <c>0x15</c> (NAK) until one of the queued jobs finishes and reports
/// <c>0x32</c>. This class reproduces that admission rule so the SDK can be exercised
/// against a saturated queue without owning a physical machine.
/// </para>
/// </summary>
public sealed class MockFifoQueue
{
    /// <summary>Mirrors the hardware limit: three concurrent jobs.</summary>
    public const int CAPACITY = 3;

    private readonly ConcurrentQueue<MockJob> _queue = new ConcurrentQueue<MockJob>();
    private readonly object _sync = new object();

    /// <summary>Number of jobs currently held.</summary>
    public int Count
    {
        get { return _queue.Count; }
    }

    /// <summary>True when the queue has reached <see cref="CAPACITY"/>.</summary>
    public bool IsFull
    {
        get { return _queue.Count >= CAPACITY; }
    }

    /// <summary>
    /// Attempt to admit a job.
    /// </summary>
    /// <param name="job">The job to admit.</param>
    /// <returns><c>true</c> when accepted; <c>false</c> when the queue is full and the
    /// caller should answer NAK.</returns>
    public bool TryEnqueue(MockJob job)
    {
        lock (_sync)
        {
            if (_queue.Count >= CAPACITY)
            {
                return false;
            }

            _queue.Enqueue(job);
            return true;
        }
    }

    /// <summary>
    /// Remove and return the oldest queued job, representing the one that has just
    /// finished printing.
    /// </summary>
    /// <returns>The completed job, or <c>null</c> when the queue is empty.</returns>
    public MockJob? DequeueOldest()
    {
        lock (_sync)
        {
            if (_queue.TryDequeue(out MockJob? job))
            {
                return job;
            }

            return null;
        }
    }

    /// <summary>Discard all queued jobs, simulating a queue clear.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            while (_queue.TryDequeue(out _))
            {
                // Drain.
            }
        }
    }
}

/// <summary>A job held by the mock FIFO, standing in for a real print job.</summary>
public sealed class MockJob
{
    /// <summary>Creates a mock job.</summary>
    public MockJob(string jobId, string payload)
    {
        JobId = jobId;
        Payload = payload;
    }

    /// <summary>Identifier assigned by the mock.</summary>
    public string JobId { get; }

    /// <summary>The code text carried by the job.</summary>
    public string Payload { get; }
}
