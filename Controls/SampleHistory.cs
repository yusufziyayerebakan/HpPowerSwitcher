namespace HpPowerSwitcher.Controls;

/// <summary>Chronological samples in a bounded ring; resizing retains the newest readings.</summary>
public class SampleHistory
{
    private double[] _times;
    private float[] _values;
    private int _head;
    public int Count { get; private set; }
    public int Capacity => _times.Length;
    public SampleHistory(int capacity = 62)
    {
        capacity = Math.Clamp(capacity, 2, 3602);
        _times = new double[capacity]; _values = new float[capacity];
    }
    public void SetCapacity(int capacity)
    {
        capacity = Math.Clamp(capacity, 2, 3602);
        if (capacity == Capacity) return;
        var times = new double[capacity];
        var values = new float[capacity];
        int retained = Math.Min(Count, capacity);
        for (int i = 0; i < retained; i++) { times[i] = TimeAt(Count - retained + i); values[i] = ValueAt(Count - retained + i); }
        _times = times; _values = values; Count = retained; _head = retained % capacity;
    }
    public void Push(double time, double value)
    {
        _times[_head] = time; _values[_head] = (float)value;
        _head = (_head + 1) % Capacity;
        if (Count < Capacity) Count++;
    }
    public double TimeAt(int index) => _times[(_head - Count + index + Capacity) % Capacity];
    public float ValueAt(int index) => _values[(_head - Count + index + Capacity) % Capacity];
    public int LowerBound(double time)
    {
        int lo = 0, hi = Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (TimeAt(mid) < time) lo = mid + 1; else hi = mid; }
        return lo;
    }
}
