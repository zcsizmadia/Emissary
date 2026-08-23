# Brand assets

| File | Use |
|---|---|
| `emissary-mark.svg` | The mark alone — README header, favicon, anywhere square |
| `emissary-logo.svg` | Horizontal lockup: mark, wordmark, tagline |
| `icon.png` | 128×128 raster, packed into every NuGet package (`PackageIcon`) |

## The mark

A **seal**. An emissary is sent carrying authority, and a sealed dispatch is one whose provenance
you can check — which is the whole argument of the library: agents you can put in front of an
auditor.

Inside it, an **E** — stem and three arms — in a single colour. The Claude-native accent is the
turned-back corner of a letter, in Anthropic clay, sitting off the letterform.

| Colour | Hex | Role |
|---|---|---|
| Violet | `#6E3AF0` → `#3D1B9E` | The seal, a diagonal gradient. Anchored on .NET's `#512BD4`. |
| Clay | `#D97757` | The turned corner — the Claude-native accent. Used once, deliberately. |
| White | `#FFFFFF` | The letterform, all of it. |
| Wordmark violet | `#7C3AED` | Legible on both a white and a dark background, so one lockup serves both GitHub themes. |

**The letterform stays one colour.** The first version of this mark put the clay on the bottom arm
of the E, and it read as an **F**: the eye groups by colour before it groups by shape, so the white
shapes became the letter and the clay bar became an underline. Any future accent goes on the seal,
not on the glyph.

Space around the mark: at least the height of one arm. It is designed to hold together down to
16×16, which is why the arms are solid rather than tonal and why nothing crosses the seal's edge.
Check any change by rendering it at 32×32 and reading it, not by looking at the 512 grid.

## Regenerating `icon.png`

The SVG is the source of truth; the PNG exists because NuGet does not accept SVG. Re-render it from
the mark whenever the mark changes, with whatever rasterizer is at hand:

```bash
inkscape -w 128 -h 128 assets/emissary-mark.svg -o assets/icon.png
# or
magick -background none -density 384 assets/emissary-mark.svg -resize 128x128 assets/icon.png
```

Keep it 128×128 with a transparent background — that is what NuGet renders, and the packages are
validated against it on every `dotnet pack`.
