# Jump and landing sounds, cut from CC0 recordings (see CREDITS.md).
# usage: python make_movement_sounds.py <folder with the raw wavs> <Assets/Resources/Audio>
# Output: Movement/land_N.wav (Jump_1..3 by Dan Knoflicek, jumpland), Movement/jump_N.wav (the shoe scuff of Jump_1..3)
# 48 kHz mono 16 bit, short fades, high-pass 80 Hz.
import sys, os, numpy as np
from fractions import Fraction
from scipy.io import wavfile
from scipy.signal import resample_poly, butter, sosfilt
import warnings; warnings.filterwarnings('ignore')

root, out = sys.argv[1], sys.argv[2]
SR = 48000

def load(name):
    sr, x = wavfile.read(os.path.join(root, name))
    x = x.astype(np.float64) / 32768.0
    if x.ndim > 1: x = x.mean(axis=1)
    ratio = Fraction(SR, sr)
    return resample_poly(x, ratio.numerator, ratio.denominator)

def hp(x, f, order=2): return sosfilt(butter(order, f, 'highpass', fs=SR, output='sos'), x)
def lp(x, f, order=2): return sosfilt(butter(order, f, 'lowpass', fs=SR, output='sos'), x)

def onset(x, frac=0.05):
    return max(0, np.where(np.abs(x) > frac * np.abs(x).max())[0][0] - int(0.004 * SR))

def save(rel, y, peak):
    y = hp(y, 80)
    n = int(0.003 * SR); y[:n] *= np.linspace(0, 1, n)
    y = y / (np.abs(y).max() + 1e-12) * peak
    path = os.path.join(out, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (y * 32767).astype(np.int16))
    print('%-24s %.3f s' % (rel.replace(os.sep, '/'), len(y) / SR))

def fade_out(y, part=0.5):
    n = int(len(y) * part); y = y.copy(); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    return y

# landing: the recordings are mostly a 40-50 Hz boom that small speakers cannot play and an 80 Hz
# high-pass would remove. The low band is shortened and soft-clipped (adds harmonics at 100-400 Hz),
# so the thud is heard on any speakers; the shoe impact (highs) stays as it is.
def landing(x, length):
    a = onset(x, 0.15); y = x[a:a + int(length * SR)]
    low = lp(y, 160, 4); high = y - low
    t = np.arange(len(y)) / SR
    low = low * np.exp(-np.maximum(t - 0.03, 0) / 0.07)           # the boom dies out fast
    k = np.abs(low).max() + 1e-12
    thud = np.tanh(4 * low / k) / np.tanh(4) * k
    high = high / (np.abs(high).max() + 1e-12) * k
    return fade_out(thud + 1.5 * high, 0.4)

for i, name in enumerate(['Jump_1.wav', 'Jump_2.wav', 'Jump_3.wav', 'jumpland.wav']):
    save('Movement/land_%d.wav' % i, landing(load(name), 0.26), 0.8)

# take-off: only the scuff of the shoe before the landing boom, quieter and shorter
for i, name in enumerate(['Jump_1.wav', 'Jump_2.wav', 'Jump_3.wav']):
    x = load(name); a = onset(x)
    y = hp(x[a:a + int(0.12 * SR)], 250, 4)
    save('Movement/jump_%d.wav' % i, fade_out(y, 0.6), 0.5)
