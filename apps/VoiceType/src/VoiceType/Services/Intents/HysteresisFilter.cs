namespace VoiceType.Services.Intents;

/// <summary>
/// M-of-N hysteresis filter: a state is entered when M of the last N scores
/// reach the threshold and exited symmetrically. Prevents flicker of command
/// detection on partial ASR hypotheses.
/// </summary>
public sealed class HysteresisFilter
{
    private readonly int _m, _n;
    private readonly float _threshold;
    private readonly Queue<bool> _window = new();
    private bool _active;

    public HysteresisFilter(int m, int n, float threshold)
    { _m = Math.Max(1, m); _n = Math.Max(_m, n); _threshold = threshold; }

    public bool IsActive => _active;

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

    public void Reset() { _window.Clear(); _active = false; }
}
