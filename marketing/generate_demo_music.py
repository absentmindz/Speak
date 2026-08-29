from __future__ import annotations

import argparse
import hashlib
import math
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np


SAMPLE_RATE = 48_000
DURATION_SECONDS = 60.0
SEED = 21_042

# Eight 7.5-second harmonic fields. The second pass uses inversions so the bed
# evolves without competing with the product copy on screen.
CHORDS = (
    (73.416, 146.832, 220.000, 261.626, 329.628),   # Dm9
    (58.270, 116.541, 174.614, 220.000, 261.626),   # Bbmaj9
    (87.307, 130.813, 174.614, 195.998, 261.626),   # Fadd9
    (65.406, 130.813, 195.998, 293.665, 329.628),   # Cadd9
    (73.416, 220.000, 261.626, 293.665, 349.228),   # Dm9 inversion
    (58.270, 174.614, 220.000, 293.665, 349.228),   # Bbmaj9 inversion
    (87.307, 174.614, 195.998, 261.626, 329.628),   # Fadd9 inversion
    (65.406, 195.998, 261.626, 293.665, 391.995),   # Cadd9 inversion
)


def run(command: list[str]) -> None:
    subprocess.run(command, check=True)


def raised_cosine_envelope(
    frame_count: int,
    attack_seconds: float,
    release_seconds: float,
) -> np.ndarray:
    envelope = np.ones(frame_count, dtype=np.float32)
    attack_frames = min(frame_count, round(attack_seconds * SAMPLE_RATE))
    release_frames = min(frame_count, round(release_seconds * SAMPLE_RATE))

    if attack_frames:
        phase = np.linspace(0.0, math.pi / 2.0, attack_frames, endpoint=True)
        envelope[:attack_frames] = np.sin(phase).astype(np.float32) ** 2
    if release_frames:
        phase = np.linspace(math.pi / 2.0, 0.0, release_frames, endpoint=True)
        envelope[-release_frames:] *= np.sin(phase).astype(np.float32) ** 2
    return envelope


def add_tone(
    audio: np.ndarray,
    *,
    start_seconds: float,
    duration_seconds: float,
    frequency: float,
    amplitude: float,
    pan: float,
    attack_seconds: float,
    release_seconds: float,
    phase: float,
    harmonics: tuple[tuple[float, float], ...],
) -> None:
    start = round(start_seconds * SAMPLE_RATE)
    end = min(audio.shape[1], start + round(duration_seconds * SAMPLE_RATE))
    if end <= start:
        return

    frame_count = end - start
    time = np.arange(frame_count, dtype=np.float64) / SAMPLE_RATE
    signal = np.zeros(frame_count, dtype=np.float32)
    for multiple, level in harmonics:
        partial = np.sin((2.0 * math.pi * frequency * multiple * time) + phase)
        signal += (partial * level).astype(np.float32)

    signal *= raised_cosine_envelope(frame_count, attack_seconds, release_seconds)
    signal *= amplitude

    # Equal-power panning keeps the center from becoming louder than the sides.
    angle = (max(-1.0, min(1.0, pan)) + 1.0) * math.pi / 4.0
    audio[0, start:end] += signal * math.cos(angle)
    audio[1, start:end] += signal * math.sin(angle)


def add_pluck(
    audio: np.ndarray,
    *,
    start_seconds: float,
    frequency: float,
    amplitude: float,
    pan: float,
    phase: float,
) -> None:
    start = round(start_seconds * SAMPLE_RATE)
    frame_count = min(round(1.8 * SAMPLE_RATE), audio.shape[1] - start)
    if frame_count <= 0:
        return

    time = np.arange(frame_count, dtype=np.float64) / SAMPLE_RATE
    envelope = (1.0 - np.exp(-time / 0.018)) * np.exp(-time / 0.62)
    signal = (
        np.sin(2.0 * math.pi * frequency * time + phase)
        + 0.24 * np.sin(2.0 * math.pi * frequency * 2.01 * time + phase / 2.0)
        + 0.08 * np.sin(2.0 * math.pi * frequency * 3.98 * time)
    )
    signal = (signal * envelope * amplitude).astype(np.float32)

    angle = (max(-1.0, min(1.0, pan)) + 1.0) * math.pi / 4.0
    audio[0, start : start + frame_count] += signal * math.cos(angle)
    audio[1, start : start + frame_count] += signal * math.sin(angle)


def compose() -> np.ndarray:
    frame_count = round(SAMPLE_RATE * DURATION_SECONDS)
    audio = np.zeros((2, frame_count), dtype=np.float32)

    for chord_index, chord in enumerate(CHORDS):
        start = chord_index * 7.5
        duration = min(9.0, DURATION_SECONDS - start)

        # A low, slow sine foundation anchors each section.
        add_tone(
            audio,
            start_seconds=start,
            duration_seconds=duration,
            frequency=chord[0],
            amplitude=0.070,
            pan=-0.05 if chord_index % 2 == 0 else 0.05,
            attack_seconds=1.25,
            release_seconds=min(2.1, duration / 3.0),
            phase=chord_index * 0.31,
            harmonics=((1.0, 1.0), (2.0, 0.15)),
        )

        # Wide, gently detuned pad voices create movement without a lead melody.
        for note_index, frequency in enumerate(chord[1:]):
            pan = (-0.72, -0.24, 0.24, 0.72)[note_index]
            amplitude = 0.034 if note_index < 2 else 0.028
            add_tone(
                audio,
                start_seconds=start,
                duration_seconds=duration,
                frequency=frequency,
                amplitude=amplitude,
                pan=pan,
                attack_seconds=1.55,
                release_seconds=min(2.25, duration / 3.0),
                phase=(chord_index + 1) * (note_index + 1) * 0.19,
                harmonics=((1.0, 1.0), (2.0, 0.16), (3.0, 0.035)),
            )
            add_tone(
                audio,
                start_seconds=start + 0.08,
                duration_seconds=max(0.1, duration - 0.08),
                frequency=frequency * 1.00115,
                amplitude=amplitude * 0.42,
                pan=-pan,
                attack_seconds=1.7,
                release_seconds=min(2.1, duration / 3.0),
                phase=(note_index + 1) * 0.43,
                harmonics=((1.0, 1.0), (2.0, 0.10)),
            )

    # A quiet pulse every 1.875 seconds gives the bed a restrained electronic
    # cadence. Its pitches are derived entirely from the active chord.
    pulse_count = int(DURATION_SECONDS / 1.875)
    for pulse_index in range(pulse_count):
        start = pulse_index * 1.875 + 0.34
        chord = CHORDS[min(len(CHORDS) - 1, int(start / 7.5))]
        frequency = chord[2 + (pulse_index % 3)] * 2.0
        add_pluck(
            audio,
            start_seconds=start,
            frequency=frequency,
            amplitude=0.023 if pulse_index % 4 else 0.029,
            pan=(-0.48, 0.18, 0.48, -0.18)[pulse_index % 4],
            phase=pulse_index * 0.17,
        )

    # Cross-channel early reflections provide a small-room sense of depth.
    dry = audio.copy()
    for delay_seconds, same_gain, cross_gain in (
        (0.173, 0.10, 0.035),
        (0.293, 0.075, 0.045),
        (0.431, 0.050, 0.030),
        (0.619, 0.032, 0.020),
    ):
        delay = round(delay_seconds * SAMPLE_RATE)
        audio[0, delay:] += dry[0, :-delay] * same_gain + dry[1, :-delay] * cross_gain
        audio[1, delay:] += dry[1, :-delay] * same_gain + dry[0, :-delay] * cross_gain

    # The source has its own edge protection; the final renderer adds the longer
    # editorial fades and broadcast-style loudness normalization.
    master_fade_in = round(0.6 * SAMPLE_RATE)
    master_fade_out = round(2.8 * SAMPLE_RATE)
    audio[:, :master_fade_in] *= np.linspace(0.0, 1.0, master_fade_in, dtype=np.float32)
    audio[:, -master_fade_out:] *= np.linspace(1.0, 0.0, master_fade_out, dtype=np.float32)

    peak = float(np.max(np.abs(audio)))
    if peak == 0.0:
        raise RuntimeError("The synthesized soundtrack is silent.")
    audio *= 0.48 / peak

    # Deterministic triangular dither prevents correlated quantization noise.
    random = np.random.default_rng(SEED)
    dither = (
        random.random(audio.shape, dtype=np.float32)
        - random.random(audio.shape, dtype=np.float32)
    ) / 65_536.0
    return np.clip(audio + dither, -1.0, 1.0)


def write_pcm_wav(path: Path, audio: np.ndarray) -> None:
    interleaved = np.rint(audio.T * 32_767.0).astype("<i2", copy=False)
    with wave.open(str(path), "wb") as destination:
        destination.setnchannels(2)
        destination.setsampwidth(2)
        destination.setframerate(SAMPLE_RATE)
        destination.writeframes(interleaved.tobytes())


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Generate Speak's deterministic, project-original demo soundtrack."
    )
    parser.add_argument("--ffmpeg", required=True, help="Path to ffmpeg.exe.")
    parser.add_argument("--output", required=True, help="Destination .m4a path.")
    args = parser.parse_args()

    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="speak-demo-music-") as temp_name:
        wav = Path(temp_name) / "speak-demo-original-bed.wav"
        write_pcm_wav(wav, compose())
        run([
            args.ffmpeg,
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", str(wav),
            "-map_metadata", "-1",
            "-c:a", "alac", "-compression_level", "5",
            "-ar", str(SAMPLE_RATE), "-ac", "2",
            "-movflags", "+faststart",
            "-metadata", "title=Speak — Quiet Momentum",
            "-metadata", "artist=Speak Project",
            "-metadata", "album=Speak Demo",
            "-metadata", "comment=Project-original deterministic composition; no samples or third-party audio.",
            str(output),
        ])

    print(f"{output}")
    print(f"sha256={sha256(output)}")


if __name__ == "__main__":
    main()
