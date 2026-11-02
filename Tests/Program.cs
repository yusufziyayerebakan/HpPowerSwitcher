using HpPowerSwitcher.Controls;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var history = new SampleHistory(3);
Check(history.Count == 0 && history.LowerBound(0) == 0, "Empty history search");
for (int i = 0; i < 10; i++) history.Push(i, i * 10);
Check(history.Count == 3 && history.Capacity == 3, "History must stay bounded after wrapping");
Check(history.TimeAt(0) == 7 && history.TimeAt(2) == 9 && history.ValueAt(1) == 80, "Wrapped samples must remain chronological");
Check(history.LowerBound(6) == 0 && history.LowerBound(7.5) == 1 && history.LowerBound(10) == 3, "Search boundaries after wrapping");
history.SetCapacity(6);
history.Push(10, 100);
Check(history.Count == 4 && history.TimeAt(0) == 7 && history.TimeAt(3) == 10, "Growing must preserve readings and append position");
history.SetCapacity(2);
Check(history.Count == 2 && history.TimeAt(0) == 9 && history.TimeAt(1) == 10, "Shrinking must retain newest samples");
history.Push(11, double.NaN);
Check(history.TimeAt(0) == 10 && float.IsNaN(history.ValueAt(1)), "Unavailable readings must survive wrapping");
history.SetCapacity(int.MaxValue);
Check(history.Capacity == 3602 && history.Count == 2 && history.TimeAt(1) == 11, "Maximum history allocation is capped");
history.SetCapacity(0);
Check(history.Capacity == 2 && history.Count == 2, "Minimum history allocation is safe");
Console.WriteLine("Passed: bounded capacity, wrap order, search boundaries, grow/shrink retention, unavailable readings.");
