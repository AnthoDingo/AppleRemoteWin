namespace AppleRemote;

/// <summary>Filtre One Euro (Casiez, Roussel, Vogel, CHI 2012) : passe-bas dont la coupure monte avec la vitesse.</summary>
public sealed class OneEuro
{
    readonly double _minCutoff, _beta, _dCutoff;
    double? _x;
    double _dx;
    double _t;

    public OneEuro(double minCutoff = 1.5, double beta = .004, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    static double Alpha(double cutoff, double dt)
    {
        double r = 2 * Math.PI * cutoff * dt;
        return r / (r + 1);
    }

    public double Filter(double t, double x)
    {
        if (_x is null || t <= _t)
        {
            _x = x;
            _t = t;
            return x;
        }
        double dt = t - _t;
        double dx = (x - _x.Value) / dt;
        _dx += Alpha(_dCutoff, dt) * (dx - _dx);
        _x = _x.Value + Alpha(_minCutoff + _beta * Math.Abs(_dx), dt) * (x - _x.Value);
        _t = t;
        return _x.Value;
    }
}
