# Shots for the weapons that had none, their reload sets, the shotgun pump, the revolver cylinder click
# and the Zeus zap (see CREDITS.md for the sources, all CC0).
# usage: python make_weapon_sounds.py <Prepared SFX Library> <raw reload wavs> <RPG Audio> <Assets/Resources/Audio> [output folder]
#   <Prepared SFX Library> = "The Free Firearm Sound Library" (freefirearmsfx.com), folders per gun.
#   <raw reload wavs> = the opengameart recordings of make_reload_sounds.py (clipload1/2, equipment_clicks3).
#   <RPG Audio> = Kenney "RPG Audio" (kenney.nl), its .ogg files decoded to .wav first (decode_blender.py).
#   Reload sets are made from the clips already in <Assets/Resources/Audio>/Reload (pitch variants);
#   the output folder is the same Audio folder by default (another one is handy for a preview).
# Output: Weapons/<weapon>/shot_N.wav, Reload/<weapon>/<key>_N.wav (shotguns also shell_in_N),
#         Mech/pump_back_N.wav, Mech/pump_forward_N.wav, Reload/R8 Revolver/cylinder_N.wav, Weapons/Zeus x27/shot_N.wav
# 48 kHz mono 16 bit. Shots: cut at the onset, high-pass 60 Hz, fade out, peak 0.9 (silenced 0.75).
# Every weapon is made different by its source gun, pitch (played faster = smaller gun), EQ and tail length.
import sys, os, numpy as np
from fractions import Fraction
from scipy.io import wavfile
from scipy.signal import resample_poly, butter, sosfilt, stft, istft, find_peaks
import warnings; warnings.filterwarnings('ignore')

lib, raw, rpg, audio = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
out = sys.argv[5] if len(sys.argv) > 5 else audio
SR = 48000
cache = {}

# ---------------- reading and cutting ----------------

def load(path):
    if path not in cache:
        sr, x = wavfile.read(path)
        scale = {np.dtype('int16'): 32768.0, np.dtype('int32'): 2147483648.0}.get(x.dtype, 1.0)
        x = x.astype(np.float64) / scale
        if x.ndim > 1: x = x.mean(axis=1)
        cache[path] = (sr, x)
    return cache[path]

def gun(name):
    folder = os.path.join(lib, name.split('/')[0])
    return os.path.join(folder, name.split('/')[1])

# times of the shots in a recording (like Tools/Audio/cut.py)
def onsets(sr, x, frac=0.3, gap=0.25):
    env = np.abs(x); thr = frac * env.max(); hop = int(0.002 * sr)
    fr = env[:len(env) // hop * hop].reshape(-1, hop).max(axis=1)
    on = []; last = -1e9
    for i, v in enumerate(fr):
        t = i * hop / sr
        if v > thr and t - last > gap: on.append(t)
        if v > thr: last = t
    return on

# resampled to 48 kHz; pitch > 1 plays it higher and shorter
def to48(seg, sr, pitch=1.0):
    ratio = Fraction(SR / (sr * pitch)).limit_denominator(400)
    return resample_poly(seg, ratio.numerator, ratio.denominator)

# shot number k of a recording, `length` seconds of it (before the pitch change);
# last=True: k is a burst, the last shot of it is taken (only that one has the full tail)
def shot(name, k, length, pitch=1.0, last=False):
    sr, x = load(gun(name))
    t = onsets(sr, x)[k]
    if last:
        # every shot of the burst is a peak of the envelope at least 35 ms after the one before
        hop = int(0.002 * sr)
        env = np.abs(x[:len(x) // hop * hop]).reshape(-1, hop).max(axis=1)
        peaks, _ = find_peaks(env, height=0.3 * env.max(), distance=int(0.035 * sr / hop))
        end = t
        for p in peaks * hop / sr:
            if p <= end: continue
            if p - end > 0.2: break
            end = p
        t = end - 0.002 if end > t else t
    a = max(0, int((t - 0.004) * sr)); b = min(len(x), int((t + length) * sr))
    seg = x[a:b].copy()
    # stop before a second shot inside the window
    env = np.abs(seg); pk = env.max(); start = int(0.2 * sr)
    later = np.where(env[start:] > 0.7 * pk)[0]
    if len(later): seg = seg[:start + later[0] - int(0.004 * sr)]
    return to48(seg, sr, pitch)

# a part of a recording by time
def part(path, t0, t1, pitch=1.0):
    sr, x = load(path)
    return to48(x[int(t0 * sr):int(t1 * sr)], sr, pitch)

# ---------------- filters ----------------

def hp(x, f, order=2): return sosfilt(butter(order, f, 'highpass', fs=SR, output='sos'), x)
def lp(x, f, order=2): return sosfilt(butter(order, f, 'lowpass', fs=SR, output='sos'), x)
def bp(x, f1, f2, order=2): return sosfilt(butter(order, [f1, f2], 'bandpass', fs=SR, output='sos'), x)
def norm(y, peak=1.0): return y / (np.abs(y).max() + 1e-12) * peak

# brighter: a part of the highs on top
def crisp(y, amount=0.5, f=3000): return y + amount * hp(y, f)
# heavier: a bit of low body on top, optionally without the top highs
def body(y, amount=0.5, f=350, cut=None):
    y = y + amount * lp(y, f)
    return lp(y, cut) if cut else y
# more of one band (a boxy or a biting sound)
def bump(y, f1, f2, amount=0.6): return y + amount * bp(y, f1, f2)

# shorter tail: after `keep` seconds the sound dies out with the time constant `decay`
def tail(y, keep, decay):
    t = np.arange(len(y)) / SR
    return y * np.exp(-np.maximum(t - keep, 0) / decay)

# quieter after `at` seconds (a strong echo of the range turned down), a smooth 20 ms step
def duck(y, at, gain):
    t = np.arange(len(y)) / SR
    return y * (1 - (1 - gain) * np.clip((t - at) / 0.02, 0, 1))

def fade(y, fin=0.002, fout=0.35):
    y = y.copy()
    n = min(int(fin * SR), len(y) // 4); y[:n] *= np.linspace(0, 1, n)
    n = int(len(y) * fout); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    return y

def write(rel, y):
    path = os.path.join(out, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (np.clip(y, -1, 1) * 32767).astype(np.int16))
    print('%-44s %.3f s  peak %.2f' % (rel.replace(os.sep, '/'), len(y) / SR, np.abs(y).max()))

def save_shot(weapon, i, y, peak=0.9):
    y = fade(hp(y, 60))
    write('Weapons/%s/shot_%d.wav' % (weapon, i), norm(y, peak))

# ---------------- shots ----------------

# weapon: (shots [(recording, shot number[, B = the last shot of that burst])], length s, pitch, effect)
B = True
L = lambda *a: list(a)
shots = {
    # pistols
    'P2000':         (L(('Walther PPQ/X_39P.wav', 0), ('Walther PPQ/X_39P.wav', 1), ('Walther PPQ/X_31P.wav', 1)), 0.8, 0.92,
                      lambda y: body(y, 0.6, 450)),
    'P250':          (L(('Bersa/F_47P.wav', 0), ('Bersa/F_47P.wav', 1), ('Bersa/F_41P.wav', 0)), 0.8, 0.94,
                      lambda y: bump(body(y, 0.4), 900, 2800, 0.5)),
    'Dual Berettas': (L(('1911/A_42P.wav', 0), ('1911/A_42P.wav', 1), ('1911/A_34P.wav', 2)), 0.75, 1.15,
                      lambda y: crisp(y, 0.5)),
    'CZ75-Auto':     (L(('Bersa/F_41P.wav', 0), ('Bersa/F_41P.wav', 1), ('Bersa/F_47P.wav', 1)), 0.6, 1.12,
                      lambda y: tail(crisp(y, 0.4), 0.04, 0.12)),
    'Tec-9':         (L(('Ruger Single Six/S_14P.wav', 0), ('Ruger Single Six/S_14P.wav', 1), ('Ruger Single Six/S_11P.wav', 1)), 0.7, 0.9,
                      lambda y: tail(bump(body(y, 0.5, 400), 1800, 5000, 0.7), 0.05, 0.2)),
    'Five-SeveN':    (L(('Ruger Mark III/R_35P.wav', 0), ('Ruger Mark III/R_30P.wav', 0), ('Ruger Mark III/R_30P.wav', 1)), 0.7, 0.95,
                      lambda y: crisp(y, 0.8, 2500)),
    'R8 Revolver':   (L(('Smith & Wesson 642/V_22P.wav', 0), ('Smith & Wesson 642/V_22P.wav', 1), ('Smith & Wesson 642/V_27P.wav', 0)), 1.1, 0.84,
                      lambda y: duck(body(y, 0.8, 300), 0.08, 0.5)),   # the mid distance take has a loud echo
    # SMGs
    'MAC-10':        (L(('Carl Gustav M45/G_31P.wav', 0), ('Carl Gustav M45/G_31P.wav', 1), ('Carl Gustav M45/G_31P.wav', 2)), 0.6, 1.12,
                      lambda y: tail(bump(y, 700, 2500, 0.8), 0.04, 0.1)),
    'UMP-45':        (L(('Carl Gustav M45/G_33P.wav', 0, B), ('Carl Gustav M45/G_33P.wav', 1, B), ('Carl Gustav M45/G_33P.wav', 2, B)), 0.7, 0.88,
                      lambda y: tail(body(y, 0.7, 350), 0.06, 0.25)),
    'PP-Bizon':      (L(('PPSh/P_30P.wav', 0), ('PPSh/P_30P.wav', 1), ('PPSh/P_30P.wav', 2)), 0.6, 0.97,
                      lambda y: tail(body(y, 0.3, 400, 7000), 0.05, 0.15)),
    'MP7':           (L(('AR-15/D_32P.wav', 0), ('AR-15/D_32P.wav', 1), ('AR-15/D_24P.wav', 0)), 0.55, 1.28,
                      lambda y: tail(crisp(y, 0.4), 0.03, 0.1)),
    'P90':           (L(('PPSh/P_32P.wav', 0, B), ('PPSh/P_32P.wav', 1, B), ('PPSh/P_34P.wav', 0, B)), 0.55, 1.18,
                      lambda y: tail(crisp(y, 0.6, 3500), 0.03, 0.12)),
    # shotguns
    'Nova':          (L(('Nova/O_21P.wav', 0), ('Nova/O_21P.wav', 1), ('Nova/O_17P.wav', 0)), 1.2, 1.0,
                      lambda y: body(y, 0.3)),
    'Sawed-Off':     (L(('Mossberg/N_30P.wav', 0), ('Mossberg/N_30P.wav', 1), ('Mossberg/N_26P.wav', 0)), 1.1, 0.88,
                      lambda y: body(y, 0.7, 300)),
    'MAG-7':         (L(('Model 12/K_22P.wav', 0), ('Model 12/K_22P.wav', 1), ('Model 12/K_17P.wav', 0)), 1.0, 1.04,
                      lambda y: bump(y, 800, 2500, 0.5)),
    'XM1014':        (L(('CD/H_21P.wav', 0), ('CD/H_21P.wav', 1), ('CD/H_16P.wav', 0)), 0.9, 1.08,
                      lambda y: tail(crisp(y, 0.3), 0.08, 0.25)),
    # machine guns
    'Negev':         (L(('AK-47/C_28P.wav', 1), ('AK-47/C_28P.wav', 2), ('AK-47/C_28P.wav', 3)), 0.7, 0.88,
                      lambda y: tail(body(y, 0.6, 350), 0.05, 0.15)),
    'M249':          (L(('SKS/U_19P.wav', 0), ('SKS/U_19P.wav', 1), ('SKS/U_19P.wav', 2)), 1.0, 0.85,
                      lambda y: tail(body(y, 0.8, 300), 0.06, 0.3)),
    # rifles
    'Galil AR':      (L(('SKS/U_14P.wav', 0), ('SKS/U_14P.wav', 1), ('SKS/U_14P.wav', 2)), 0.9, 1.06,
                      lambda y: bump(y, 1000, 3000, 0.5)),
    'FAMAS':         (L(('AK-47/C_29P.wav', 0, B), ('AK-47/C_29P.wav', 1, B), ('AK-47/C_27P.wav', 0, B)), 0.8, 1.15,
                      lambda y: tail(crisp(y, 0.5), 0.05, 0.2)),
    'SG 553':        (L(('Model 1894/L_23P.wav', 0), ('Model 1894/L_23P.wav', 1), ('Model 1894/L_17P.wav', 0)), 1.0, 0.93,
                      lambda y: tail(body(y, 0.5, 350, 9000), 0.07, 0.16)),
    'AUG':           (L(('Marlin 336/I_22P.wav', 0), ('Marlin 336/I_22P.wav', 1), ('Marlin 336/I_17P.wav', 0)), 0.9, 1.12,
                      lambda y: tail(crisp(y, 0.4), 0.06, 0.25)),
    # snipers
    'SSG 08':        (L(('Arisaka/E_25P.wav', 0), ('Arisaka/E_25P.wav', 1), ('Arisaka/E_18P.wav', 0)), 1.4, 1.06,
                      lambda y: crisp(y, 0.3)),
    'SCAR-20':       (L(('Savage 10 .300 Blackout/T_27P.wav', 0), ('Savage 10 .300 Blackout/T_27P.wav', 1), ('Savage 10 .300 Blackout/T_17P.wav', 0)), 1.3, 0.95,
                      lambda y: body(y, 0.5, 350)),
    'G3SG1':         (L(('1917/B_24P.wav', 0), ('1917/B_24P.wav', 1), ('1917/B_16P.wav', 0)), 1.3, 0.9,
                      lambda y: body(y, 0.7, 300, 10000)),
}

for weapon, (srcs, length, pitch, fx) in shots.items():
    for i, src in enumerate(srcs):
        save_shot(weapon, i, fx(shot(src[0], src[1], length, pitch, len(src) > 2)))

# silenced: the shot through a low-pass (the muzzle blast is held in), short, with a mechanical clack
# of the action and a small puff of air on top
def silenced(y, cut, clack, puff_f):
    rng = np.random.default_rng(len(y))
    thump = tail(hp(lp(y, cut, 4), 150), 0.02, 0.06)
    thump = norm(thump)
    n = int(0.05 * SR); t = np.arange(n) / SR
    puff = bp(rng.standard_normal(n), puff_f * 0.5, puff_f * 1.6) * np.exp(-t / 0.012)
    out = thump.copy()
    out[:n] += 0.25 * norm(puff)
    c = norm(hp(clack, 1500, 4))
    m = min(len(c), len(out) - int(0.004 * SR))
    out[int(0.004 * SR):int(0.004 * SR) + m] += 0.3 * c[:m]
    return out

clack = part(os.path.join(audio, 'Reload/M4A4/bolt_forward_0.wav'), 0.0, 0.05, 1.3)
for i, (name, k) in enumerate([('AR-15/D_32P.wav', 0), ('AR-15/D_24P.wav', 1), ('AR-15/D_32P.wav', 1)]):
    save_shot('M4A1-S', i, silenced(shot(name, k, 0.6, 0.95), 1900, clack, 2200), 0.75)
clack = part(os.path.join(audio, 'Reload/MP9/bolt_forward_0.wav'), 0.0, 0.04, 1.4)
for i, (name, k) in enumerate([('Carl Gustav M45/G_35P.wav', 0), ('Carl Gustav M45/G_35P.wav', 1), ('Carl Gustav M45/G_24P.wav', 0)]):
    save_shot('MP5-SD', i, silenced(shot(name, k, 0.5, 1.05, True), 1400, clack, 1600), 0.7)

# ---------------- reload sets ----------------

# a clip, trimmed to where it is heard, short fades, peak `peak`
def save_part(rel, y, peak):
    y = hp(y, 80)
    env = np.abs(y); pk = env.max() + 1e-12
    a = max(0, np.where(env > 0.04 * pk)[0][0] - int(0.004 * SR))
    b = min(len(y), np.where(env > 0.015 * pk)[0][-1] + int(0.01 * SR))
    y = y[a:b].copy()
    n = min(int(0.003 * SR), len(y) // 4); y[:n] *= np.linspace(0, 1, n)
    n = min(int(0.04 * SR), len(y) // 3); y[len(y) - n:] *= np.linspace(1, 0, n) ** 2
    write(rel, norm(y, peak))

# an existing clip of the Audio folder and its peak
def clip(rel):
    sr, x = load(os.path.join(audio, rel))
    return x, np.abs(x).max()

def heavy(y, cut=6000, amount=0.5): return lp(y, cut) + amount * lp(y, 400)

# a second copy of the sound a bit later and a bit higher (the two magazines of the dual pistols)
def double(y, delay, pitch=1.08):
    z = to48(y, SR, pitch); d = int(delay * SR)
    res = np.zeros(max(len(y), d + len(z)))
    res[:len(y)] += y; res[d:d + len(z)] += 0.8 * z
    return res

# weapon: (the set it is made from, pitch, effect)
families = {
    'P2000':         ('USP-S', 1.04, None),
    'P250':          ('Glock-18', 0.94, None),
    'Dual Berettas': ('Glock-18', 1.06, None),
    'CZ75-Auto':     ('Glock-18', 1.12, lambda y: crisp(y, 0.3)),
    'Tec-9':         ('Desert Eagle', 1.15, None),
    'Five-SeveN':    ('USP-S', 1.1, lambda y: crisp(y, 0.3)),
    'MAC-10':        ('MP9', 0.94, None),
    'UMP-45':        ('M4A4', 0.96, None),
    'PP-Bizon':      ('AK-47', 1.08, None),
    'MP7':           ('MP9', 1.08, None),
    'MP5-SD':        ('M4A4', 1.06, lambda y: crisp(y, 0.3)),
    'P90':           ('MP9', 0.88, lambda y: heavy(y, 8000, 0.3)),
    'MAG-7':         ('AK-47', 1.05, None),
    'Negev':         ('AK-47', 0.85, lambda y: heavy(y, 7000, 0.4)),
    'M249':          ('M4A4', 0.85, lambda y: heavy(y, 7000, 0.4)),
    'Galil AR':      ('AK-47', 1.04, None),
    'FAMAS':         ('M4A4', 1.1, None),
    'SG 553':        ('AK-47', 0.95, None),
    'AUG':           ('M4A4', 0.98, lambda y: heavy(y, 9000, 0.2)),
    'M4A1-S':        ('M4A4', 1.03, None),
    'SSG 08':        ('AWP', 1.1, None),
    'SCAR-20':       ('M4A4', 0.9, lambda y: heavy(y, 8000, 0.3)),
    'G3SG1':         ('AK-47', 0.92, None),
}
for weapon, (fam, pitch, fx) in families.items():
    for f in sorted(os.listdir(os.path.join(audio, 'Reload', fam))):
        if not f.endswith('.wav'): continue
        if weapon == 'MAG-7' and f.startswith('bolt'): continue      # the MAG-7 pumps (below)
        x, pk = clip('Reload/%s/%s' % (fam, f))
        y = to48(x, SR, pitch)
        if fx: y = fx(y)
        if weapon == 'Dual Berettas' and f.startswith('mag_out'): y = double(y, 0.12)
        if weapon == 'Dual Berettas' and f.startswith('mag_in'): y = double(y, 0.3)
        save_part('Reload/%s/%s' % (weapon, f), y, pk)

# ---------------- shotgun pump and shells ----------------

# the pump of the Winchester Model 12 between its shots: back, then forward
M12a, M12b = gun('Model 12/K_17P.wav'), gun('Model 12/K_22P.wav')
pump_back = [part(M12a, 3.08, 3.21), part(M12b, 3.27, 3.42)]
pump_forward = [part(M12a, 3.92, 4.07), part(M12b, 4.80, 4.90)]

# a little heavier and brighter: the click of the action stays clear on small speakers
def pump(y, pitch=1.0): return crisp(heavy(to48(y, SR, pitch), 9000, 0.4), 0.3)

for i, y in enumerate(pump_back + [pump_back[0]]):
    save_part('Mech/pump_back_%d.wav' % i, pump(y, 0.94 if i == 2 else 1.0), 0.8)
for i, y in enumerate(pump_forward + [pump_forward[0]]):
    save_part('Mech/pump_forward_%d.wav' % i, pump(y, 0.94 if i == 2 else 1.0), 0.8)

# the pump of the reload (and of the bots) as the bolt keys of the pump shotguns
for weapon, pitch in [('Nova', 1.0), ('Sawed-Off', 0.95), ('MAG-7', 1.05)]:
    save_part('Reload/%s/bolt_back_0.wav' % weapon, pump(pump_back[0], pitch), 0.8)
    save_part('Reload/%s/bolt_forward_0.wav' % weapon, pump(pump_forward[0], pitch), 0.8)
# the XM1014 is semi-automatic: its charging handle is the rifle bolt, lower
for key in ['bolt_back_0.wav', 'bolt_forward_0.wav']:
    x, pk = clip('Reload/M4A4/' + key)
    save_part('Reload/XM1014/' + key, heavy(to48(x, SR, 0.9), 8000, 0.3), pk)

# a shell pushed into the tube: the click-clack of the clip loading recordings and an equipment click, lower
C1, C2, EC = [os.path.join(raw, f) for f in ['clipload1.wav', 'clipload2.wav', 'equipment_clicks3.wav']]
shells = [part(C2, 0.0, 0.21), part(C1, 0.0, 0.26), part(EC, 18.95, 19.2)]
for weapon, pitch in [('Nova', 0.84), ('Sawed-Off', 0.8), ('XM1014', 0.88)]:
    for i, y in enumerate(shells):
        y = heavy(to48(y, SR, pitch), 9000, 0.4)
        save_part('Reload/%s/shell_in_%d.wav' % (weapon, i), y, 0.75)
        if i == 0: save_part('Reload/%s/mag_in_0.wav' % weapon, y, 0.75)   # for code that asks for a magazine

# ---------------- R8 Revolver: reload and cylinder ----------------

# a Kenney RPG Audio clip (decoded from .ogg to .wav)
def rpg_clip(name, t0=0.0, t1=None, pitch=1.0):
    path = os.path.join(rpg, name + '.wav')
    if not os.path.exists(path): path = os.path.join(rpg, name + '.dec.wav')
    sr, x = load(path)
    return to48(x[int(t0 * sr):int(t1 * sr) if t1 else len(x)], sr, pitch)

# the cylinder turning to the next round (each shot cocks the hammer): metal clicks, small and high
clicks = [rpg_clip('metalClick', 0.085, 0.16, 1.05), rpg_clip('metalClick', 0.255, 0.33, 1.0), rpg_clip('metalClick', 0.085, 0.16, 0.92)]
for i, y in enumerate(clicks):
    save_part('Reload/R8 Revolver/cylinder_%d.wav' % i, crisp(hp(y, 300), 0.3), 0.7)

# sounds laid over each other: (start s, gain, sound) ...
def mix(*layers):
    n = max(int(t * SR) + len(y) for t, g, y in layers)
    res = np.zeros(n)
    for t, g, y in layers: res[int(t * SR):int(t * SR) + len(y)] += g * y
    return res

# open the cylinder and the empty cases fall out (coins played lower sound like brass), a speed loader in,
# the hammer back and the cylinder shut
R = 'Reload/R8 Revolver/'
opened = norm(rpg_clip('metalLatch', 0.0, 0.2, 0.9))
cases = norm(lp(rpg_clip('handleCoins', 0.04, 0.84, 0.78), 7000))
save_part(R + 'mag_out_0.wav', mix((0, 1.0, opened), (0.12, 0.55, cases)), 0.75)
x, pk = clip('Reload/Desert Eagle/mag_in_0.wav')
loader = norm(heavy(to48(x, SR, 1.15), 8000, 0.2))
rattle = norm(rpg_clip('handleCoins2', 0.0, 0.12, 0.8))
save_part(R + 'mag_in_0.wav', mix((0, 1.0, loader), (0, 0.3, rattle)), pk)
save_part(R + 'slide_back_0.wav', body(rpg_clip('metalClick', 0.255, 0.34, 0.85), 0.4, 600), 0.7)
save_part(R + 'slide_forward_0.wav', body(rpg_clip('metalLatch', 0.0, 0.22, 1.08), 0.5, 500), 0.75)

# ---------------- Zeus x27 ----------------

# an electric zap, made here: a hard snap, crackling sparks (sparse clicks through band-passes)
# and a buzzing tone (a mains-like hum with many harmonics, flickering), about 0.35 s
def zap(seed, length=0.36, hum=110.0):
    rng = np.random.default_rng(seed)
    n = int(length * SR); t = np.arange(n) / SR
    env = np.minimum(t / 0.004, 1) * np.where(t > length - 0.08, ((length - t) / 0.08).clip(0, 1) ** 1.5, 1)
    # buzz: a sawtooth with a little vibrato, its level jumping every few ms like an unsteady arc
    f = hum * (1 + 0.02 * np.sin(2 * np.pi * 9 * t + rng.uniform(0, 6)))
    saw = 2 * ((np.cumsum(f) / SR) % 1.0) - 1
    steps = np.repeat(rng.uniform(0.4, 1.0, n // 96 + 1), 96)[:n]
    buzz = bp(saw * steps, 150, 4500) + 0.3 * bp(np.sign(saw) * steps, 2500, 6000)
    # crackle: random clicks, dense at the start and thinning out, each a short burst of noise
    crack = np.zeros(n)
    rate = 1400 * np.exp(-t / 0.06) + 120
    hits = np.where(rng.random(n) < rate / SR)[0]
    for h in hits:
        m = min(n - h, int(rng.uniform(0.0008, 0.003) * SR))
        crack[h:h + m] += rng.uniform(0.2, 1.0) * rng.standard_normal(m) * np.exp(-np.arange(m) / (0.0006 * SR))
    crack = bp(crack, 1500, 8000) + 0.4 * bp(crack, 400, 1500)
    # the first snap of the discharge
    m = int(0.015 * SR)
    snap = np.zeros(n); snap[:m] = rng.standard_normal(m) * np.exp(-np.arange(m) / (0.003 * SR))
    snap = hp(snap, 300)
    y = 0.7 * norm(buzz) + 0.45 * norm(crack) + 1.3 * norm(snap)
    return lp(y * env, 10000)

for i, (seed, hum) in enumerate([(1, 110.0), (2, 118.0), (3, 104.0)]):
    y = zap(seed, 0.35 + 0.02 * i, hum)
    n = int(0.03 * SR); y[-n:] *= np.linspace(1, 0, n)
    write('Weapons/Zeus x27/shot_%d.wav' % i, norm(hp(y, 80), 0.8))
