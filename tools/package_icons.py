"""Publish source PNGs under the filenames resolved by Logitech's native SDK.

For a state without a parameter, CombinedString uses SIX underscores before
the state name: action + '___' + empty parameter + '___' + state.
Run after generate_state_icons.py when changing artwork. No extra dependencies.
"""
from pathlib import Path
import shutil

root = Path(__file__).resolve().parents[1] / "src"
destination = root / "package" / "actionicons"
destination.mkdir(exist_ok=True)

toggles = {
    "ToggleStreamCommand": ["stream", "stream-start", "stream-stop"],
    "ToggleRecordingCommand": ["record", "record-start", "record-stop"],
    "ToggleMicrophoneCommand": ["mic", "mic-muted", "mic-live"],
    "ToggleCameraCommand": ["camera", "camera-hidden", "camera-visible"],
    "ToggleVirtualCameraCommand": ["virtual-camera", "virtual-camera-start", "virtual-camera-stop"],
}
prefix = "Loupedeck.MeldStudioControlsPlugin."
for action, icons in toggles.items():
    shutil.copyfile(root / "icons" / f"{icons[0]}.png", destination / f"{prefix}{action}.png")
    for state, icon in zip(["unknown", "inactive", "active"], icons):
        shutil.copyfile(root / "icons" / f"{icon}.png", destination / f"{prefix}{action}______{state}.png")

for action, icon in {
    prefix + "SaveClipCommand": "clip",
    prefix + "ScreenshotCommand": "screenshot",
    # ActionEditorCommand explicitly declares Name=ShowScene. Keep the class
    # aliases for host versions that use the fully qualified class name.
    "ShowScene": "scene",
    prefix + "ShowScene": "scene",
    prefix + "ShowSceneCommand": "scene",
}.items():
    shutil.copyfile(root / "icons" / f"{icon}.png", destination / f"{action}.png")

symbol_dir = root / "package" / "actionsymbols"
shutil.copyfile(symbol_dir / f"{prefix}ShowSceneCommand.svg", symbol_dir / "ShowScene.svg")

# IMPORTANT: an ACTION template is rendered as-is by Logi Plugin Service. It
# bypasses both actionicons and GetCommandImage. Only the PLUGIN DEFAULT template
# is composed with a runtime image. Empty per-action image slots render black.
# All actions now use runtime artwork. ShowScene draws its selected background,
# glyph and caption into one bitmap; an action template would override it.
templates = root / "package" / "icontemplates"
for action in [*toggles, "SaveClipCommand", "ScreenshotCommand"]:
    (templates / f"{prefix}{action}.ict").unlink(missing_ok=True)

for path in templates.glob("*ShowScene*.ict"):
    path.unlink()
print(f"Packaged {len(list(destination.glob('*.png')))} native action/state images")
