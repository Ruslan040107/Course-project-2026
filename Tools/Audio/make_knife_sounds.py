# Knife sounds like in CS2, cut and mixed from CC0 recordings (see CREDITS.md).
# usage: python make_knife_sounds.py <downloads folder> <Assets/Resources/Audio> [only]
#   <downloads folder> holds the packs below (any layout, the files are found by name); the .ogg/.flac files
#   are decoded to .wav first with decode_blender.py ("x.ogg" becomes "x.dec.wav"; a plain "x.wav" works too):
#     swosh-NN            "Swish - bamboo stick weapon swhoshes", qubodup (opengameart.org), CC0
#     machete_vs_spatula_N "Machete vs. Spatula", cyberdevil (opengameart.org), CC0 (saved as m_N.wav works too)
#     wet_towel_on_floor-NN "12 wet towel hitting/falling/punching floor sounds", qubodup (opengameart.org), CC0
#     knifeSlice2, metalClick, metalLatch, beltHandle1/2   Kenney "RPG Audio" (kenney.nl), CC0
#     impactPunch_heavy_00N                                Kenney "Impact Sounds" (kenney.nl), CC0
#   [only] = a name prefix (air, wall, body, stab, flip) to make only those.
# Output: Knife/air_N.wav   the blade cutting the air: a soft short whoosh (swept band noise made here + a real
#                           swoosh under it), low-mid, no sharp highs, quiet (peak 0.4)
#         Knife/wall_N.wav  the blade hitting a wall or the floor: a metallic hit and a short scrape
#         Knife/body_N.wav  a slash hitting a player: a wet slap, a bit of the cut and a low thump
#         Knife/stab_N.wav  the heavy stab: the same, lower and heavier, with a squelch after it
#         Knife/flip_N.wav  the butterfly knife flip on the draw: a few light metallic clicks
# 48 kHz mono 16 bit, trimmed, short fades.
import sys, os, numpy as np
from fractions import Fraction
from scipy.io import wavfile
from scipy.signal import resample_poly, butter, sosfilt
import warnings; warnings.filterwarnings('ignore')

root, out = sys.argv[1], sys.argv[2]
only = sys.argv[3] if len(sys.argv) > 3 else ''
SR = 48000
files = {}
for dp, dn, fn in os.walk(root):
    for f in fn:
        if f.lower().endswith('.wav') and '__MACOSX' not in dp:
            files[f[:-4].replace('.dec', '')] = os.path.join(dp, f)
cache = {}

# ---------------- reading ----------------

def load(name):
    if name not in cache:
        sr, x = wavfile.read(files[name])
        scale = {np.dtype('int16'): 32768.0, np.dtype('int32'): 2147483648.0}.get(x.dtype, 1.0)
        x = x.astype(np.float64) / scale
        if x.ndim > 1: x = x.mean(axis=1)
        cache[name] = (sr, x)
    return cache[name]

# a part of a recording by time, at 48 kHz; pitch > 1 plays it higher and shorter
def part(name, t0=0.0, t1=None, pitch=1.0):
    sr, x = load(name)
    seg = x[int(t0 * sr):int(t1 * sr) if t1 else len(x)]
    ratio = Fraction(SR / (sr * pitch)).limit_denominator(400)
    return resample_poly(seg, ratio.numerator, ratio.denominator)

# ---------------- filters and helpers ----------------

def hp(x, f, order=2): return sosfilt(butter(order, f, 'highpass', fs=SR, output='sos'), x)
def lp(x, f, order=2): return sosfilt(butter(order, f, 'lowpass', fs=SR, output='sos'), x)
def bp(x, f1, f2, order=2): return sosfilt(butter(order, [f1, f2], 'bandpass', fs=SR, output='sos'), x)
def norm(y, peak=1.0): return y / (np.abs(y).max() + 1e-12) * peak

# loudness over time (10 ms windows)
def envelope(y, win=0.01):
    n = int(win * SR)
    return np.sqrt(np.convolve(y ** 2, np.ones(n) / n, mode='same'))

# after `keep` seconds the sound dies out with the time constant `decay`
def tail(y, keep, decay):
    t = np.arange(len(y)) / SR
    return y * np.exp(-np.maximum(t - keep, 0) / decay)

# sounds laid over each other: (start s, gain, sound) ...
def mix(*layers):
    n = max(int(t * SR) + len(y) for t, g, y in layers)
    res = np.zeros(n)
    for t, g, y in layers: res[int(t * SR):int(t * SR) + len(y)] += g * y
    return res

def gap(s): return np.zeros(int(s * SR))

# trimmed to where it is heard, short fades, peak `peak`
def write(rel, y, peak, fin=0.002, fout=0.03):
    env = np.abs(y); pk = env.max() + 1e-12
    a = max(0, np.where(env > 0.03 * pk)[0][0] - int(0.002 * SR))
    b = min(len(y), np.where(env > 0.01 * pk)[0][-1] + int(0.005 * SR))
    y = y[a:b].copy()
    n = min(int(fin * SR), len(y) // 4); y[:n] *= np.linspace(0, 1, n)
    n = min(int(fout * SR), len(y) // 3); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    y = norm(y, peak)
    path = os.path.join(out, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (np.clip(y, -1, 1) * 32767).astype(np.int16))
    print('%-22s %.3f s  peak %.2f' % (rel.replace(os.sep, '/'), len(y) / SR, np.abs(y).max()))

# ---------------- air: the swing that hits nothing ----------------

# a smooth rise and fall: up for `up` seconds, then down to the end
def swell(n, up):
    t = np.arange(n) / SR; T = n / SR
    return np.where(t < up, np.sin(0.5 * np.pi * t / up) ** 2,
                    np.cos(0.5 * np.pi * np.clip((t - up) / (T - up), 0, 1)) ** 1.6)

# made here: noise whose band slides up while the blade comes and down while it goes (like a thing passing by),
# low-mid only (about 350-1800 Hz), with a smooth swell; built frame by frame in the frequency domain
def synth_whoosh(seed, length=0.24, up=0.1, f0=450, f1=1300, f2=600):
    rng = np.random.default_rng(seed)
    n = int(length * SR); hop = 256; frame = 1024
    frames = n // hop + 4
    freqs = np.fft.rfftfreq(frame, 1 / SR)
    win = np.hanning(frame)
    res = np.zeros(frames * hop + frame)
    for k in range(frames):
        u = min(k * hop / n, 1.0); peak = up / length
        fc = f0 + (f1 - f0) * np.sin(0.5 * np.pi * u / peak) ** 2 if u < peak else              f2 + (f1 - f2) * np.cos(0.5 * np.pi * (u - peak) / (1 - peak)) ** 2
        mag = np.exp(-0.5 * (np.log(freqs / fc + 1e-9) / 0.45) ** 2)        # a soft band around fc
        mag += 0.25 * np.exp(-0.5 * (np.log(freqs / 260 + 1e-9) / 0.35) ** 2)  # a little low "whoom"
        spec = mag * np.exp(2j * np.pi * rng.random(len(freqs)))
        res[k * hop:k * hop + frame] += np.fft.irfft(spec) * win
    y = hp(res[:n], 200, 2)
    return y * swell(n, up)

# a real bamboo stick swoosh (qubodup), higher (a short blade), its low-mid band only (the wind pops of the
# microphone below 300 Hz and the hiss on top cut), its loudest point put at the peak of the swell
def real_whoosh(name, pitch, length=0.24, up=0.1):
    y = lp(hp(part(name, pitch=pitch), 300, 4), 3500, 2)
    peak = int(np.argmax(envelope(y, 0.015)))
    res = np.zeros(int(length * SR))
    a = peak - int(up * SR)                     # where the recording starts in the result (may be before 0)
    seg = y[max(0, a):max(0, a) + len(res) - max(0, -a)]
    res[max(0, -a):max(0, -a) + len(seg)] = seg
    return res * swell(len(res), up)

if 'air'.startswith(only) or only == '':
    for i, (name, pitch, seed, f1, length) in enumerate([('swosh-12', 1.15, 1, 1300, 0.24), ('swosh-17', 1.2, 2, 1150, 0.22),
                                                         ('swosh-22', 1.12, 3, 1450, 0.26), ('swosh-42', 1.18, 4, 1250, 0.23)]):
        up = length * 0.42
        y = norm(synth_whoosh(seed, length, up, f1=f1)) + 0.6 * norm(real_whoosh(name, pitch, length, up))
        write('Knife/air_%d.wav' % i, y, 0.4, fin=0.004, fout=0.03)

# ---------------- wall: the blade hits a hard surface ----------------

# the machete on a metal spatula: a sharp clink and a short scrape ("chirk"); the long ring of the spatula
# is cut short (a wall does not ring), the low part off
def wall(name, pitch, keep, decay):
    y = part(name, 0.0, 0.5, pitch)
    y = tail(hp(y, 350, 4), keep, decay)
    return lp(y, 10000)

if 'wall'.startswith(only) or only == '':
    for i, (name, pitch, keep, decay) in enumerate([('machete_vs_spatula_11', 1.0, 0.06, 0.04),
                                                    ('machete_vs_spatula_13', 1.06, 0.05, 0.04),
                                                    ('machete_vs_spatula_2', 0.97, 0.1, 0.035),
                                                    ('machete_vs_spatula_14', 1.03, 0.09, 0.035)]):
        if name not in files: name = 'm_' + name.split('_')[-1]
        write('Knife/wall_%d.wav' % i, wall(name, pitch, keep, decay), 0.6)

# ---------------- body and stab: the blade hits a player ----------------

# the wet slap of a towel on the floor (the flesh), a short piece of the knife slice on top (the cut) and a
# damped low punch under it (the weight); low-passed so it is dull and wet, not a click
def flesh(towel, pitch, cut_t, punch, punch_gain, cut_gain=0.4, length=0.3):
    wet = lp(hp(part(towel, 0.0, length, pitch), 90), 6000)
    wet = tail(wet, 0.04, 0.07)
    cut = part('knifeSlice2', cut_t, cut_t + 0.09, 1.15)
    cut = bp(cut, 900, 7000) * np.exp(-np.arange(len(cut)) / (0.025 * SR))
    thump = lp(part(punch, 0.0, 0.25, 0.9), 450)
    thump = tail(thump, 0.02, 0.05)
    return mix((0, 1.0, norm(wet)), (0.004, cut_gain, norm(cut)), (0, punch_gain, norm(thump)))

if 'body'.startswith(only) or only == '':
    for i, (towel, pitch, cut_t, punch) in enumerate([('wet_towel_on_floor-03', 1.05, 0.22, 'impactPunch_heavy_000'),
                                                      ('wet_towel_on_floor-07', 1.0, 0.25, 'impactPunch_heavy_002'),
                                                      ('wet_towel_on_floor-09', 1.1, 0.28, 'impactPunch_heavy_001')]):
        write('Knife/body_%d.wav' % i, flesh(towel, pitch, cut_t, punch, 0.5), 0.65)

if 'stab'.startswith(only) or only == '':
    for i, (towel, pitch, punch, squelch) in enumerate([('wet_towel_on_floor-07', 0.85, 'impactPunch_heavy_000', 'wet_towel_on_floor-11'),
                                                        ('wet_towel_on_floor-03', 0.82, 'impactPunch_heavy_003', 'wet_towel_on_floor-09')]):
        hit = flesh(towel, pitch, 0.0, punch, 0.85, cut_gain=0.5, length=0.35)
        # the blade pulled out: a lower, darker wet sound a moment later
        sq = lp(hp(part(squelch, 0.06, 0.26, 0.72), 120), 2200)
        sq = sq * np.sin(np.pi * np.arange(len(sq)) / len(sq)) ** 2
        write('Knife/stab_%d.wav' % i, mix((0, 1.0, norm(hit)), (0.1, 0.35, norm(sq))), 0.7, fout=0.06)

# ---------------- flip: the butterfly knife on the draw ----------------

# light clicks only: the low part off, a bit higher
def click(name, t0, t1, pitch): return hp(part(name, t0, t1, pitch), 900, 4)

if 'flip'.startswith(only) or only == '':
    write('Knife/flip_0.wav', click('beltHandle2', 0.07, 0.42, 1.12), 0.6)
    write('Knife/flip_1.wav', click('beltHandle1', 0.0, 0.25, 1.08), 0.6)
    write('Knife/flip_2.wav', np.concatenate([
        norm(click('metalClick', 0.085, 0.15, 1.3), 0.7), gap(0.05),
        norm(click('metalLatch', 0.03, 0.12, 1.35), 0.8), gap(0.07),
        norm(click('metalClick', 0.255, 0.33, 1.2))]), 0.6)
