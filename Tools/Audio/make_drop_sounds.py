# A weapon landing on the floor (WeaponDrops plays Resources/Audio/Drops/*): a heavy metal thunk and a lighter
# clank a moment later (the gun bounces once), from Kenney "Impact Sounds" (kenney.nl, CC0).
# usage: python make_drop_sounds.py <folder with the Kenney clips decoded to wav> <Assets/Resources/Audio>
# (impactMetal_heavy_00N and impactMetal_medium_00N; any file name that starts so and ends with .wav,
#  e.g. impactMetal_heavy_000.ogg.wav or .dec.wav from decode_blender.py)
# Output: Drops/land_N.wav, 48 kHz mono 16 bit, peak 0.7.
import sys, os, numpy as np
from fractions import Fraction
from scipy.io import wavfile
from scipy.signal import resample_poly, butter, sosfilt
import warnings; warnings.filterwarnings('ignore')

root, out = sys.argv[1], sys.argv[2]
SR = 48000

def load(name):
    for f in sorted(os.listdir(root)):
        if f.startswith(name) and f.lower().endswith('.wav'):
            sr, x = wavfile.read(os.path.join(root, f))
            x = x.astype(np.float64) / 32768.0
            if x.ndim > 1: x = x.mean(axis=1)
            r = Fraction(SR, sr)
            x = resample_poly(x, r.numerator, r.denominator) if sr != SR else x
            return x / (np.abs(x).max() + 1e-12)
    raise SystemExit('missing ' + name)

def hp(x, f, order=2): return sosfilt(butter(order, f, 'highpass', fs=SR, output='sos'), x)

for i in range(5):
    thunk = load('impactMetal_heavy_%03d' % i)
    clank = load('impactMetal_medium_%03d' % ((i + 2) % 5))
    y = np.zeros(int(0.32 * SR))
    a = thunk[:len(y)]; y[:len(a)] += a
    d = int((0.07 + 0.015 * i) * SR); b = clank[:len(y) - d]; y[d:d + len(b)] += 0.45 * b
    y = hp(y, 90)
    n = int(len(y) * 0.3); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    y = y / (np.abs(y).max() + 1e-12) * 0.7
    path = os.path.join(out, 'Drops', 'land_%d.wav' % i)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (y * 32767).astype(np.int16))
    print('Drops/land_%d.wav  %.3f s' % (i, len(y) / SR))
