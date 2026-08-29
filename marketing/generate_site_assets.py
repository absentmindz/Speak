from __future__ import annotations

import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageOps


# Keep generated web artwork on the same monochrome system as the Windows app.
# MainWindow.xaml is the source of truth for these colors.
CANVAS = "#111214"
PANEL = "#18191C"
INK = "#D8D3C7"
MUTED = "#918D84"
LINE = "#2B2C30"
ACCENT = "#B8B1A4"
BRIGHT = "#CBC4B5"


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    fonts_root = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
    candidates = {
        "regular": ("SegoeUIVF.ttf", "segoeui.ttf"),
        "bold": ("seguisb.ttf", "segoeuib.ttf"),
    }[name]
    for candidate in candidates:
        path = fonts_root / candidate
        if path.is_file():
            return ImageFont.truetype(path, size=size)
    raise FileNotFoundError(f"Segoe UI was not found in {fonts_root}")


def fit(source: Image.Image, size: tuple[int, int]) -> Image.Image:
    copy = source.copy()
    copy.thumbnail(size, Image.Resampling.LANCZOS)
    return copy


def wrap(draw: ImageDraw.ImageDraw, text: str, face: ImageFont.FreeTypeFont, width: int) -> list[str]:
    lines: list[str] = []
    current = ""
    for word in text.split():
        candidate = word if not current else f"{current} {word}"
        if draw.textlength(candidate, font=face) <= width:
            current = candidate
        else:
            if current:
                lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines


def square_logo(source: Image.Image, size: int, padding: int = 0) -> Image.Image:
    logo = source.convert("RGBA")
    logo.thumbnail((size - 2 * padding, size - 2 * padding), Image.Resampling.LANCZOS)
    output = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    output.alpha_composite(logo, ((size - logo.width) // 2, (size - logo.height) // 2))
    return output


def framed_screenshot(source: Image.Image, size: tuple[int, int]) -> Image.Image:
    fitted = fit(source.convert("RGB"), (size[0] - 2, size[1] - 2))
    frame = Image.new("RGB", size, PANEL)
    frame.paste(fitted, ((size[0] - fitted.width) // 2, (size[1] - fitted.height) // 2))
    draw = ImageDraw.Draw(frame)
    draw.rectangle((0, 0, size[0] - 1, size[1] - 1), outline=LINE, width=2)
    return frame


def build_social_card(logo: Image.Image, screenshot: Image.Image, output: Path) -> None:
    image = Image.new("RGB", (1200, 630), CANVAS)
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 1200, 10), fill=ACCENT)
    draw.line((620, 54, 620, 576), fill=LINE, width=1)
    draw.rectangle((660, 54, 1152, 576), fill=PANEL, outline=LINE, width=2)

    mark = square_logo(logo, 62)
    image.paste(mark, (70, 62), mark)
    draw.text((146, 72), "SPEAK", font=font("bold", 31), fill=INK)
    draw.text((70, 154), "VOICE WRITING FOR WINDOWS", font=font("bold", 17), fill=MUTED)

    title_face = font("bold", 58)
    y = 194
    for line in wrap(draw, "Write anywhere on Windows—just by speaking.", title_face, 535):
        draw.text((70, y), line, font=title_face, fill=INK)
        y += 66

    draw.text((70, 470), "Private. Local-first. Open source.", font=font("regular", 25), fill=MUTED)
    draw.rectangle((70, 530, 334, 580), fill=BRIGHT)
    draw.text((92, 543), "DOWNLOAD COMMUNITY", font=font("bold", 18), fill=CANVAS)

    shot = framed_screenshot(screenshot, (430, 313))
    image.paste(shot, (691, 115))
    draw.text((691, 464), "One shortcut. Polished text.", font=font("bold", 24), fill=INK)
    draw.text((691, 504), "Windows 10/11  ·  Apache-2.0", font=font("regular", 18), fill=MUTED)

    image.save(output, optimize=True)


def build_poster(logo: Image.Image, screenshot: Image.Image, output: Path) -> None:
    image = Image.new("RGB", (1280, 720), CANVAS)
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 1280, 10), fill=ACCENT)

    mark = square_logo(logo, 72)
    image.paste(mark, (64, 56), mark)
    draw.text((150, 73), "SPEAK", font=font("bold", 31), fill=INK)
    draw.text((64, 178), "60-SECOND PRODUCT TOUR", font=font("bold", 17), fill=ACCENT)

    title_face = font("bold", 55)
    y = 218
    for line in wrap(draw, "Talk naturally. Get polished text.", title_face, 490):
        draw.text((64, y), line, font=title_face, fill=INK)
        y += 64

    body_face = font("regular", 24)
    y += 18
    for line in wrap(draw, "See dictation, history, your personal dictionary, and privacy controls.", body_face, 470):
        draw.text((64, y), line, font=body_face, fill=MUTED)
        y += 35

    draw.rectangle((64, 582, 280, 586), fill=ACCENT)
    draw.text((64, 606), "PRODUCT DEMO · 60 SECONDS", font=font("bold", 17), fill=INK)

    draw.rectangle((608, 72, 1216, 650), fill=PANEL, outline=LINE, width=2)
    shot = framed_screenshot(screenshot, (548, 397))
    image.paste(shot, (638, 123))
    draw.text((638, 558), "Private, local-first voice writing", font=font("bold", 25), fill=INK)
    draw.text((638, 598), "Windows 10/11  ·  Community edition", font=font("regular", 18), fill=MUTED)

    image.save(output, quality=90, optimize=True, progressive=True, subsampling=0)


def main() -> None:
    repository = Path(__file__).resolve().parents[1]
    docs = repository / "docs"
    assets = docs / "assets"
    screenshots = docs / "screenshots"
    demo = docs / "demo"
    assets.mkdir(parents=True, exist_ok=True)
    demo.mkdir(parents=True, exist_ok=True)

    logo = Image.open(assets / "speak-logo.png")
    screenshot = Image.open(screenshots / "main.png")

    square_logo(logo, 64).save(assets / "speak-logo-64.png", optimize=True)
    square_logo(logo, 32).save(assets / "favicon-32.png", optimize=True)
    square_logo(logo, 192, padding=16).save(assets / "speak-icon-192.png", optimize=True)
    square_logo(logo, 512, padding=40).save(assets / "speak-icon-512.png", optimize=True)

    apple_icon = Image.new("RGBA", (180, 180), CANVAS)
    apple_mark = square_logo(logo, 148)
    apple_icon.alpha_composite(apple_mark, (16, 16))
    apple_icon.convert("RGB").save(assets / "apple-touch-icon.png", optimize=True)

    screenshot.convert("RGB").save(
        screenshots / "main.webp",
        "WEBP",
        quality=84,
        method=6,
    )

    build_social_card(logo, screenshot, assets / "speak-social-card.png")
    build_poster(logo, screenshot, demo / "speak-demo-poster.jpg")


if __name__ == "__main__":
    main()
