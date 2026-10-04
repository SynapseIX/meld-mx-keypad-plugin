# Meld Studio for Logitech MX Creative Keypad — 0.4.16

By **SynapseIX** · [Project homepage](https://github.com/SynapseIX/meld-mx-keypad-plugin) · [Report a problem](https://github.com/SynapseIX/meld-mx-keypad-plugin/issues)

Control Meld Studio from your keypad: streaming, recording, microphone mute, camera visibility, virtual camera, scenes, clips, and screenshots.

**0.4.16 is the confirmed working camera baseline.** The maintainer confirmed the fix on a physical keypad after the automated regression checks passed. Toggle Camera has one picker, uses an icon without a text label, and follows the selected layer's visibility.

Independent community project; not affiliated with or endorsed by Meld Studio or Logitech.

- [For streamers: install and use](#for-streamers-install-and-use)
- [Troubleshooting](#troubleshooting)
- [For developers: set up and build](#for-developers-set-up-and-build)
- [Known issues and compatibility](#known-issues-and-compatibility)
- [License](#license)

## For streamers: install and use

You do not need to write code or install developer tools. Use the **`.lplug4` installer**. The source ZIP is for developers.

### What you need

- A Logitech **MX Creative Keypad**, connected to your Windows PC or Mac.
- [Logi Options+](https://www.logitech.com/software/logi-options-plus.html), which also installs Logi Plugin Service.
- [Meld Studio](https://meldstudio.co/) on the **same computer**, with your session open.
- The prebuilt **`MeldMxKeypad-0.4.16.lplug4`** file supplied with this release.

The reported working setup uses **Options+ 2.9.984725** and **Logi Plugin Service 6.4.2.3414**. The package declares Service 6.3 as its minimum; older versions have not received the same verification.

### 1. Connect Meld

1. Open Meld Studio and the session you want to use.
2. Open **Settings → Advanced** and enable **WebSocket Server**. This lets the keypad communicate with Meld. See [Meld's settings guide](https://meldstudio.co/docs/settings/#websocket-server).
3. Leave Meld running. The plugin connects automatically on the same computer; there is no address to enter in Options+.

Set up your stream destinations, recording options, clips, and screenshot save locations in Meld. The keypad buttons use those existing settings.

### 2. Install and assign buttons

1. Double-click **`MeldMxKeypad-0.4.16.lplug4`** and follow Logitech's installation prompt. Keep the extension; do not unzip the file.
2. Open Options+. In its settings, use **Restart Logi Plugin Service** so the new version loads. Reopen Options+ if needed.
3. Select your **MX Creative Keypad** and open the profile/page you want to customize.
4. Open **All Actions → Installed Plugins → Meld Studio** and drag an action onto a keypad button.
5. Click the assigned button to configure it. **Show Scene** and **Toggle Camera** each have a picker. The microphone uses the naming rule below.

Installing the prebuilt plugin does **not** require .NET, Python, Node.js, or a terminal. If double-clicking does nothing, open or update Options+ first so its plugin installer is available.

### 3. Use the actions

Each toggle is one action: press once to change its state, then press again to change it back. Toggle buttons use **icons only by default**. Their graphics follow the state reported by Meld, including supported changes made directly in Meld.

| Action | What pressing it does | What the button shows |
| --- | --- | --- |
| **Toggle Stream** | Starts or stops streaming using your Meld stream setup | Broadcast/start graphic when stopped; stop graphic while streaming |
| **Toggle Recording** | Starts or stops recording | Record circle when stopped; stop square while recording |
| **Toggle Microphone** | Mutes or unmutes the microphone track configured below | Live microphone or crossed-out microphone |
| **Toggle Camera** | Shows or hides the chosen camera layer in the scene currently shown | Orange camera when visible; crossed-out camera when hidden |
| **Toggle Virtual Camera** | Starts or stops Meld's virtual camera | State graphic when Meld supplies its status; otherwise neutral |
| **Show Scene** | Switches to the scene chosen in its picker | Scene icon and chosen scene name; highlighting depends on the host renderer |
| **Save Clip** | Saves a replay clip using Meld's clip settings | Scissors icon |
| **Screenshot** | Saves a screenshot using Meld's screenshot settings | Screenshot icon |

A neutral gray toggle icon means its state or target is unavailable. It does not necessarily mean the operation is stopped.

### Set up scene buttons

Drag **Meld Studio → Scenes → Show Scene** onto a button, open its settings, and choose a scene from **Meld scene**. Save/apply if Options+ prompts you. Add the same action to more buttons and choose a different scene for each, such as **Starting Soon**, **Main**, and **Be Right Back**.

The picker reads all scenes in your open Meld session; there is no fixed scene count. If you rename a scene, select it again. Use unique scene names where possible. If duplicate names are reordered, reselect the affected buttons.

### Set up the camera button

1. In Meld, show a scene containing your camera. This means the scene currently being shown, not just a scene highlighted for editing.
2. Drag **Meld Studio → Scenes → Toggle Camera** onto a button.
3. In **Camera / capture layer**, choose your camera layer by name and save the assignment.
4. Keep the default artwork. If you previously customized it, choose **Reset icon to default**. Allow about a second after saving a selection for its state to appear.
5. Press the button: the layer should hide and the camera graphic should become crossed out. Press again to show it and restore the orange camera. Changing visibility directly in Meld should update the same button.

**One button can follow the camera across scenes.** Give the camera layer the same name in each scene, such as `Main Camera`, and select it once. The action finds that name in whichever scene is currently shown. Each scene keeps its own layer visibility. Matching ignores capitalization and spaces at the beginning or end of a name. After a substantive rename, select the layer again.

An explicit selection can use any name. The optional **Automatic — Camera / Webcam** choice requires exactly one capture layer named `Camera` or `Webcam` in the current scene. Avoid duplicate camera names within a scene. If the target is missing or cannot be identified, the icon becomes gray and pressing it does nothing.

Hidden capture layers and layers inside groups are included. Screen/game captures can also appear because Meld does not reliably distinguish every capture source in its session data. Choose the actual camera layer. This action changes layer visibility; it does not disconnect or power off the physical camera.

### Set up the microphone button

1. In Meld's **audio mixer**, rename the audio track you want to control to **`Mic`** or **`Microphone`**. Rename the audio track, not just a visual layer.
2. Drag **Meld Studio → Audio → Toggle Microphone** onto a button.
3. Press it and check the track's mute state in Meld. The graphic changes between a live and a crossed-out microphone.

This version selects the microphone by its track name; there is no microphone picker. `mic`, `MIC`, and `Microphone` all work. A single matching global track takes priority. If there is no global match, the action uses a single matching track in the currently shown scene. Multiple matching tracks in the applicable scope leave the button gray. Rename extra matches so the intended microphone is unambiguous.

### Update from an older version

Install the new `.lplug4` file, then restart Logi Plugin Service and Options+.

**If upgrading from 0.4.15, remove its Toggle Camera assignment and add Toggle Camera again, then choose your layer.** That version saved the assignment in a different format. Resetting its icon alone does not convert it. If an older build shows a separate camera action for every layer, replace it with the single picker action in 0.4.16.

Keep other working assignments. Reset custom artwork if a button retains an old icon or label. You do not need to delete your keypad profiles.

## Troubleshooting

| What you see | What to do |
| --- | --- |
| Meld Studio is missing from All Actions | Confirm installation completed. Restart Logi Plugin Service from Options+ settings, then reopen Options+. |
| A picker says it is waiting for Meld | Open Meld on the same computer, enable **Settings → Advanced → WebSocket Server**, wait a few seconds, and reopen the picker. |
| No scenes appear | Open a Meld session containing scenes and let it finish loading. |
| No camera/capture layers appear | Show the intended scene in Meld and check that it contains a camera/capture layer. Reopen the picker. Identifiable image, browser, and media layers are excluded. |
| The camera icon is gray | Check that the selected layer exists in the currently shown scene. Use the same camera name across scenes, avoid duplicates, and save the selection. |
| Camera toggles but its graphic stays unchanged, or an old label remains | Confirm 0.4.16 is installed, restart the service, recreate a 0.4.15 camera assignment, and reset custom artwork to default. |
| The microphone is gray or does nothing | Rename the intended audio track to **Mic** or **Microphone** and remove duplicate matching names. |
| A scene button stops working after editing the session | Reselect the scene, especially after renaming it or reordering duplicate names. |
| Stream/recording does not start | Check Meld for missing settings, confirmation prompts, or errors. Icons change only after Meld reports the new state. |
| Virtual Camera stays gray | Some Meld versions do not report virtual-camera status. The toggle can still work; check the output in the receiving app. |
| Show Scene's background does not change | Some Options+ rendering paths omit the selected scene when requesting an image. The caption and scene-switch command still work. |

The plugin uses the fixed local address **`ws://127.0.0.1:13376`** and retries automatically. There is no setting for controlling Meld on a second computer.

If the problem remains, [open an issue](https://github.com/SynapseIX/meld-mx-keypad-plugin/issues) with your OS, plugin version, Options+ and Logi Plugin Service versions, the affected action, and steps to reproduce it. Include a screenshot of the button and its settings when useful.

## For developers: set up and build

The plugin is C# targeting **.NET 8**. Run the commands below from the project root: the folder containing this README, `src`, and `action-check`.

### 1. Install the prerequisites

| Tool | Needed for |
| --- | --- |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), including its runtime | Building the plugin, basic tests, and the pinned packaging tool |
| Git, or a downloaded source ZIP | Getting the source |
| LogiPluginTool **6.1.4.22672** | .NET 8-compatible Logitech SDK assemblies and packaging; installed below |
| Logi Options+, Meld, and a keypad on Windows/macOS | Testing the installer with real applications and hardware |
| .NET 10 SDK and Logi Plugin Service **6.4.2.3414** assemblies | Optional native-host rendering regression suite |

An editor such as VS Code, Rider, or Visual Studio is optional. Python and Node.js are not required for the normal build. Linux can run headless checks but is not a supported desktop host for the plugin.

Install the SDK for your system's architecture, open a new terminal, and check:

```sh
dotnet --list-sdks
dotnet --list-runtimes
```

The lists should include an **8.0.x SDK** and **Microsoft.NETCore.App 8.0.x**. Install .NET 10 alongside .NET 8 if you need the optional host suite. A runtime alone is not enough to build.

### 2. Get the source and pinned Logitech tool

```sh
git clone https://github.com/SynapseIX/meld-mx-keypad-plugin.git
cd meld-mx-keypad-plugin
dotnet tool install LogiPluginTool --tool-path .tools --version 6.1.4.22672
```

For the supplied source ZIP, extract it and open a terminal in **`MeldCameraPickerPrototype/MeldMxKeypadPlugin`**, then run the same tool-install command. If already installed there, check it with `dotnet tool list --tool-path .tools` instead.

The tool contains `PluginApi.dll`, `LoupedeckShared.dll`, and `SkiaSharp.dll` under `.tools/.store/logiplugintool/6.1.4.22672/logiplugintool/6.1.4.22672/tools/net8.0/any/`. Use this directory explicitly. Newer service assemblies can target .NET 10 and are not interchangeable with this plugin's .NET 8 build references.

### 3. Build and package

**macOS Terminal (zsh/bash), or Linux for a headless build:**

```sh
MELD_SDK_DIR="$(pwd)/.tools/.store/logiplugintool/6.1.4.22672/logiplugintool/6.1.4.22672/tools/net8.0/any/"
dotnet build src/MeldMxKeypadPlugin.csproj -c Release -p:SkipHostActions=true "-p:PluginApiDir=$MELD_SDK_DIR"
./.tools/logiplugintool pack bin/Release/ MeldMxKeypad-0.4.16.lplug4
./.tools/logiplugintool verify MeldMxKeypad-0.4.16.lplug4
```

**Windows PowerShell:**

```powershell
$meldSdkDir = (Resolve-Path ".tools/.store/logiplugintool/6.1.4.22672/logiplugintool/6.1.4.22672/tools/net8.0/any").Path.Replace('\', '/') + '/'
dotnet build src/MeldMxKeypadPlugin.csproj -c Release -p:SkipHostActions=true "-p:PluginApiDir=$meldSdkDir"
& .\.tools\logiplugintool.exe pack bin/Release/ MeldMxKeypad-0.4.16.lplug4
& .\.tools\logiplugintool.exe verify MeldMxKeypad-0.4.16.lplug4
```

Continue only after each command succeeds. Quote the whole `-p:PluginApiDir=...` argument for paths containing spaces. **Keep its trailing slash**: the project appends DLL filenames directly.

The DLL is **`bin/Release/bin/MeldMxKeypadPlugin.dll`**. Package **`bin/Release/`**, which contains both `bin` and `metadata`, rather than its inner `bin` folder. The installer is written to the project root. Install it using the streamer instructions above.

`SkipHostActions=true` builds without creating a development `.link` file or reloading an installed plugin. Rebuild, repack, install, and restart the service to try subsequent changes. For a new release, update the version in both `src/MeldMxKeypadPlugin.csproj` and `src/package/metadata/LoupedeckPackage.yaml`, and use a matching installer filename.

### 4. Run the basic tests

Close Meld first: the mock server needs local port **13376**. Run suites **one at a time** from the project root. Keep the SDK-path variable from step 3, or set it again in a new terminal.

**macOS/Linux:**

```sh
dotnet run --project protocol-check/protocol-check.csproj -c Release
dotnet run --project action-check/action-check.csproj -c Release -p:SkipHostActions=true "-p:PluginApiDir=$MELD_SDK_DIR" -- bin/Release test-results/sdk
```

**Windows PowerShell:**

```powershell
dotnet run --project protocol-check/protocol-check.csproj -c Release
dotnet run --project action-check/action-check.csproj -c Release -p:SkipHostActions=true "-p:PluginApiDir=$meldSdkDir" -- bin/Release test-results/sdk
```

The protocol check tests communication with a mock Meld server. The action suite checks commands, pickers, case-insensitive matching, state changes, and SDK rendering, writing images to **`test-results/sdk/`**. Linux restores its native Skia test dependency through NuGet; that dependency is not added to the plugin installer.

### 5. Optional native-host regression tests

This suite checks saved camera assignments and the native image paths that previously regressed. It is tied to **Logi Plugin Service 6.4.2.3414** and requires **.NET 10**. Build the plugin first using the .NET 8-compatible references above. Logitech service binaries are not included in the source archive.

Use your installed service's directory containing `PluginApi.dll`, `LoupedeckShared.dll`, `LoupedeckService.dll`, and `SkiaSharp.dll`. The commands show typical locations; adjust them if your installation differs.

**macOS:**

```sh
MELD_HOST_DIR="/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/"
MELD_EXPECTED_HOST_VERSION=6.4.2.3414 dotnet run --project host-render-check/host-render-check.csproj -c Release "-p:HostAssemblyDir=$MELD_HOST_DIR" -- bin/Release test-results/host
```

**Windows PowerShell:**

```powershell
$meldHostDir = 'C:/Program Files/Logi/LogiPluginService/'
$env:MELD_EXPECTED_HOST_VERSION = '6.4.2.3414'
dotnet run --project host-render-check/host-render-check.csproj -c Release "-p:HostAssemblyDir=$meldHostDir" -- bin/Release test-results/host
```

On Linux, provide your own authorized service assemblies and add `-p:SkiaSharpAssemblyPath=/path/to/linux-compatible/SkiaSharp.dll` if the service's Skia assembly is platform-specific. The .NET 8 SDK directory's compatible Skia assembly was used for the recorded Linux run.

Run this suite after the others finish, with Meld still closed. It uses an isolated temporary profile directory and a mock server; it does not read or edit your real profiles. See [TESTING.md](TESTING.md) for coverage and earlier failure reproductions. Automated checks do not replace trying a changed build in Options+, real Meld, and a physical keypad.

### Project map and common build problems

| Path | Purpose |
| --- | --- |
| `src/MeldClient.cs` | Meld connection, session data, and commands |
| `src/Actions/` | Actions, target matching, and saved camera selection lookup |
| `src/icons/` | Embedded button artwork |
| `src/package/metadata/` | Plugin identity, version, app icon, and default button template |
| `protocol-check/`, `action-check/`, `host-render-check/` | Test programs |
| `TESTING.md`, `docs/` | Regression notes and recorded evidence |

- **`dotnet` not found:** install the SDK and open a new terminal.
- **Missing `PluginApi.dll`:** check the pinned tool installation and `PluginApiDir`, including the trailing slash.
- **Assembly version mismatch:** build the plugin against the pinned tool's .NET 8 SDK. Reserve the 6.4.2 service assemblies for the host suite.
- **Wrong host version:** provide 6.4.2.3414 assemblies and keep the version guard enabled. Other versions need separate compatibility checks.
- **Port 13376 in use:** close Meld and other test processes, then run one suite at a time.
- **Skia cannot load:** use matching managed/native libraries for the test platform; on Linux, allow the native dependency to restore through NuGet.
- **`NETSDK1045` for the host suite:** install the .NET 10 SDK. The plugin and basic tests still target .NET 8.

The `.gitignore` excludes build outputs, local tools, SDK binaries, generated installers, and test runs. Keep source artwork, metadata, and curated `docs/` evidence in the repository. Do not commit proprietary Logitech binaries.

SDK references: [environment setup](https://logitech.github.io/actions-sdk-docs/csharp/plugin-development/introduction/), [packaging](https://logitech.github.io/actions-sdk-docs/csharp/plugin-development/distributing-the-plugin/), [profile action lists](https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/profile-actions/), and [multistate actions](https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/multistate-plugin-actions/).

## Known issues and compatibility

- **0.4.15 camera assignments need to be recreated** when upgrading. Custom icons can override dynamic artwork.
- **Virtual-camera state depends on Meld.** Without a supported status value, the toggle works but its icon stays neutral.
- **Show Scene highlighting depends on the Options+ renderer.** Some paths omit the selected scene when requesting an image, so the background can remain neutral while the caption and switching work.
- **Capture identification is limited by Meld's session data.** Screen/game captures may appear alongside webcams.
- **Camera selections must be saved and readable.** Unreadable, incomplete, or conflicting saved selections produce a neutral camera until resolved.
- **Compatibility is version-specific.** The maintainer confirmed the camera fix on the physical keypad with the reported Options+/service setup above. Automated runs use a mock Meld server and real Logitech managed assemblies; they do not certify every OS, Meld version, or later Options+ release.

### What changed in 0.4.16

Toggle Camera returns to Logitech's native multistate/list action, using the same rendering path as Toggle Microphone. Capture filtering, scene matching, the native `toggleLayer(sceneId, layerId)` call, post-press visibility polling, and camera artwork remain unchanged.

Logi 6.4.2 saves picker selections under generated button IDs. Its press path resolves those IDs, but its state/image path does not. `CameraProfileSelections` reads only this plugin's camera ID-to-selection mappings from local service profiles, then publishes confirmed visibility under both IDs. Saved selections refresh automatically. It **never modifies profiles** and does not guess visibility after a press.

Microphone muting uses Meld's native `toggleMute(trackId)`. Neither action simulates keyboard shortcuts. See [TESTING.md](TESTING.md) for the regression checks.

## License

This project's original code is licensed under the [MIT License](LICENSE). Copyright (c) 2026 Jorge Tapia (SynapseIX). Third-party names, logos, and SDKs remain subject to their respective owners' rights and terms.
