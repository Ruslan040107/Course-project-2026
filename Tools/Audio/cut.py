import sys, os, numpy as np
from scipy.io import wavfile
from scipy.signal import resample_poly, butter, sosfilt
import warnings; warnings.filterwarnings('ignore')
root, out = sys.argv[1], sys.argv[2]
SR = 48000
def load(path):
    sr, x = wavfile.read(path)
    x = x.astype(np.float32)
    if x.ndim > 1: x = x.mean(axis=1)
    return sr, x
def onsets(sr, x, frac=0.3, gap=0.25):
    env = np.abs(x); thr = frac * env.max(); hop = int(0.002 * sr)
    fr = env[:len(env)//hop*hop].reshape(-1, hop).max(axis=1)
    on = []; last = -1e9
    for i, v in enumerate(fr):
        t = i * hop / sr
        if v > thr and t - last > gap: on.append(t)
        if v > thr: last = t
    return on
def cut(sr, x, t, length):
    a = max(0, int((t - 0.004) * sr)); b = min(len(x), int((t + length) * sr))
    seg = x[a:b].copy()
    # stop before a second shot inside the window
    env = np.abs(seg); pk = env.max(); start = int(0.09 * sr)
    later = np.where(env[start:] > 0.5 * pk)[0]
    if len(later): seg = seg[:start + later[0] - int(0.004 * sr)]
    n = len(seg); f = int(n * 0.35)
    seg[n - f:] *= np.linspace(1, 0, f) ** 2
    seg[:int(0.002 * sr)] *= np.linspace(0, 1, int(0.002 * sr))
    return seg
def save(path, x, sr_in, gain=0.9):
    y = resample_poly(x, 1, sr_in // SR) if sr_in != SR else x
    y = sosfilt(butter(2, 60, 'highpass', fs=SR, output='sos'), y)   # removes the low rumble of the microphone
    y = y / (np.abs(y).max() + 1e-9) * gain
    os.makedirs(os.path.dirname(path), exist_ok=True)
    wavfile.write(path, SR, (y * 32767).astype(np.int16))
    return len(y) / SR
plan = {
  "AK-47":        (["AK-47"], 1.1),
  "M4A4":         (["AR-15"], 1.0),
  "AWP":          (["Tikka"], 1.6),
  "Desert Eagle": (["1911"], 0.9),
  "Glock-18":     (["Walther PPQ"], 0.8),
  "MP9":          (["PPSh"], 0.7),
}
for weapon, (dirs, length) in plan.items():
    cands = []
    for d in dirs:
        for f in sorted(os.listdir(os.path.join(root, d))):
            if not f.endswith('.wav'): continue
            sr, x = load(os.path.join(root, d, f))
            for t in onsets(sr, x):
                seg = cut(sr, x, t, length)
                if len(seg) > 0.25 * sr: cands.append((np.abs(seg).max(), f, t, sr, seg))
    cands.sort(key=lambda c: -c[0])
    for i, (pk, f, t, sr, seg) in enumerate(cands[:3]):
        d = save(os.path.join(out, 'Weapons', weapon, 'shot_%d.wav' % i), seg, sr)
        print(weapon, i, f, round(t, 2), 'len', round(d, 2))
# USP-S: quiet .22 shots, filtered to sound suppressed
sos_lp = butter(4, 1500, 'lowpass', fs=96000, output='sos'); sos_hp = butter(2, 180, 'highpass', fs=96000, output='sos')
k = 0
for f in sorted(os.listdir(os.path.join(root, 'Ruger Mark III'))):
    if not f.endswith('.wav') or k >= 3: continue
    sr, x = load(os.path.join(root, 'Ruger Mark III', f))
    for t in onsets(sr, x):
        if k >= 3: break
        seg = cut(sr, x, t, 0.45)
        seg = sosfilt(sos_hp, sosfilt(sos_lp, seg))
        d = save(os.path.join(out, 'Weapons', 'USP-S', 'shot_%d.wav' % k), seg, sr, gain=0.75)
        print('USP-S', k, f, round(t, 2), 'len', round(d, 2)); k += 1
# the knife sounds are made by make_knife_sounds.py now
