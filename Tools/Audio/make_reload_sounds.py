# Reload sets for every weapon and the knife flip, cut from CC0 recordings (see CREDITS.md).
# usage: python make_reload_sounds.py <folder with the raw wavs> <Assets/Resources/Audio>
# Output: Reload/<weapon>/{mag_out,mag_in,bolt_back,bolt_forward | slide_back,slide_forward}_N.wav
#         Knife/flip_N.wav
# 48 kHz mono 16 bit, trimmed, short fades, high-pass 80 Hz, peak 0.8 (loud slams turned down to the others' level).
# The times below were found by onset detection and by looking at waveform/spectrogram pictures.
import sys, os, numpy as np
from fractions import Fraction
from scipy.io import wavfile
from scipy.signal import resample_poly, butter, sosfilt
import warnings; warnings.filterwarnings('ignore')

root, out = sys.argv[1], sys.argv[2]
SR = 48000
cache = {}

def load(name):
    if name not in cache:
        sr, x = wavfile.read(os.path.join(root, name))
        x = x.astype(np.float64) / 32768.0
        if x.ndim > 1: x = x.mean(axis=1)
        cache[name] = (sr, x)
    return cache[name]

def hp(x, f, order=2): return sosfilt(butter(order, f, 'highpass', fs=SR, output='sos'), x)
def lp(x, f, order=2): return sosfilt(butter(order, f, 'lowpass', fs=SR, output='sos'), x)

# a part of a recording at 48 kHz; pitch > 1 plays it higher and shorter (like a smaller part)
def part(name, t0, t1, pitch=1.0):
    sr, x = load(name)
    seg = x[int(t0 * sr):int(t1 * sr)]
    ratio = Fraction(SR / (sr * pitch)).limit_denominator(400)
    return resample_poly(seg, ratio.numerator, ratio.denominator)

# starts a few ms before the first loud sample, ends when the tail is quiet, fades both ends
def finish(y, fade_in=0.003, fade_out=0.05):
    y = hp(y, 80)
    env = np.abs(y); pk = env.max() + 1e-12
    a = max(0, np.where(env > 0.04 * pk)[0][0] - int(0.004 * SR))
    b = min(len(y), np.where(env > 0.015 * pk)[0][-1] + int(0.01 * SR))
    y = y[a:b].copy()
    n = min(int(fade_in * SR), len(y) // 4); y[:n] *= np.linspace(0, 1, n)
    n = min(int(fade_out * SR), len(y) // 3); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    return y

# loudness of the loudest 50 ms
def loudest(y, w=2400):
    if len(y) <= w: return np.sqrt((y ** 2).mean())
    return max(np.sqrt((y[i:i + w] ** 2).mean()) for i in range(0, len(y) - w, 480))

def save(rel, y, peak=0.8, loud=0.2):
    y = finish(y)
    y = y / (np.abs(y).max() + 1e-12) * peak
    y *= min(1.0, loud / (loudest(y) + 1e-12))   # a hard slam is not much louder than the rest
    path = os.path.join(out, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (y * 32767).astype(np.int16))
    print('%-40s %.3f s' % (rel.replace(os.sep, '/'), len(y) / SR))

# brighter: adds a part of the highs on top
def crisp(y, amount=0.5, f=3000): return y + amount * hp(y, f)
# heavier: removes some highs and adds a bit of low body
def heavy(y, cut=6000, body=0.5): return lp(y, cut) + body * lp(y, 400)

# a small gap of silence, to put two parts one after another
def gap(t): return np.zeros(int(t * SR))
def norm(y, peak=1.0): return y / (np.abs(y).max() + 1e-12) * peak

AR = 'assaultriflereload1_0.wav'   # rifle reload (airsoft): mag out 0.14, mag in 1.04, handle 1.2, bolt 1.27
GR = 'gunreload1.wav'              # rifle reload (airsoft): mag rattle 0.06, small clicks 0.68-1.03, slam 1.26
PI = 'reload.wav'                  # real handgun: mag out 0.09, mag in 0.55, slide back 1.02, slide forward 1.185
BA = 'equipment_clicks3.wav'       # real bolt-action rifle cocked a few times (+ stapler, tape measure later)
C1, C2 = 'clipload1.wav', 'clipload2.wav'
EC = 'equipmentclicks.wav'

R = 'Reload'

# AK-47: heavy and low
save(f'{R}/AK-47/mag_out_0.wav', heavy(part(GR, 0.06, 0.32, 0.86)))
save(f'{R}/AK-47/mag_in_0.wav', heavy(part(AR, 1.035, 1.19, 0.86)))
save(f'{R}/AK-47/bolt_back_0.wav', heavy(part(AR, 1.195, 1.272, 0.84)))
save(f'{R}/AK-47/bolt_forward_0.wav', heavy(part(GR, 1.25, 1.56, 0.88), 5000, 0.7))

# M4A4: crisp
save(f'{R}/M4A4/mag_out_0.wav', crisp(part(AR, 0.14, 0.36, 1.04)))
save(f'{R}/M4A4/mag_in_0.wav', crisp(part(AR, 1.035, 1.19, 1.05)))
save(f'{R}/M4A4/bolt_back_0.wav', crisp(part(AR, 1.195, 1.272, 1.05)))
save(f'{R}/M4A4/bolt_forward_0.wav', crisp(part(AR, 1.27, 1.48, 1.04)))

# MP9: light and short
save(f'{R}/MP9/mag_out_0.wav', crisp(part(GR, 0.06, 0.32, 1.22), 0.3))
save(f'{R}/MP9/mag_in_0.wav', crisp(part(AR, 1.035, 1.15, 1.24), 0.3))
save(f'{R}/MP9/bolt_back_0.wav', crisp(part(GR, 0.795, 0.86, 1.15), 0.3))
save(f'{R}/MP9/bolt_forward_0.wav', crisp(part(AR, 1.27, 1.42, 1.25), 0.3))

# AWP: magazine from the clip loading recordings, bolt from the real bolt-action rifle
save(f'{R}/AWP/mag_out_0.wav', heavy(part(C2, 0.0, 0.21, 0.92), 9000, 0.3))
save(f'{R}/AWP/mag_in_0.wav', heavy(part(C1, 0.0, 0.26, 0.92), 9000, 0.3))
save(f'{R}/AWP/bolt_back_0.wav', part(BA, 2.15, 2.40))
save(f'{R}/AWP/bolt_back_1.wav', part(BA, 3.72, 3.95))
save(f'{R}/AWP/bolt_forward_0.wav', part(BA, 2.76, 3.02))
save(f'{R}/AWP/bolt_forward_1.wav', part(BA, 1.43, 1.75))

# pistols from the real handgun reload; the Desert Eagle lower and heavier, the Glock a bit higher
pistols = {
    'Glock-18':     (1.07, lambda y: crisp(y, 0.3)),
    'USP-S':        (0.97, lambda y: y),
    'Desert Eagle': (0.80, lambda y: heavy(y, 6000, 0.6)),
}
for name, (p, fx) in pistols.items():
    save(f'{R}/{name}/mag_out_0.wav', fx(part(PI, 0.085, 0.24, p)))
    save(f'{R}/{name}/mag_in_0.wav', fx(part(PI, 0.55, 0.86, p)))
    save(f'{R}/{name}/slide_back_0.wav', fx(part(PI, 1.02, 1.185, p)))
    save(f'{R}/{name}/slide_forward_0.wav', fx(part(PI, 1.185, 1.40, p)))

# knife flip: short light metallic clicks without the low part
def flick(y): return hp(y, 1200, 4)
save('Knife/flip_0.wav', flick(part(EC, 2.055, 2.17, 1.1)), 0.7)
save('Knife/flip_1.wav', flick(part(EC, 1.125, 1.20, 1.15)), 0.7)
save('Knife/flip_2.wav', np.concatenate([norm(flick(part(EC, 2.28, 2.30, 1.1)), 0.6), gap(0.045), norm(flick(part(EC, 2.055, 2.15, 1.25)))]), 0.7)
