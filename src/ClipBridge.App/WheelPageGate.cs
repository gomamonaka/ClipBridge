using System;

namespace ClipBridge.App;

/// <summary>Converts standard and high resolution wheel input into bounded page actions.</summary>
internal sealed class WheelPageGate
{
    private int _accumulated, _direction;
    private long _lastNavigation = long.MinValue, _lastEvent = long.MinValue;
    internal void Reset() { _accumulated = 0; _direction = 0; _lastNavigation = _lastEvent = long.MinValue; }
    internal bool TryStep(int delta, long timestampMilliseconds, out int direction)
    {
        direction = Math.Sign(delta);
        if (direction == 0) return false;
        bool reverse = _direction != 0 && _direction != direction;
        if (_lastEvent != long.MinValue && (timestampMilliseconds < _lastEvent || timestampMilliseconds - _lastEvent > 500)) _accumulated = 0;
        if (reverse) _accumulated = 0;
        _direction = direction; _lastEvent = timestampMilliseconds;
        long magnitude = Math.Abs((long)delta);
        int debounce = magnitude == 120 ? 40 : 80;
        if (!reverse && _lastNavigation != long.MinValue && timestampMilliseconds >= _lastNavigation && timestampMilliseconds - _lastNavigation < debounce)
        { _accumulated = 0; return false; }
        _accumulated += direction * (int)Math.Min(120, magnitude);
        if (Math.Abs(_accumulated) < 120) return false;
        _accumulated = 0; _lastNavigation = timestampMilliseconds;
        // Positive wheel rotation is up; page/group order moves in the opposite direction.
        direction = -direction;
        return true;
    }
}
