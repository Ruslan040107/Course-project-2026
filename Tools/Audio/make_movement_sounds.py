# Jump and landing sounds, layered from CC0 recordings of freesound.org (see CREDITS.md):
#   494797 brandondelehoy "Jacket/Cloth Rustle 9"           464488 elynch0901 "Jumping Human"
#   170504 SpliceSound "Footsteps, Tile, Male Sneakers, Jumping"
#   170499 SpliceSound "Footsteps, Tile, Male Tennis Shoes, Scuffs"
#   689126 Pelouta "Footstes sneaker jump"                  422753, 422754 KieranKeegan "Landing_Stone_1/2"
#   646660 SoundDesignForYou "Landing On The Ground [2]"    464527 pcmac "jump.wav"
# usage: python make_movement_sounds.py <folder with <freesound id>.wav> <Assets/Resources/Audio>
# (the freesound previews decoded to wav, e.g. with decode_blender.py; any rate, mono or stereo)
# Output: Movement/jump_N.wav (the push off: a cloth rustle + a light shoe scuff, no voice)
#         Movement/land_N.wav (the landing: shoes on a hard floor + a body thud + a short cloth flap)
# 48 kHz mono 16 bit, short fades.
import sys, os, numpy as np
from fractions import Fraction
from scipy.io import wavfile
from scipy.signal import resample_poly, butter, sosfilt
import warnings; warnings.filterwarnings('ignore')

root, out = sys.argv[1], sys.argv[2]
SR = 48000
cache = {}

def load(sid):
    if sid not in cache:
        sr, x = wavfile.read(os.path.join(root, '%d.wav' % sid))
        x = x.astype(np.float64) / 32768.0
        if x.ndim > 1: x = x.mean(axis=1)
        r = Fraction(SR, sr)
        cache[sid] = resample_poly(x, r.numerator, r.denominator) if sr != SR else x
    return cache[sid]

def hp(x, f, order=2): return sosfilt(butter(order, f, 'highpass', fs=SR, output='sos'), x)
def lp(x, f, order=2): return sosfilt(butter(order, f, 'lowpass', fs=SR, output='sos'), x)

# a part of a recording that starts just before its attack: the first sample above frac of the peak
# of the window [at, at + search], minus 3 ms; normalized to peak 1
def part(sid, at, length, frac=0.2, search=0.3):
    x = load(sid)
    a = int(at * SR); w = x[a:a + int(search * SR)]
    on = np.where(np.abs(w) > frac * np.abs(w).max())[0][0]
    a = max(0, a + on - int(0.003 * SR))
    y = x[a:a + int(length * SR)].copy()
    n = int(0.002 * SR); y[:n] *= np.linspace(0, 1, n)
    return y / (np.abs(y).max() + 1e-12)

# higher pitch = shorter (like a faster tape)
def pitch(y, p):
    if p == 1: return y
    r = Fraction(1 / p).limit_denominator(200)
    return resample_poly(y, r.numerator, r.denominator)

# exponential decay after hold seconds (cuts the room and the second foot of a recording short)
def decay(y, tau, hold=0.0):
    t = np.arange(len(y)) / SR
    return y * np.exp(-np.maximum(t - hold, 0) / tau)

def fade_in(y, sec):
    n = min(len(y), int(sec * SR)); y = y.copy(); y[:n] *= np.linspace(0, 1, n) ** 2
    return y

def mix(length, *layers):
    # layers: (signal, gain in dB, delay in seconds)
    y = np.zeros(int(length * SR))
    for s, db, delay in layers:
        a = int(delay * SR); s = s[:len(y) - a]
        y[a:a + len(s)] += s * 10 ** (db / 20)
    return y

def save(rel, y, peak, hpf=80, tail=0.4):
    y = hp(y, hpf)
    n = int(len(y) * tail); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    y[:int(0.002 * SR)] *= np.linspace(0, 1, int(0.002 * SR))
    y = y / (np.abs(y).max() + 1e-12) * peak
    path = os.path.join(out, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (y * 32767).astype(np.int16))
    print('%-22s %.3f s' % (rel.replace(os.sep, '/'), len(y) / SR))

# ---------------- jump: the push off ----------------

# the cloth: a rustle of a jacket (or the clothes of a jumping man) that swells in fast and dies out
def cloth(sid, at, length=0.24, p=1.0):
    y = hp(part(sid, at, length / p + 0.02, frac=0.1), 180, 2)
    return decay(fade_in(pitch(y, p), 0.012), 0.07, 0.06)

# the shoe: one light scuff of a sneaker on a hard floor, without its low thump
def scuff(sid, at, tau=0.035):
    return decay(hp(part(sid, at, 0.2, frac=0.3), 220, 2), tau, 0.01)

jumps = [
    (cloth(494797, 0.03), scuff(170504, 0.62), -4),
    (cloth(464488, 0.44), scuff(689126, 2.86), -5),
    (cloth(494797, 0.12, p=1.08), scuff(170499, 4.02, 0.03), -6),
    (cloth(464488, 0.44, p=1.1), scuff(689126, 5.06), -5),
]
for i, (c, s, db) in enumerate(jumps):
    y = mix(0.24, (c, 0, 0), (s, db, 0.004))
    save('Movement/jump_%d.wav' % i, y, 0.55)

# ---------------- landing ----------------

# the feet on the floor: a landing recording, its room cut short
def feet(sid, at, length=0.28):
    return decay(part(sid, at, length, frac=0.2), 0.06, 0.05)

# the weight of the body: the low part of a heavy landing thud
def thud(sid, at):
    return decay(lp(part(sid, at, 0.25, frac=0.3), 300, 4), 0.05, 0.02)

lands = [
    (feet(422753, 0.0), thud(464527, 0.6), cloth(494797, 0.06, 0.2)),
    (feet(422754, 0.0), thud(170504, 2.10), cloth(464488, 0.44, 0.2)),
    (feet(170504, 2.10), thud(464527, 0.6), cloth(494797, 0.14, 0.2, 1.06)),
    (feet(646660, 0.04), thud(464527, 0.6), cloth(464488, 0.44, 0.2, 0.94)),
]

# most of the thud is a 40-80 Hz boom that small speakers cannot play: the low band is shortened and
# soft-clipped (harmonics at 100-400 Hz), so the landing is heard on any speakers; the shoes stay as they are
def weighty(y):
    low = lp(y, 160, 4); high = y - low
    t = np.arange(len(y)) / SR
    low = low * np.exp(-np.maximum(t - 0.03, 0) / 0.07)
    k = np.abs(low).max() + 1e-12
    return np.tanh(3 * low / k) / np.tanh(3) * k + high

for i, (f, th, c) in enumerate(lands):
    y = mix(0.28, (f, 0, 0), (th, -5, 0.002), (c, -13, 0.01))
    save('Movement/land_%d.wav' % i, weighty(y), 0.8)
