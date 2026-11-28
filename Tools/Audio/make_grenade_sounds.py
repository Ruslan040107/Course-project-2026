# Synthesizes clean grenade WAV sound effects into Assets/Resources/Audio/Grenades/
# Output: 48 kHz mono 16-bit WAV clips.
# Usage: python Tools/Audio/make_grenade_sounds.py
import os
import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

here = os.path.dirname(os.path.abspath(__file__))
project = os.path.normpath(os.path.join(here, '..', '..'))
out_dir = os.path.join(project, 'Assets', 'Resources', 'Audio', 'Grenades')
os.makedirs(out_dir, exist_ok=True)

SR = 48000


def lp(x, f, order=2):
    return sosfilt(butter(order, f, 'lowpass', fs=SR, output='sos'), x)


def hp(x, f, order=2):
    return sosfilt(butter(order, f, 'highpass', fs=SR, output='sos'), x)


def bp(x, f0, f1, order=2):
    return sosfilt(butter(order, [f0, f1], 'bandpass', fs=SR, output='sos'), x)


def write_wav(name, y, peak=0.78):
    y = np.asarray(y, dtype=np.float64)
    mx = np.abs(y).max()
    if mx > 1e-9:
        y = y / mx * peak
    path = os.path.join(out_dir, name + '.wav')
    wavfile.write(path, SR, (np.clip(y, -1.0, 1.0) * 32767.0).astype(np.int16))
    print('Grenades/%s.wav  %.3f s' % (name, len(y) / SR))


rng = np.random.default_rng(2026)


def make_pin():
    dur = 0.22
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    ring1 = np.sin(2 * np.pi * 4100 * t) * np.exp(-t * 48) * 0.45
    ring2 = np.sin(2 * np.pi * 6300 * t) * np.exp(-t * 65) * 0.35
    scrape = bp(rng.normal(0, 1, len(t)), 2500, 7500) * np.exp(-t * 32) * 0.5
    click_t = np.maximum(0.0, t - 0.055)
    click = np.sin(2 * np.pi * 2400 * click_t) * np.exp(-click_t * 95) * (t >= 0.055) * 0.65
    write_wav('pin', ring1 + ring2 + scrape + click, 0.65)


def make_throw():
    dur = 0.26
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    env = np.sin(np.pi * t / dur) ** 1.6
    n = rng.normal(0, 1, len(t))
    whoosh = bp(n, 350, 1800) * env
    air = lp(n, 700) * env * 0.4
    write_wav('throw', whoosh + air, 0.55)


def make_bounces():
    for i in range(3):
        dur = 0.18
        t = np.linspace(0, dur, int(SR * dur), endpoint=False)
        f0 = 880 + i * 140
        f1 = 2150 + i * 210
        ping = (np.sin(2 * np.pi * f0 * t) * 0.5 + np.sin(2 * np.pi * f1 * t) * 0.35) * np.exp(-t * 42)
        thud = np.sin(2 * np.pi * (170 + i * 25) * t) * np.exp(-t * 55) * 0.6
        n = bp(rng.normal(0, 1, len(t)), 900, 4500) * np.exp(-t * 70) * 0.5
        write_wav('bounce_%d' % i, ping + thud + n, 0.62)


def make_he():
    for i in range(2):
        dur = 1.65
        t = np.linspace(0, dur, int(SR * dur), endpoint=False)
        freq = 42.0 + 140.0 * np.exp(-t * 24.0)
        phase = 2 * np.pi * np.cumsum(freq) / SR
        sub = np.sin(phase) * np.exp(-t * 3.8) * 1.15
        n1 = rng.normal(0, 1, len(t))
        crack = hp(n1, 1100) * np.exp(-t * 16.0) * 0.85
        rumble = lp(rng.normal(0, 1, len(t)), 320) * np.exp(-t * 2.6) * 1.0
        # Shrapnel tails
        shrap = bp(rng.normal(0, 1, len(t)), 1400, 6200) * np.exp(-t * 5.2) * (0.25 + 0.15 * np.sin(t * (28 + i * 7)))
        y = np.tanh((sub + crack + rumble + shrap) * 1.4)
        fade = int(0.15 * SR)
        y[-fade:] *= np.linspace(1, 0, fade) ** 2
        write_wav('he_explode_%d' % i, y, 0.92)


def make_flash():
    dur = 1.15
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    n = rng.normal(0, 1, len(t))
    snap = hp(n, 1600) * np.exp(-t * 28.0) * 1.2
    thump = np.sin(2 * np.pi * (55 + 220 * np.exp(-t * 35)) * t) * np.exp(-t * 11.0) * 0.9
    tail = bp(rng.normal(0, 1, len(t)), 500, 3500) * np.exp(-t * 4.5) * 0.45
    y = np.tanh((snap + thump + tail) * 1.5)
    fade = int(0.12 * SR)
    y[-fade:] *= np.linspace(1, 0, fade) ** 2
    write_wav('flash_explode', y, 0.9)


def make_flash_ring():
    dur = 3.6
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    attack = np.minimum(1.0, t / 0.04)
    decay = np.exp(-t * 0.85) * np.maximum(0.0, 1.0 - np.maximum(0.0, t - 2.6))
    tone = 0.72 * np.sin(2 * np.pi * 4320 * t) + 0.28 * np.sin(2 * np.pi * 6480 * t)
    write_wav('flash_ring', tone * attack * decay, 0.45)


def make_smoke_bloom():
    dur = 2.6
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    n = rng.normal(0, 1, len(t))
    pop = bp(rng.normal(0, 1, len(t)), 150, 900) * np.exp(-t * 22.0) * 0.8
    hiss = bp(n, 700, 5200) * np.minimum(1.0, t / 0.08) * np.exp(-t * 0.85) * 0.65
    air = lp(rng.normal(0, 1, len(t)), 600) * np.minimum(1.0, t / 0.12) * np.exp(-t * 1.1) * 0.45
    y = pop + hiss + air
    fade = int(0.25 * SR)
    y[-fade:] *= np.linspace(1, 0, fade) ** 2
    write_wav('smoke_bloom', y, 0.72)


def make_molotov_break():
    dur = 1.25
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    # Glass shatter bursts + ignition whoosh
    shatter = np.zeros_like(t)
    for delay in (0.0, 0.025, 0.06, 0.11):
        dt = np.maximum(0.0, t - delay)
        mask = t >= delay
        shatter += mask * bp(rng.normal(0, 1, len(t)), 2800, 9500) * np.exp(-dt * 45) * 0.6
        for f in (3400, 4900, 6700):
            shatter += mask * np.sin(2 * np.pi * (f + rng.integers(-250, 250)) * dt) * np.exp(-dt * 55) * 0.18
    whoosh_env = np.minimum(1.0, t / 0.06) * np.exp(-t * 2.4)
    whoosh = bp(rng.normal(0, 1, len(t)), 180, 1400) * whoosh_env * 1.1
    fade = int(0.15 * SR)
    y = np.tanh((shatter + whoosh) * 1.25)
    y[-fade:] *= np.linspace(1, 0, fade) ** 2
    write_wav('molotov_break', y, 0.84)


def make_incendiary_detonate():
    dur = 1.1
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    pop = np.sin(2 * np.pi * (80 + 180 * np.exp(-t * 30)) * t) * np.exp(-t * 14) * 0.75
    thermite = bp(rng.normal(0, 1, len(t)), 900, 6000) * np.exp(-t * 2.6) * 0.9
    y = np.tanh((pop + thermite) * 1.3)
    fade = int(0.12 * SR)
    y[-fade:] *= np.linspace(1, 0, fade) ** 2
    write_wav('incendiary_detonate', y, 0.80)


def make_fire_loop():
    dur = 3.0
    n_samples = int(SR * dur)
    n = rng.normal(0, 1, n_samples)
    roar = bp(n, 140, 850) * 0.55
    crackle_raw = bp(rng.normal(0, 1, n_samples), 1600, 6500)
    # Sparse crackle spikes
    spikes = (rng.random(n_samples) < 0.0028).astype(np.float64)
    crackle_env = lp(spikes, 180) * 14.0
    y = roar + crackle_raw * crackle_env
    # Crossfade ends for seamless loop
    xf = int(0.15 * SR)
    w = np.linspace(0, 1, xf)
    y[:xf] = y[:xf] * w + y[-xf:] * (1 - w)
    y = y[:-xf]
    write_wav('fire_loop', y, 0.55)


def make_fire_extinguish():
    dur = 1.2
    t = np.linspace(0, dur, int(SR * dur), endpoint=False)
    env = np.minimum(1.0, t / 0.04) * np.exp(-t * 2.5)
    steam = bp(rng.normal(0, 1, len(t)), 1100, 7200) * env
    fade = int(0.15 * SR)
    steam[-fade:] *= np.linspace(1, 0, fade) ** 2
    write_wav('fire_extinguish', steam, 0.70)


make_pin()
make_throw()
make_bounces()
make_he()
make_flash()
make_flash_ring()
make_smoke_bloom()
make_molotov_break()
make_incendiary_detonate()
make_fire_loop()
make_fire_extinguish()
print('All grenade sounds generated in', out_dir)
