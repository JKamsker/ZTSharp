namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpRtoEstimator
{
    private double? _srttMs;
    private double _rttvarMs;

    public TimeSpan Rto { get; set; } = TimeSpan.FromSeconds(1);

    public void Update(TimeSpan rtt)
    {
        var r = rtt.TotalMilliseconds;
        if (r <= 0 || double.IsNaN(r) || double.IsInfinity(r))
        {
            return;
        }

        const double alpha = 1.0 / 8.0;
        const double beta = 1.0 / 4.0;

        if (_srttMs is null)
        {
            _srttMs = r;
            _rttvarMs = r / 2.0;
        }
        else
        {
            var srtt = _srttMs.Value;
            _rttvarMs = (1.0 - beta) * _rttvarMs + beta * Math.Abs(srtt - r);
            _srttMs = (1.0 - alpha) * srtt + alpha * r;
        }

        var rtoMs = _srttMs.Value + Math.Max(1.0, 4.0 * _rttvarMs);
        rtoMs = Math.Clamp(rtoMs, 200.0, 60_000.0);
        Rto = TimeSpan.FromMilliseconds(rtoMs);
    }
}

