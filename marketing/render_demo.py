from __future__ import annotations

import argparse
import os
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

WIDTH = 1920
HEIGHT = 1080
BG = "#111214"
PANEL = "#18191c"
LINE = "#303136"
INK = "#f0ece3"
MUTED = "#aaa397"
ACCENT = "#cbc4b5"


@dataclass(frozen=True)
class Slide:
    duration: int
    eyebrow: str
    title: str
    body: str
    image: str | None = None
    callout: str | None = None


def font(path: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(path, size=size)


def wrap(draw: ImageDraw.ImageDraw, text: str, typeface: ImageFont.FreeTypeFont, width: int) -> list[str]:
    words = text.split()
    lines: list[str] = []
    current = ""
    for word in words:
        candidate = word if not current else f"{current} {word}"
        if draw.textlength(candidate, font=typeface) <= width:
            current = candidate
        else:
            if current:
                lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines


def fit_image(source: Image.Image, box: tuple[int, int]) -> Image.Image:
    copy = source.convert("RGB")
    copy.thumbnail(box, Image.Resampling.LANCZOS)
    return copy


def render_slide(repo: Path, slide: Slide, destination: Path, index: int) -> None:
    image = Image.new("RGB", (WIDTH, HEIGHT), BG)
    draw = ImageDraw.Draw(image)
    fonts_root = Path(os.environ.get("WINDIR", "")) / "Fonts"
    bold_path = str(fonts_root / "segoeuib.ttf")
    regular_path = str(fonts_root / "segoeui.ttf")
    title_font = font(bold_path, 70 if slide.image else 86)
    eyebrow_font = font(bold_path, 22)
    body_font = font(regular_path, 31)
    micro_font = font(regular_path, 22)

    draw.rectangle((0, 0, WIDTH, 10), fill=ACCENT)
    draw.text((90, 82), slide.eyebrow, font=eyebrow_font, fill=ACCENT)

    if slide.image:
        left_width = 620
        title_lines = wrap(draw, slide.title, title_font, left_width - 20)
        y = 150
        for line in title_lines:
            draw.text((90, y), line, font=title_font, fill=INK)
            y += 82
        y += 18
        for line in wrap(draw, slide.body, body_font, left_width - 15):
            draw.text((90, y), line, font=body_font, fill=MUTED)
            y += 45
        if slide.callout:
            y += 24
            draw.rounded_rectangle((90, y, 585, y + 82), radius=12, fill=PANEL, outline=LINE, width=2)
            draw.text((118, y + 24), slide.callout, font=micro_font, fill=INK)

        source = Image.open(repo / slide.image)
        fitted = fit_image(source, (1110, 820))
        x = 720 + (1110 - fitted.width) // 2
        y_img = 145 + (820 - fitted.height) // 2
        shadow = Image.new("RGBA", image.size, (0, 0, 0, 0))
        shadow_draw = ImageDraw.Draw(shadow)
        shadow_draw.rounded_rectangle((x - 20, y_img - 20, x + fitted.width + 20, y_img + fitted.height + 20), radius=28, fill=(0, 0, 0, 100))
        image = Image.alpha_composite(image.convert("RGBA"), shadow).convert("RGB")
        image.paste(fitted, (x, y_img))
        draw = ImageDraw.Draw(image)
        draw.rounded_rectangle((x - 2, y_img - 2, x + fitted.width + 2, y_img + fitted.height + 2), radius=16, outline=LINE, width=3)
    else:
        logo = Image.open(repo / "speak_logo.png").convert("RGBA")
        logo.thumbnail((190, 190), Image.Resampling.LANCZOS)
        image.paste(logo, ((WIDTH - logo.width) // 2, 150), logo)
        draw = ImageDraw.Draw(image)
        title_lines = wrap(draw, slide.title, title_font, 1460)
        y = 390
        for line in title_lines:
            w = draw.textlength(line, font=title_font)
            draw.text(((WIDTH - w) / 2, y), line, font=title_font, fill=INK)
            y += 100
        y += 8
        for line in wrap(draw, slide.body, body_font, 1250):
            w = draw.textlength(line, font=body_font)
            draw.text(((WIDTH - w) / 2, y), line, font=body_font, fill=MUTED)
            y += 48
        if slide.callout:
            y += 25
            w = draw.textlength(slide.callout, font=eyebrow_font)
            draw.rounded_rectangle(((WIDTH - w) / 2 - 28, y - 12, (WIDTH + w) / 2 + 28, y + 48), radius=12, fill=ACCENT)
            draw.text(((WIDTH - w) / 2, y), slide.callout, font=eyebrow_font, fill=BG)

    draw.text((90, HEIGHT - 64), "Speak · private, local-first voice writing for Windows", font=micro_font, fill=MUTED)
    counter = f"{index:02d}"
    draw.text((WIDTH - 120, HEIGHT - 64), counter, font=micro_font, fill=ACCENT)
    image.save(destination, quality=95)


def run(command: list[str]) -> None:
    subprocess.run(command, check=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    repo = Path(__file__).resolve().parents[1]
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)

    slides = [
        Slide(6, "PRIVATE · LOCAL-FIRST · WINDOWS", "Talk naturally. Get polished text.", "Speak turns your voice into useful writing in any Windows application.", callout="One shortcut. Your words. Your control."),
        Slide(12, "DICTATE", "Press one shortcut and speak naturally.", "Record from anywhere, transcribe with your chosen engine, polish the result, and paste it into the active app.", "docs/screenshots/main.png", "Global hotkey · local or configured cloud STT"),
        Slide(10, "HISTORY", "Keep useful transcripts close.", "Browse saved local transcripts, search them, reopen the messages worth refining, and export when you choose.", "docs/screenshots/history.png", "Local history with clear retention controls"),
        Slide(10, "PERSONAL DICTIONARY", "Teach Speak the words you use.", "Save names, products, technical terms, symbols, and preferred written forms so repeated corrections become unnecessary.", "docs/screenshots/dictionary.png", "Your vocabulary, on your machine"),
        Slide(9, "VOICE PROFILE", "See what the app has learned.", "Review corrections, usage, accuracy signals, and progress without turning your writing habits into an advertising profile.", "docs/screenshots/voice-profile.png", "Corrections and progress stay visible"),
        Slide(7, "OPTIONAL LOCAL AUDIO", "Go beyond dictation when you need to.", "Use separately configured local voice tools, preview outputs, and keep heavy models offloaded when idle.", "docs/screenshots/audio-studio.png", "Advanced tools—without changing the core promise"),
        Slide(6, "SPEAK COMMUNITY + FOUNDING PRO", "Use your voice. Keep control.", "Download the Apache-2.0 Community release today. Join the planned Founding 100 for guided setup, priority support, and future official convenience features.", callout="github.com/absentmindz/Speak"),
    ]

    with tempfile.TemporaryDirectory(prefix="speak-demo-") as temp_name:
        temp = Path(temp_name)
        segments: list[Path] = []
        for index, slide in enumerate(slides, start=1):
            png = temp / f"slide-{index:02d}.png"
            mp4 = temp / f"segment-{index:02d}.mp4"
            render_slide(repo, slide, png, index)
            fade_out = max(0.0, slide.duration - 0.45)
            run([
                args.ffmpeg,
                "-hide_banner", "-loglevel", "error", "-y",
                "-loop", "1", "-i", str(png),
                "-t", str(slide.duration),
                "-vf", f"fps=30,fade=t=in:st=0:d=0.45,fade=t=out:st={fade_out}:d=0.45,format=yuv420p",
                "-an", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-movflags", "+faststart",
                str(mp4),
            ])
            segments.append(mp4)

        concat_file = temp / "segments.txt"
        concat_file.write_text("\n".join(f"file '{segment.as_posix()}'" for segment in segments), encoding="utf-8")
        run([
            args.ffmpeg,
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "concat", "-safe", "0", "-i", str(concat_file),
            "-c", "copy", "-movflags", "+faststart", str(output),
        ])

    print(output)


if __name__ == "__main__":
    main()
