"""Rebuild original-style Meld action artwork from editable SVG files (requires Inkscape)."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1] / "src" / "icons"
svg_dir = root / "svg"
svg_dir.mkdir(exist_ok=True)

broadcast = '<path d="M49 49a21 21 0 0 0 0 30m30-30a21 21 0 0 1 0 30M38 38a37 37 0 0 0 0 52m52-52a37 37 0 0 1 0 52"/>'
camera = '<rect x="27" y="43" width="60" height="43" rx="8"/><path d="m89 54 19-10v42L89 75z" fill="currentColor" stroke="none"/><circle cx="57" cy="64" r="9"/>'
mic = '<rect x="54" y="26" width="20" height="48" rx="10" fill="currentColor" stroke="none"/><path d="M44 56v9a20 20 0 0 0 40 0v-9M64 85v14M52 99h24"/>'
slash = '<path d="M34 96 96 34" stroke="#ECEFF6" stroke-width="8"/>'
artwork = {
    'stream': ('#7E899B', broadcast + '<circle cx="64" cy="64" r="7" fill="currentColor" stroke="none"/>'),
    'stream-start': ('#24B9A7', broadcast + '<circle cx="64" cy="64" r="7" fill="currentColor" stroke="none"/>'),
    'stream-stop': ('#ED5A71', broadcast + '<rect x="56" y="56" width="16" height="16" rx="2" fill="currentColor" stroke="none"/>'),
    'record': ('#7E899B', '<circle cx="64" cy="64" r="31"/><circle cx="64" cy="64" r="19" fill="currentColor" stroke="none"/>'),
    'record-start': ('#E84F6A', '<circle cx="64" cy="64" r="31"/><circle cx="64" cy="64" r="19" fill="currentColor" stroke="none"/>'),
    'record-stop': ('#E84F6A', '<circle cx="64" cy="64" r="31"/><rect x="49" y="49" width="30" height="30" rx="3" fill="currentColor" stroke="none"/>'),
    'mic': ('#7E899B', mic), 'mic-live': ('#D44F87', mic), 'mic-muted': ('#87506D', mic + slash),
    'camera': ('#7E899B', camera), 'camera-visible': ('#E99A50', camera), 'camera-hidden': ('#8B715C', camera + slash),
    'virtual-camera': ('#7E899B', camera + '<circle cx="104" cy="25" r="7" fill="#8B93A4" stroke="none"/>'),
    'virtual-camera-start': ('#5DA6DE', camera + '<circle cx="104" cy="25" r="7" fill="#2BD4B0" stroke="none"/>'),
    'virtual-camera-stop': ('#5DA6DE', camera + '<rect x="97" y="18" width="14" height="14" rx="2" fill="#ED5A71" stroke="none"/>'),
    'clip': ('#A880DF', '<circle cx="44" cy="83" r="9"/><circle cx="85" cy="83" r="9"/><path d="m49 76 39-38M79 76 39 38"/>'),
    'screenshot': ('#55B2D6', '<path d="M45 36H31v17m51-17h15v17M31 80v16h15m51-16v16H81"/><circle cx="64" cy="66" r="8"/>'),
}
for name, (color, shapes) in artwork.items():
    background = '<rect x="3" y="3" width="122" height="122" rx="25" fill="#10151F" stroke="#2B3547" stroke-width="2"/>'
    if name in ('camera', 'camera-visible', 'camera-hidden'):
        # Options+ embeds this source above its native caption. A complete
        # rounded tile here became a tiny button inside the real key.
        # Keep only a larger glyph with transparent padding on every side.
        background = ''
        if name == 'camera-hidden':
            shapes = camera + '<path d="M33 88 100 39" stroke="#ECEFF6" stroke-width="8"/>'
        shapes = '<g transform="translate(-16 -17) scale(1.24)">' + shapes + '</g>'
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128">
{background}
<g color="{color}" fill="none" stroke="currentColor" stroke-width="6" stroke-linecap="round" stroke-linejoin="round">{shapes}</g></svg>'''
    source = svg_dir / f'{name}.svg'
    source.write_text(svg + '\n')
    subprocess.run(['inkscape', str(source), '--export-filename=' + str(root / f'{name}.png'),
                    '--export-width=128', '--export-height=128'], check=True, capture_output=True)
print(f'Generated {len(artwork)} original-style action icons')
