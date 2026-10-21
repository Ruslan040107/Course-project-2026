# Bullet hit sounds on players: Impacts/body_N (a meaty flesh hit) and Impacts/head_N (a headshot: a sharp
# helmet "tink" + a crack + the flesh hit), layered from CC0 recordings of freesound.org (see CREDITS.md):
#   344409 jawbutch "Three Flesh Impacts"            423301 u1769092 "VisceralBulletImpacts"
#   586841 Kinoton "Power Kick, Body Impact, 7x"      717270 Mafon2 "Tummy slaps"
#   138478..138481 JustInvoke "Bullet Blood 1-4"      219024 JarredGibb "Metal Ping 192kHz"
#   616492 Empiremonkey "clank"                       445771 DDT197 "Hitting metal"
#   660769 MadPanCake "Headshot sound"
# usage: python make_hit_sounds.py <folder with <freesound id>.wav> <Assets/Resources/Audio>
# (the freesound previews decoded to wav, e.g. with decode_blender.py; any rate, mono or stereo)
# Output: 48 kHz mono 16 bit, peak 0.8, short fades.
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
# of the window [at, at + search], minus 2 ms
def part(sid, at, length, frac=0.2, search=0.4):
    x = load(sid)
    a = int(at * SR); w = x[a:a + int(search * SR)]
    on = np.where(np.abs(w) > frac * np.abs(w).max())[0][0]
    a = max(0, a + on - int(0.002 * SR))
    y = x[a:a + int(length * SR)].copy()
    n = int(0.001 * SR); y[:n] *= np.linspace(0, 1, n)
    return y / (np.abs(y).max() + 1e-12)

# higher pitch = shorter (like a faster tape)
def pitch(y, p):
    if p == 1: return y
    r = Fraction(1 / p).limit_denominator(200)
    return resample_poly(y, r.numerator, r.denominator)

# exponential decay after hold seconds (shortens the ring of a ping or the room of a recording)
def decay(y, tau, hold=0.0):
    t = np.arange(len(y)) / SR
    return y * np.exp(-np.maximum(t - hold, 0) / tau)

def mix(length, *layers):
    # layers: (signal, gain in dB, delay in seconds)
    y = np.zeros(int(length * SR))
    for s, db, delay in layers:
        a = int(delay * SR); s = s[:len(y) - a]
        y[a:a + len(s)] += s * 10 ** (db / 20)
    return y

def save(rel, y, peak=0.8, hpf=70, tail=0.35):
    y = hp(y, hpf)
    n = int(len(y) * tail); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    y[:int(0.001 * SR)] *= np.linspace(0, 1, int(0.001 * SR))
    y = y / (np.abs(y).max() + 1e-12) * peak
    path = os.path.join(out, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (y * 32767).astype(np.int16))
    print('%-22s %.3f s' % (rel.replace(os.sep, '/'), len(y) / SR))

# the low punch of the hit: the body impact of a kick, low-passed and shortened; soft-clipped a little,
# so the thump has harmonics that small speakers can play
def punch(at, tau=0.05):
    y = lp(part(586841, at, 0.3, frac=0.3), 450, 4)
    y = decay(y, tau, 0.02)
    k = np.abs(y).max() + 1e-12
    return np.tanh(2.5 * y / k) / np.tanh(2.5)

# the flesh smack: a close slap on a body with the room cut short
def smack(sid, at, tau=0.045):
    return decay(part(sid, at, 0.3), tau, 0.012)

# the wet layer: a bullet into flesh (slow attack), high-passed
def wet(sid):
    return decay(hp(part(sid, 0.0, 0.2, frac=0.1, search=0.2), 700), 0.05, 0.03)

# a sharp skin slap transient
def slap(at):
    return hp(part(717270, at, 0.08, frac=0.3, search=0.2), 400)

# body: smack + punch + wet + slap, four variants
body = [
    (smack(344409, 0.85), punch(0.50), wet(138478), slap(0.00)),
    (smack(344409, 2.53), punch(2.50), wet(138479), slap(5.00)),
    (smack(344409, 4.05), punch(4.52), wet(138480), slap(3.38)),
    (smack(423301, 0.12, 0.035), punch(6.52), wet(138481), slap(10.27)),
]
bodies = []
for i, (s, p, w, sl) in enumerate(body):
    y = mix(0.26, (s, 0, 0), (p, -10, 0.002), (w, -6, 0.004), (sl, -5, 0))
    bodies.append(y / np.abs(y).max())
    save('Impacts/body_%d.wav' % i, y)

# the helmet tink: a bright metal ping (its ring cut short) over a sharp metal clank
def ping(p, tau=0.06):
    y = hp(part(219024, 0.0, 0.5, frac=0.3, search=0.3), 1800, 4)
    return decay(pitch(y, p), tau, 0.005)

def clank(sid, at, tau=0.035):
    return decay(hp(part(sid, at, 0.25, frac=0.3), 1200, 2), tau, 0.004)

# the crack of the skull: the low knock of a designed headshot sound
def crack():
    return decay(part(660769, 0.0, 0.2, frac=0.3), 0.04, 0.01)

head = [
    (ping(1.00), clank(616492, 0.0), bodies[0]),
    (ping(0.92), clank(445771, 0.10), bodies[1]),
    (ping(1.08), clank(445771, 2.00), bodies[2]),
    (ping(0.96, 0.07), clank(445771, 4.00), bodies[3]),
]
for i, (pg, cl, fl) in enumerate(head):
    y = mix(0.32, (pg, 0, 0), (cl, -4, 0), (crack(), -6, 0.001), (fl, -7, 0.003))
    save('Impacts/head_%d.wav' % i, y, tail=0.4)
