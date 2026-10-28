#pragma once
#include <cmath>
#include <cstdint>
#include <complex>
#include <cstring>

namespace peq {
constexpr int MaxBands = 64, MaxChannels = 8;
constexpr double Pi = 3.14159265358979323846;
struct Band { int32_t enabled, type; double frequency, gain, q; };
struct Header { uint32_t magic, version; int64_t revision; int32_t enabled, count; double preamp; };
static_assert(sizeof(Band) == 32 && sizeof(Header) == 32);
struct Settings { Header header; Band bands[MaxBands]; };
inline bool valid(const Settings& s, size_t bytes) {
    const auto& h = s.header;
    if (h.magic != 0x31514550 || h.version != 1 || h.count < 0 || h.count > MaxBands ||
        bytes != sizeof(Header) + h.count * sizeof(Band) || (h.enabled != 0 && h.enabled != 1) ||
        !std::isfinite(h.preamp) || h.preamp < -60 || h.preamp > 12) return false;
    for (int i = 0; i < h.count; ++i) {
        const auto& b = s.bands[i];
        if ((b.enabled != 0 && b.enabled != 1) || b.type < 0 || b.type > 5 ||
            !std::isfinite(b.frequency) || b.frequency < 10 || b.frequency > 22000 ||
            !std::isfinite(b.gain) || b.gain < -30 || b.gain > 30 ||
            !std::isfinite(b.q) || b.q < 0.1 || b.q > 30 || (b.type>=3 && b.gain!=0)) return false;
    }
    return true;
}
struct Coefficients { double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0; };
// Robert Bristow-Johnson Audio EQ Cookbook, normalized biquad equations.
inline Coefficients coefficients(const Band& b, double rate) {
    const double w = 2 * Pi * b.frequency / rate, c = cos(w), alpha = sin(w) / (2 * b.q);
    const double A = pow(10, b.gain / 40), beta = 2 * sqrt(A) * alpha;
    double b0, b1, b2, a0, a1, a2;
    switch (b.type) {
    case 0: b0=1+alpha*A; b1=-2*c; b2=1-alpha*A; a0=1+alpha/A; a1=-2*c; a2=1-alpha/A; break;
    case 1: b0=A*((A+1)-(A-1)*c+beta); b1=2*A*((A-1)-(A+1)*c); b2=A*((A+1)-(A-1)*c-beta);
            a0=(A+1)+(A-1)*c+beta; a1=-2*((A-1)+(A+1)*c); a2=(A+1)+(A-1)*c-beta; break;
    case 2: b0=A*((A+1)+(A-1)*c+beta); b1=-2*A*((A-1)+(A+1)*c); b2=A*((A+1)+(A-1)*c-beta);
            a0=(A+1)-(A-1)*c+beta; a1=2*((A-1)-(A+1)*c); a2=(A+1)-(A-1)*c-beta; break;
    case 3: b0=(1-c)/2; b1=1-c; b2=b0; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
    case 4: b0=(1+c)/2; b1=-(1+c); b2=b0; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
    default: b0=1; b1=-2*c; b2=1; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
    }
    return {b0/a0,b1/a0,b2/a0,a1/a0,a2/a0};
}
inline double response(const Settings& s, double frequency, double rate) {
    if (!s.header.enabled) return 0;
    auto z = std::polar(1.0, -2 * Pi * frequency / rate);
    double db = s.header.preamp;
    for (int i=0; i<s.header.count; ++i) if (s.bands[i].enabled && s.bands[i].frequency < rate/2) {
        const auto c=coefficients(s.bands[i],rate);
        db += 20*log10(std::abs((c.b0+c.b1*z+c.b2*z*z)/(1.0+c.a1*z+c.a2*z*z)));
    }
    return db;
}
struct Dsp {
    int count = 0; int64_t revision = 0; double gain = 1;
    Coefficients filters[MaxBands];
    double state[MaxBands][MaxChannels][2]{};
    explicit Dsp(const Settings& s, double rate) : revision(s.header.revision) {
        if (!s.header.enabled) return;
        gain = pow(10, s.header.preamp/20);
        for (int i=0; i<s.header.count; ++i) if (s.bands[i].enabled) {
            // An unrepresentable band is an invalid configuration at this sample rate.
            if (s.bands[i].frequency >= rate/2) { count=-1; return; }
            filters[count++] = coefficients(s.bands[i],rate);
        }
    }
    double sample(double input, int channel) {
        double x=input*gain;
        for (int i=0; i<count; ++i) {
            const auto& c=filters[i]; auto& z=state[i][channel];
            double y=c.b0*x+z[0]; z[0]=c.b1*x-c.a1*y+z[1]; z[1]=c.b2*x-c.a2*y; x=y;
        }
        return x;
    }
    void reset() { memset(state,0,sizeof(state)); }
};
}
