namespace GlDrive.Spread;

public class SpeedTracker
{
    private readonly Dictionary<(string src, string dst), Queue<double>> _speeds = new();
    private readonly Lock _lock = new();
    private const int MaxSamples = 10;

    public void RecordTransfer(string srcId, string dstId, long bytes, TimeSpan duration)
    {
        if (duration.TotalSeconds < 0.1 || bytes <= 0) return;

        var key = (srcId, dstId);
        var speed = bytes / duration.TotalSeconds;

        lock (_lock)
        {
            if (!_speeds.TryGetValue(key, out var queue))
            {
                queue = new Queue<double>();
                _speeds[key] = queue;
            }
            if (queue.Count >= MaxSamples)
                queue.Dequeue();
            queue.Enqueue(speed);
        }
    }

    public double GetAverageSpeed(string srcId, string dstId)
    {
        var key = (srcId, dstId);
        lock (_lock)
        {
            if (!_speeds.TryGetValue(key, out var queue) || queue.Count == 0)
                return 0;
            return queue.Average();
        }
    }
}
