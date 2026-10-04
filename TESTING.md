# Camera regression verification — 0.4.16

Updated 2026-10-03. Native host checks use **Logi Plugin Service 6.4.2.3414**. Actual Options+, Meld and the physical keypad are not available in the automated test environment. After the 0.4.16 installer was delivered, the maintainer confirmed that the camera fix works on the physical keypad; their reported setup is **Options+ 2.9.984725 / Service 6.4.2.3414**. This confirmation is separate from the automated results below.

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
| .NET 8 plugin build | Passed |
| Native legacy profile creation and serialization | Uses real generated aliases, not substituted layer keys |
| Initial state before image callbacks or presses | Matches each saved selection |
| Native standalone image subscription | Original renderer and invalidation queue; no profile-renderer substitution |
| Native saved-profile rendering | Compared independently with expected artwork |
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
| SDK suite | Other actions, picker population, mixed-case schema/names, groups and exclusions |
| Actual Options+ frontend, real Meld, physical keypad | Camera fix confirmed by maintainer after delivery; not exercised by the automated suite |

The host suite uses a temporary Applications directory populated by Logitech's native profile serializer. It does not scan real user profiles. Only the final IPC image transport is replaced with a frame collector; image generation and event matching use native host code.

Assertion counts include repeated pixel checks and asynchronous waits, not distinct workflows. NU1900 may appear when NuGet's vulnerability metadata endpoint is unavailable; dependencies are already cached.

## Reproduce

Use the build/test commands in [README.md](README.md). Run suites sequentially with Meld closed because the mock binds local port 13376.

For the original **0.4.14** installer, unpack it and set `MELD_REPRO_LEGACY_CAMERA=1`, add `-p:PluginAssemblyPath=/path/to/old-package/bin/MeldMxKeypadPlugin.dll`, and pass that package as the first program argument. The diagnostic succeeds only if a successful toggle leaves the saved alias unknown with an unchanged image.

For the original **0.4.15** installer, use `MELD_REPRO_EDITOR_CAMERA=1` with the same assembly/package overrides. It succeeds only when the modern saved state changes but the native standalone image with the selected editor parameters stays unchanged.

Keep `MELD_EXPECTED_HOST_VERSION=6.4.2.3414` for these runs. The suite checks that the loaded plugin DLL matches the supplied package.

## Evidence

- [SDK output](docs/test-output.txt)
- [Native host output](docs/host-render-output.txt)
- [0.4.14 alias regression](docs/camera-saved-profile-regression-0414.txt)
- [0.4.15 standalone-render regression](docs/camera-render-regression-0415.txt)
- [Visible camera](docs/host-camera-active-116.png)
- [Hidden camera](docs/host-camera-inactive-116.png)
- [Unavailable camera](docs/host-camera-unknown-116.png)
- [Package verification](docs/package-verification.txt)

Historical artifacts from earlier releases are not verification of this version. Proprietary Logitech binaries and decompiled code are not distributed.
