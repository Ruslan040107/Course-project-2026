# Decodes every .ogg/.flac under a folder into <name>.dec.wav next to it (source rate, mono, 16 bit),
# for the scripts here that read only .wav (scipy). Uses the audio library built into Blender.
# usage: blender -b --factory-startup -P decode_blender.py -- <folder>
import sys, os, aud, wave, numpy as np

root = sys.argv[sys.argv.index('--') + 1]
n = 0
for dp, dn, fn in os.walk(root):
    if '__MACOSX' in dp: continue
    for f in fn:
        if not f.lower().endswith(('.ogg', '.flac')): continue
        src = os.path.join(dp, f); dst = os.path.splitext(src)[0] + '.dec.wav'
        try:
            s = aud.Sound(src)
            rate = int(s.specs[0]); x = np.asarray(s.data(), dtype=np.float64)
            if x.ndim > 1: x = x.mean(axis=1)
            with wave.open(dst, 'wb') as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
                w.writeframes((np.clip(x, -1, 1) * 32767).astype('<i2').tobytes())
            n += 1
        except Exception as e:
            print('FAIL', src, e)
print('decoded', n)
