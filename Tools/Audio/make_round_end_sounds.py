# Round win sounds (GameAudio plays them when a team wins a round).
#   Audio/RoundEnd/Default/{t_win, ct_win}.wav — from Kenney "Music Jingles" (CC0): jingles_HIT03 / jingles_HIT11.
#   Audio/RoundEnd/Local/{t_win, ct_win}.wav  — the author's own files (optional, not in git: see .gitignore);
#                                               GameAudio prefers them when they exist.
# Each clip: silence cut at both ends, a short fade-out, peak normalised, 44.1 kHz mono 16 bit.
# Uses the audio library built into Blender (decodes mp3 / ogg):
#   blender -b --factory-startup -P make_round_end_sounds.py -- <out Assets/Resources/Audio> <kenney Audio folder> [t_win.mp3 ct_win.mp3]
import os
import sys
import wave

import aud
import numpy as np

RATE = 44100


def load(path):
    s = aud.Sound(path).resample(RATE, False)
    x = np.asarray(s.data(), dtype=np.float64)
    return x.mean(axis=1) if x.ndim > 1 else x


def clean(x, peak, threshold=0.02, fade=0.08):
    a = np.abs(x)
    idx = np.where(a > threshold * a.max())[0]
    if len(idx):
        x = x[max(0, idx[0] - int(0.01 * RATE)):min(len(x), idx[-1] + int(0.05 * RATE))]
    n = min(len(x), int(fade * RATE))
    if n > 0:
        x = x.copy()
        x[-n:] *= np.linspace(1.0, 0.0, n)
    return x * (peak / max(np.abs(x).max(), 1e-9))


def save(x, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype('<i2').tobytes())
    print('wrote', path, '%.2f s' % (len(x) / RATE))


args = sys.argv[sys.argv.index('--') + 1:]
out, kenney = args[0], args[1]
save(clean(load(os.path.join(kenney, 'Hit jingles', 'jingles_HIT03.ogg')), 0.7), os.path.join(out, 'RoundEnd', 'Default', 't_win.wav'))
save(clean(load(os.path.join(kenney, 'Hit jingles', 'jingles_HIT11.ogg')), 0.7), os.path.join(out, 'RoundEnd', 'Default', 'ct_win.wav'))
if len(args) >= 4:
    save(clean(load(args[2]), 0.85), os.path.join(out, 'RoundEnd', 'Local', 't_win.wav'))
    save(clean(load(args[3]), 0.85), os.path.join(out, 'RoundEnd', 'Local', 'ct_win.wav'))
