"""Makes the fonts the DU and the status page use from Inter's variable font (#81).

    python firmware/tools/make_fonts.py <Inter[opsz,wght].ttf>

Inter (SIL Open Font License 1.1, https://github.com/rsms/inter) is taken from Google's font repository:
https://github.com/google/fonts/tree/main/ofl/inter. The DU's font renderer (stb_truetype) cannot pick an instance of a
variable font, so the three weights it draws are cut out of it as static fonts, each at the optical size it is used
at, and trimmed to the characters a DU screen can show (Latin-1 plus a few punctuation marks): about 60 KB each.
The status page gets the variable font itself, trimmed the same way, as WOFF2.

Needs: pip install fonttools brotli
"""
import io
import sys
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

ROOT = Path(__file__).resolve().parents[2]
DU_FONTS = ROOT / "firmware" / "main" / "fonts"
WEB_FONTS = ROOT / "web" / "fonts"

# Latin-1, plus en/em dash, curly quotes, bullet, ellipsis, arrow and minus.
UNICODES = list(range(0x20, 0x7F)) + list(range(0xA0, 0x100)) + [0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D,
                                                                   0x2022, 0x2026, 0x2192, 0x2212]

# (file, weight, optical size): regular for running text (20-28 px), semibold for titles and labels, bold for the
# display name and the identify label (72-200 px).
DU_WEIGHTS = [("inter-regular.ttf", 400, 22), ("inter-semibold.ttf", 600, 28), ("inter-bold.ttf", 700, 32)]


def trimmed(font: TTFont, flavor: str | None = None) -> bytes:
    options = subset.Options()
    options.layout_features = ["kern", "tnum"]     # stb_truetype reads kerning pairs; tnum for the status page
    options.hinting = False                        # neither the DU nor a browser at these sizes needs it
    options.name_IDs = ["*"]                       # keep the copyright and licence names
    options.flavor = flavor
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=UNICODES)
    subsetter.subset(font)
    out = io.BytesIO()
    font.flavor = flavor
    font.save(out)
    return out.getvalue()


def main() -> None:
    source = Path(sys.argv[1])
    DU_FONTS.mkdir(parents=True, exist_ok=True)
    WEB_FONTS.mkdir(parents=True, exist_ok=True)
    for name, weight, opsz in DU_WEIGHTS:
        static = instancer.instantiateVariableFont(TTFont(source), {"wght": weight, "opsz": opsz})
        data = trimmed(static)
        (DU_FONTS / name).write_bytes(data)
        print(f"{name}: {len(data) // 1024} KB")
    data = trimmed(TTFont(source), flavor="woff2")
    (WEB_FONTS / "inter.woff2").write_bytes(data)
    print(f"inter.woff2: {len(data) // 1024} KB")


if __name__ == "__main__":
    main()
