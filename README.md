# Meld Studio for Logitech MX Creative Keypad — 0.4.14

By **SynapseIX** · [Homepage](https://github.com/SynapseIX/meld-mx-keypad-plugin)

Native controls for Meld Studio: streaming, recording, microphone mute, camera-layer visibility, virtual camera, scenes, clips and screenshots.

## Known issues

**Toggle Camera's icon does not update after pressing the button**, even though the selected layer's visibility changes correctly in Meld. The camera picker and icon-only button layout work, but the displayed camera icon can remain stale. This was reported on **Logi Options+ 2.9.984725 / Logi Plugin Service 6.4.2.3414**.

Check the selected layer's visibility in Meld to confirm whether the camera is shown or hidden; do not rely on the button icon. Synchronization after changes made directly in Meld is not confirmed working on the physical device either. There is no confirmed workaround. **This issue remains unresolved in 0.4.14.**

Earlier automated rendering and state tests passed but did not catch this behavior in Options+ on the physical device. Those results do not establish that camera icon synchronization works in actual use.

## Install or update

1. Double-click **MeldMxKeypad-0.4.14.lplug4** and complete the Logitech installer prompt. Keep its extension; do not unzip it.
2. Restart **Logi Options+ and Logi Plugin Service** so the new plugin loads.
3. In Meld, enable **Settings → Advanced → WebSocket Server**. The plugin connects to `127.0.0.1:13376` on the same computer.
4. Add **Meld Studio → Scenes → Toggle Camera** and choose its **Camera / capture layer**. If upgrading from before 0.4.13, replace the old Toggle Camera assignment once because 0.4.13 changed its native action type. Existing 0.4.13 assignments can be kept when updating to 0.4.14.
5. Keep the action's default artwork. If you previously customized its icon, use **Reset icon to default** in the icon editor.

The installer needs no .NET SDK, Python or Node.js. The source ZIP contains the code, tests and this README; it is not the installer.

## Camera button

There is one **Toggle Camera** action with one camera picker. The picker lists capture-layer candidates from the scene currently shown in Meld, including hidden layers and layers inside groups.

The intended graphics are shown below. **The known issue above prevents reliable updates on the device.**

| Selected layer | Intended button graphic |
| --- | --- |
| Visible | Active orange camera |
| Hidden | Inactive crossed-out camera |
| Missing, ambiguous or disconnected | Neutral gray camera |

The button has no text caption. A press calls Meld's native `toggleLayer(sceneId, layerId)`. The implementation polls visibility afterward and listens for Meld session updates, but these mechanisms have not resolved the stale icon in actual device use.

For one selection to follow a camera across scenes, give that camera layer **the same name in each scene**. Matching ignores case and surrounding whitespace. An explicit selection can use any name; **Automatic — Camera / Webcam** requires exactly one matching capture candidate with either of those names. If the current scene has no matching layer, no toggle command is sent; the intended neutral icon is subject to the same synchronization issue.

Identifiable image, browser and media layers are excluded. Meld's session schema does not reliably distinguish every webcam from other capture sources, so the picker can also include screen/game captures.

## What changed in 0.4.14

- Plugin attribution is **by SynapseIX**.
- The **HOMEPAGE** button points to [github.com/SynapseIX/meld-mx-keypad-plugin](https://github.com/SynapseIX/meld-mx-keypad-plugin).
- The stale camera icon is documented as a known issue, and verification notes now include the physical-device report.
- This is a documentation and metadata update. Action behavior and artwork are unchanged from 0.4.13.

## Camera implementation in 0.4.13

The camera now uses **`PluginMultistateDynamicCommand`**, the same native state and image mechanism as Toggle Microphone. **`MakeProfileAction("list;Camera / capture layer")`** keeps it as a single action with a picker instead of expanding cameras into separate catalog actions.

The implementation derives native active/inactive state from the selected layer's visibility. Automated tests observe updated native notifications and frames, but the physical button still fails to refresh as described above. Current-scene layer filtering, name matching and the native toggle call retain their behavior.

The earlier Action Editor image callback passed saved-profile rendering tests, but the standalone rendering path used a neutral image and the generic caption. That gap is now covered by native standalone rendering and image-subscription tests.

## Other controls

- **Toggle Stream / Recording:** one button starts or stops the operation.
- **Toggle Microphone:** calls `toggleMute(trackId)`. Name one audio track **Mic** or **Microphone**, ignoring case and surrounding whitespace. A unique global match takes priority; otherwise a unique match in the current scene is used.
- **Show Scene:** choose one of Meld's available scenes. The button displays its name and scene icon.
- **Toggle Virtual Camera:** changes state through Meld. Its displayed state depends on Meld exposing a supported status property.
- **Save Clip / Screenshot:** invoke the corresponding Meld commands.

## Verification

The 0.4.13 implementation passed automated tests with the **6.4.2.3414 Logi Plugin Service managed assemblies** and a local Meld protocol mock. The SDK and native renderer/subscription suites exercise graphics, state notifications, presses, simulated Meld updates and scene switches.

**The user's physical-device test confirmed correct icon-only rendering and working visibility toggling, but the camera icon still does not update after a press.** Actual Options+, real Meld and the physical keypad were not available in the automated test environment. See [TESTING.md](TESTING.md) for the distinction between those test results and the known issue.

Version 0.4.14 changes only documentation and package/version metadata. Its build and package were verified; no camera-state fix is claimed.

## Build and package

Use the .NET 8 SDK and Logitech's C# SDK assemblies:

```sh
dotnet build src/MeldMxKeypadPlugin.csproj -c Release -p:SkipHostActions=true -p:PluginApiDir=/path/to/Logi/assemblies/
dotnet run --project action-check/action-check.csproj -c Release -p:SkipHostActions=true -p:PluginApiDir=/path/to/Logi/assemblies/ -- bin/Release test-results/sdk
```

The plugin was built against .NET 8-compatible assemblies bundled with **LogiPluginTool 6.1.4.22672**. Include the trailing slash in `PluginApiDir`.

```sh
dotnet tool install --global LogiPluginTool --version 6.1.4.22672
logiplugintool pack bin/Release/ MeldMxKeypad-0.4.14.lplug4
logiplugintool verify MeldMxKeypad-0.4.14.lplug4
```

The host suite requires .NET 10 and locally installed **6.4.2.3414** service assemblies:

```sh
MELD_EXPECTED_HOST_VERSION=6.4.2.3414 dotnet run --project host-render-check/host-render-check.csproj -c Release -p:HostAssemblyDir=/path/to/Logi/assemblies/ -- bin/Release test-results/host
```

On Linux, pass `-p:SkiaSharpAssemblyPath=/path/to/linux-compatible/SkiaSharp.dll` if the installed service bundles a platform-specific Skia assembly. Both suites bind local port **13376**; run them sequentially on an isolated machine with Meld closed.

Logitech binaries and decompiled host code are not included.

SDK references: [profile action lists](https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/profile-actions/) and [native multistate actions](https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/multistate-plugin-actions/).
