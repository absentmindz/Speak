from __future__ import annotations

import json
import re
import unittest
from collections import Counter
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


REPOSITORY = Path(__file__).resolve().parents[2]
SITE = REPOSITORY / "docs"


class SiteParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.ids: list[str] = []
        self.references: list[tuple[str, str, str]] = []
        self.fragment_links: list[str] = []
        self.aria_references: list[tuple[str, str, str]] = []
        self.h1_count = 0
        self.tags: list[tuple[str, dict[str, str | None]]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attributes = dict(attrs)
        self.tags.append((tag, attributes))
        if tag == "h1":
            self.h1_count += 1
        if element_id := attributes.get("id"):
            self.ids.append(element_id)

        for name in ("href", "src", "poster"):
            value = attributes.get(name)
            if not value:
                continue
            if value.startswith("#"):
                self.fragment_links.append(value[1:])
            else:
                self.references.append((tag, name, value))

        for name in ("aria-controls", "aria-describedby", "aria-labelledby"):
            value = attributes.get(name)
            if value:
                for target in value.split():
                    self.aria_references.append((tag, name, target))


class LaunchSiteContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.html = (SITE / "index.html").read_text(encoding="utf-8")
        cls.parser = SiteParser()
        cls.parser.feed(cls.html)
        cls.parser.close()

    def test_html_has_one_primary_heading_and_unique_ids(self) -> None:
        self.assertEqual(self.parser.h1_count, 1)
        duplicates = [name for name, count in Counter(self.parser.ids).items() if count > 1]
        self.assertEqual(duplicates, [])

    def test_internal_fragments_and_aria_targets_exist(self) -> None:
        available = set(self.parser.ids)
        missing_fragments = sorted(set(self.parser.fragment_links) - available)
        missing_aria = sorted(
            {target for _, _, target in self.parser.aria_references if target not in available}
        )
        self.assertEqual(missing_fragments, [])
        self.assertEqual(missing_aria, [])

    def test_current_and_legacy_commercial_fragments_target_the_right_content(self) -> None:
        targets = {
            attributes["id"]: (tag, attributes)
            for tag, attributes in self.parser.tags
            if attributes.get("id") in {"editions", "pricing", "founding"}
        }

        self.assertEqual(set(targets), {"editions", "pricing", "founding"})
        self.assertEqual(targets["editions"][0], "section")
        self.assertIn("editions-section", (targets["editions"][1].get("class") or "").split())
        self.assertEqual(targets["pricing"][0], "div")
        self.assertIn("shell", (targets["pricing"][1].get("class") or "").split())
        self.assertEqual(targets["founding"][0], "article")
        self.assertIn("edition-supported", (targets["founding"][1].get("class") or "").split())

    def test_every_local_html_asset_exists_inside_docs(self) -> None:
        missing: list[str] = []
        escaped: list[str] = []
        site_root = SITE.resolve()

        for tag, attribute, reference in self.parser.references:
            parsed = urlsplit(reference)
            if parsed.scheme or parsed.netloc or not parsed.path:
                continue
            target = (SITE / unquote(parsed.path)).resolve()
            if target != site_root and site_root not in target.parents:
                escaped.append(f"{tag}[{attribute}]={reference}")
            elif not target.is_file():
                missing.append(f"{tag}[{attribute}]={reference}")

        self.assertEqual(escaped, [])
        self.assertEqual(missing, [])

    def test_video_has_controls_poster_and_english_captions(self) -> None:
        video = next(
            attributes
            for tag, attributes in self.parser.tags
            if tag == "video" and attributes.get("id") == "speak-demo"
        )
        self.assertIn("controls", video)
        self.assertEqual(video.get("poster"), "demo/speak-demo-poster.jpg")
        self.assertIn("playsinline", video)

        captions = [
            attributes
            for tag, attributes in self.parser.tags
            if tag == "track" and attributes.get("kind") == "captions"
        ]
        self.assertTrue(any(track.get("srclang") == "en" for track in captions))

        vtt = (SITE / "demo" / "speak-demo.vtt").read_text(encoding="utf-8")
        self.assertTrue(vtt.startswith("WEBVTT"))
        self.assertEqual(vtt.count(" --> "), 7)
        self.assertIn("00:54.000 --> 01:00.000", vtt)

    def test_required_metadata_and_structured_data_are_valid(self) -> None:
        self.assertRegex(self.html, r'<link rel="canonical" href="https://absentmindz\.github\.io/Speak/">')
        self.assertIn('property="og:image"', self.html)
        self.assertIn('name="twitter:card" content="summary_large_image"', self.html)

        theme_colors = [
            attributes
            for tag, attributes in self.parser.tags
            if tag == "meta" and attributes.get("name") == "theme-color"
        ]
        self.assertEqual(len(theme_colors), 2)
        self.assertEqual(
            {attributes.get("media") for attributes in theme_colors},
            {"(prefers-color-scheme: light)", "(prefers-color-scheme: dark)"},
        )
        self.assertTrue(all(attributes.get("content") == "#111214" for attributes in theme_colors))

        matches = re.findall(
            r'<script type="application/ld\+json">\s*(.*?)\s*</script>',
            self.html,
            flags=re.DOTALL,
        )
        self.assertEqual(len(matches), 1)
        structured_data = json.loads(matches[0])
        self.assertEqual(structured_data["@type"], "SoftwareApplication")
        self.assertEqual(structured_data["softwareVersion"], "0.5.2")
        offers = structured_data.get("offers", [])
        if isinstance(offers, dict):
            offers = [offers]
        self.assertTrue(offers)
        self.assertTrue(all(str(offer.get("price")) in {"0", "0.0"} for offer in offers))

    def test_styles_do_not_reference_the_retired_lime_palette(self) -> None:
        stylesheet = (SITE / "site.css").read_text(encoding="utf-8").lower()
        retired_tokens = ("--lime", "#b8f51a", "#c8ff3a", "#5f8000")
        found = [token for token in retired_tokens if token in stylesheet]
        self.assertEqual(found, [])

    def test_manifest_icons_and_search_files_exist(self) -> None:
        manifest = json.loads((SITE / "site.webmanifest").read_text(encoding="utf-8"))
        self.assertEqual(manifest["name"], "Speak for Windows")
        self.assertEqual(manifest["background_color"], "#111214")
        self.assertEqual(manifest["theme_color"], "#111214")
        for icon in manifest["icons"]:
            self.assertTrue((SITE / icon["src"]).is_file(), icon["src"])

        robots = (SITE / "robots.txt").read_text(encoding="utf-8")
        sitemap = (SITE / "sitemap.xml").read_text(encoding="utf-8")
        self.assertIn("https://absentmindz.github.io/Speak/sitemap.xml", robots)
        self.assertIn("https://absentmindz.github.io/Speak/", sitemap)


if __name__ == "__main__":
    unittest.main()
