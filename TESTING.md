# Meld Studio Controls verification — 0.4.18

Updated 2026-10-23. Native host checks use **Logi Plugin Service 6.4.2.3414**. Actual Options+, Meld and the physical keypad are not available in the automated test environment. After the 0.4.16 installer was delivered, the maintainer confirmed that the camera fix works on the physical keypad; their reported setup is **Options+ 2.9.984725 / Service 6.4.2.3414**. This confirmation is separate from the automated results below.

## Scene color update in 0.4.18

The selected title/background color pair has a calculated contrast ratio of **9.65:1**.

Only the configured Show Scene image palette changes: current scene `#C4E5F5` / title `#17354A`; other scenes remain `#111823` / white. The unconfigured transparent symbol, icon spacing, title sizing, action identity, scene selection, event callbacks, camera logic, and other commands are unchanged. Existing 0.4.17 assignments remain valid.

The existing SDK/native host scene-render assertions now check dark captions on the light background and white captions on inactive buttons. Glyph detection is restricted to purple so the light blue fill is not mistaken for the icon. The native saved-profile renderer covers simulated scene changes and both short/long labels. The separate unconfigured editor renderer still has no selected-scene context; this change does not resolve that pre-existing host limitation.

## Rename in 0.4.17

The package ID is now `MeldStudioControls`. Namespace, class and assembly names, action asset names, the camera saved-selection filter, and test references use that identity consistently. Camera command and rendering logic are unchanged from 0.4.16. The maintainer's physical-device confirmation applies to 0.4.16; the renamed package requires a new install and button assignments.

## Failures reproduced

### Saved aliases in 0.4.14

`ApplicationAction.CreateLegacyProfileAction` saves the selected layer key in `LegacyProfileActionParameter`, but addresses the button using a generated alias. The host resolves that alias for a press, but its native multistate lookup queries the alias directly. The original camera stored state only against the layer key.

The original 0.4.14 installer reproduces:

```text
BEFORE: raw layer state=active, saved-button state=unknown
AFTER: raw layer state=inactive, saved-button state=unknown, image changed=False
```

Earlier tests used a direct layer-key `ApplicationAction`, bypassing native list-profile creation. That test fixture missed this failure.

### Rendering paths in 0.4.15

The modern `MultistateActionEditorCommand` fixed the saved-profile state lookup, but the native standalone image renderer does not forward its Action Editor parameter dictionary to the image callback. Its image remains unchanged when the selected camera toggles. It also lacks the legacy multistate icon route used by the microphone.

The 0.4.15 subscription test explicitly replaced the native standalone renderer with the saved-profile renderer. Both were genuine host APIs, but that substitution concealed the unsupported path. It has been removed.

The configuration handler also generates editable image-and-text defaults. This happens for the working microphone too: seeing a caption there alone is not proof that the live device will use it. Live rendering and editable previews must be tested separately.

## Fix

The camera again uses `PluginMultistateDynamicCommand` and one native list picker. `CameraProfileSelections` reads the service's local `Applications/.../Profiles/.../ProfileInfo.json` files to obtain **only Meld camera alias-to-selection mappings**. It checks file metadata every 500 ms, parses changed files, and never writes a profile. State and images use the same confirmed Meld visibility for the raw picker value and every saved alias.

The compatibility component handles new/edited selections, plugin reload, atomic/partial saves, deletion and conflicting copied aliases. Missing or ambiguous mappings stay unknown. No last-pressed-camera fallback or optimistic state flip is used.

Capture filtering, case-insensitive scene matching, native `toggleLayer` arguments, bounded post-press polling and all camera PNG assets are unchanged.

## Automated coverage

| Check | Result |
| --- | --- |
| .NET 8 target build (using SDK 10.0.401) | Passed; 0 warnings, 0 errors |
| Native legacy profile creation and serialization | Uses real generated aliases, not substituted layer keys |
| Initial state before image callbacks or presses | Matches each saved selection |
| Native standalone image subscription | Original renderer and invalidation queue; no profile-renderer substitution |
| Native saved-profile rendering | Compared independently with expected artwork; selected baby blue background/dark title, inactive dark background/white title |
| Camera graphics at 50/80/116 px | Full-size pixels, no captions, distinct visible/hidden/unavailable states |
| Press twice through native command queue | Correct native layer IDs and state in both directions |
| Delayed post-press polling reply | Icon retains confirmed state until Meld replies |
| Direct simulated Meld visibility changes | State and subscribed image frames update |
| Two different selected cameras | Independent states and images |
| Scene changes, missing layers, disconnection/reconnection | Correct selection and unavailable state |
| No-change toggle response | No guessed state; polling stops |
| Saved picker edits, plugin reload, profile replacement/deletion | Updates without a new press or Meld event |
| Incomplete/invalid JSON and conflicting copied aliases | Neutral state, then recovery after correction |
| Profile content | Plugin does not modify it |
| SDK suite | Passed: 1,612 checks and 50 renders; actions, picker population, mixed-case schema/names, groups and exclusions |
| Native host suite | Passed: 316 checks against Service 6.4.2.3414 |
| Actual Options+ frontend, real Meld, physical keypad | 0.4.16 camera fix confirmed by maintainer; 0.4.18 scene palette not yet tested on hardware |

The host suite uses a temporary Applications directory populated by Logitech's native profile serializer. It does not scan real user profiles. Only the final IPC image transport is replaced with a frame collector; image generation and event matching use native host code.

Assertion counts include repeated pixel checks and asynchronous waits, not distinct workflows. NU1900 may appear when NuGet's vulnerability metadata endpoint is unavailable; dependencies are already cached.

## Reproduce

Use the build/test commands in [README.md](README.md). Run suites sequentially with Meld closed because the mock binds local port 13376. The historical reproduction instructions below require the original 0.4.16 source/harness, which retains the old plugin identity and assembly names.

For the original **0.4.14** installer, unpack it and set `MELD_REPRO_LEGACY_CAMERA=1`, add `-p:PluginAssemblyPath=/path/to/old-package/bin/MeldMxKeypadPlugin.dll`, and pass that package as the first program argument. The diagnostic succeeds only if a successful toggle leaves the saved alias unknown with an unchanged image.

For the original **0.4.15** installer, use `MELD_REPRO_EDITOR_CAMERA=1` with the same assembly/package overrides. It succeeds only when the modern saved state changes but the native standalone image with the selected editor parameters stays unchanged.

Keep `MELD_EXPECTED_HOST_VERSION=6.4.2.3414` for these runs. The suite checks that the loaded plugin DLL matches the supplied package.

## Evidence

- [Selected scene render](docs/scene-selected-116.png)
- [Inactive scene render](docs/scene-inactive-116.png)
- [Selected long scene title](docs/scene-selected-long-80.png)
- [SDK output](docs/test-output.txt)
- [Native host output](docs/host-render-output.txt)
- [0.4.14 alias regression](docs/camera-saved-profile-regression-0414.txt)
- [0.4.15 standalone-render regression](docs/camera-render-regression-0415.txt)
- [Visible camera](docs/host-camera-active-116.png)
- [Hidden camera](docs/host-camera-inactive-116.png)
- [Unavailable camera](docs/host-camera-unknown-116.png)
- [Package verification](docs/package-verification.txt)

Historical artifacts from earlier releases are not verification of this version. Proprietary Logitech binaries and decompiled code are not distributed.
