namespace LVA.Nlu.Intents;

/// <summary>
/// M-of-N hysteresis filter (TZ §4.3): a state is entered when M of the last N
/// scores reach the threshold, and exited symmetrically. Eliminates flicker
/// on partial hypotheses.
/// </summary>
public sealed class HysteresisFilter
{
    private readonly int _m, _n;
    private readonly float _threshold;
    private readonly Queue<bool> _window = new();
    private bool _active;

    public HysteresisFilter(int m, int n, float threshold)
    {
        _m = m; _n = n; _threshold = threshold;
    }

    public bool IsActive => _active;

    /// <summary>Feeds a new score; returns the filtered state.</summary>
    public bool Update(float score)
    {
        _window.Enqueue(score >= _threshold);
        if (_window.Count > _n)
            _window.Dequeue();

        int hits = _window.Count(b => b);
        if (!_active && hits >= _m)
            _active = true;
        else if (_active && _window.Count == _n && hits < _m)
            _active = false;
        return _active;
    }

    public void Reset()
    {
        _window.Clear();
        _active = false;
    }
}
