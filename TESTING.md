# Verification and known failure — 0.4.14

Updated on 2026-10-03. The 0.4.13 automated checks passed, but the user's physical-device test found that the camera icon still does not update after pressing the button. This remains unresolved in 0.4.14.

## Physical-device report

On **Options+ 2.9.984725 / Logi Plugin Service 6.4.2.3414**, the user confirmed that the camera button renders correctly with an icon and no label, and that pressing it changes the selected layer's visibility in Meld. The camera icon does **not** update to reflect that visibility. Synchronization after changes made directly in Meld is not confirmed working on the physical device. The automated checks below did not detect this integration failure.

## 0.4.14 scope

This release changes the author to **SynapseIX**, sets `homePageUrl` to `https://github.com/SynapseIX/meld-mx-keypad-plugin`, and documents the known issue. It does not change action code or artwork. Validation is limited to building, package verification, checking packaged metadata, and comparing implementation/artwork hashes against 0.4.13. The recorded automated results below belong to 0.4.13; they were not rerun for this documentation and metadata update.

## Rendering correction

Version 0.4.12 used an Action Editor command. Its parameter-aware saved-profile renderer passed, while the standalone renderer ignored the separate parameter dictionary and returned the neutral camera. Generating its default icon template reproduced a small gray camera with the **Toggle Camera** caption, consistent with the reported layout.

Version 0.4.13 uses the same `PluginMultistateDynamicCommand` API as Toggle Microphone. The selected camera travels as the native action parameter. A **list profile action** exposes one catalog action and a populated picker. Native state indices are synchronized from the selected current-scene layer's visibility.

The older renderer result is no longer dismissed as an irrelevant diagnostic. The current tests exercise the native standalone renderer and its subscription queue using selected camera parameters.

## Recorded automated results — 0.4.13

| Check | Recorded result |
| --- | --- |
| Plugin build, .NET 8 / SDK 6.1.4.22672 | Passed |
| SDK suite | 1,604 checks, 50 native renders; exit 0 |
| Native service 6.4.2.3414 suite | 245 checks; exit 0 |
| 0.4.12 comparison | Reproduced small-icon/caption template; failed native-camera regression gate |
| One camera catalog action with list-picker metadata | Passed |
| Current-scene capture candidates, including grouped and hidden layers | Passed |
| Active/inactive graphics match full-size icon-only artwork at 50/80/116 px | Passed |
| Explicit unknown/inactive/active previews | Passed |
| Native subscription receives independent camera frames | Passed |
| Native command queue toggles in both directions | Passed |
| Post-press polling updates native state when push data is withheld | Passed |
| Icon retains old state until Meld returns fresh visibility | Passed |
| External Meld changes trigger native state events and new subscribed frames | Passed |
| No-change reply retains state and polling stops | Passed |
| Scene changes, missing targets, disconnect and reconnect | Passed |
| Case-insensitive names/schema, capture exclusions, replacement IDs | Passed |
| Logitech package verifier and byte comparison to tested DLLs | Passed |
| Options+ frontend, physical keypad and real Meld | Unavailable in the test environment; user reports correct layout/toggle but failed icon refresh |

Counts include repeated assertions and asynchronous waits, not separate user workflows. A NuGet vulnerability-metadata fetch reported NU1900; cached dependencies were available and builds had no compilation errors.

## Scope

The tests use genuine Logitech managed assemblies, image decoding/rendering, command dispatch and the native image-subscription dispatcher. The final IPC output is replaced by a frame collector; Meld is a local WebChannel mock. No user profile or physical device is opened.

The host suite initializes the SDK's dynamic-action callbacks and exercises its load lifecycle. Presses use `PluginManager.ExecuteAction` with a native action string and no Action Editor dictionary, then pass through the real command queue. The suite checks native catalog list metadata and serialization of a selected action parameter.

These checks do not capture the Options+ frontend's actual request payload or prove that every frontend version persists the picker selection in that tested representation. The user's test on Options+ 2.9.984725 / service 6.4.2.3414 confirms that camera icon refresh remains broken despite these passing automated results.

The SDK suite retains the existing layer-name fixtures. Its adapter sends only the chosen Layer string to the new native list API. That is not an automatic migration of old Action Editor assignments: recreate the camera assignment when installing 0.4.13.

## Evidence

- [SDK output](docs/test-output.txt)
- [Native host output](docs/host-render-output.txt)
- [0.4.12 comparison output](docs/camera-ui-regression-output.txt)
- [0.4.12 generated caption layout](docs/camera-ui-regression-0412.png)
- [Active camera, native renderer](docs/host-camera-active-116.png)
- [Inactive camera, native renderer](docs/host-camera-inactive-116.png)

The 0.4.13 installer was checked byte-for-byte against both test runners' plugin DLLs. Version 0.4.14 retains that camera source and artwork, with a new build version and package metadata; its rebuilt DLL is not claimed to be identical to the 0.4.13 test-runner DLLs. Proprietary Logitech binaries and private host analysis are excluded from the source archive.
