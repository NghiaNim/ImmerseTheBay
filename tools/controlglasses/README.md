# Local S25 display compatibility experiment

This is a local development patch for authorized ControlGlasses and Unity APKs,
not an official release or a redistributed APK. Vendor binaries, decoded source,
preferences and signing keys must remain outside version control.

The inspected official 3.1.0.20251118115716 APK has SHA-256:

`49c9484ebd34e495984b219bb73988a366c03339f7251a9fe9a4702f58f5c76e`

On the tested Samsung S25 / Android 16, Android reports the external display as
`Air 2 Ultra`, while `DisplayModel._isHdmi()` accepts names containing `HDMI`.
This causes `hasHdmi=false` and `sdkFailed` before Unity starts XR.

`patch_display.py` adds one exact-name exception for `Air 2 Ultra` on a
non-primary display. It preserves the existing HDMI/Beam handling and the
subsequent vendor/product checks. It does not change signature validation,
licensing, permissions, firmware, native code or ELF alignment.

## Preparation and verification

Use Apktool 3.0.3 from its official release with SHA-256
`dbf930b076c6b9be08d57c449cacefc3bdd6b71ebd59b3066fc0e1f5b14f9423`.
Java 21 and Android build-tools 35.0.1 were used locally.

1. Preserve the original APK. Back up app preferences and permission state.
   The tested official APK is debuggable, so `run-as` can read its preferences
   without root. Preserve `OTA_SKIP=true`; do not trigger a firmware update.
2. Decode with `apktool d --no-res -o DECODED ORIGINAL.apk`.
3. Run `python3 tools/controlglasses/patch_display.py DECODED`.
4. Build with `apktool b DECODED -o REBUILT.apk`.
5. Use `python3 tools/controlglasses/repack.py ORIGINAL.apk REBUILT.apk
   classes3.dex UNSIGNED.apk --source-sha256 ORIGINAL_SHA256` (Python 3.11+).
   This replaces only the selected DEX and removes old JAR signature metadata,
   preserving the remaining entry bytes and compression methods. Use
   `classes.dex` for the tested Unity APK. Do not distribute the full rebuild.
6. Run `zipalign -P 16 -f 4 UNSIGNED.apk ALIGNED.apk`, then `apksigner sign`
   with a local development key. Verify with `apksigner verify --verbose` and
   `zipalign -c -P 16 4 SIGNED.apk`.
7. Re-decode the signed APK. Compare all classes against the original except the
   targeted method. Check the Android Manifest, resources and all native-library
   bytes are unchanged. Re-signing also changes `META-INF/MANIFEST.MF`; that is
   signature metadata, not the Android Manifest.

The prepared local APK is ignored at
`Builds/controlglasses-patch/ControlGlasses-local-air2ultra.apk`, SHA-256:
`8e1f7e824fee7455ee55ac4eba06d5581cd643e313cee3799e9ffa2091b8331b`.
Static verification passed: only `_isHdmi()` changed; all 15 native libraries,
Android Manifest and resources retained their original bytes. On-device
ControlGlasses validation passed: green `SDK ready`, `hasHdmi=true`, and
`sdkSuccess`. Unity subsequently discovered the ControlGlasses runtime and
started its IMU, camera and perception services.

## Apply the same fix to the Unity APK

The Unity APK embeds a second copy of `DisplayModel`. Patching ControlGlasses
alone leaves Unity reporting `isHdmi=false` and `NativeDisplay Start 12`.
Repeat the procedure above with the Unity APK and replace **classes.dex**.
The patch script supports the two register layouts inspected in these builds;
it rejects other layouts and repeated application.

Sign the Unity result using its existing development key. Verify that its
certificate matches the installed app before using `adb install -r`; this
preserves app data. Do not replace the original build artifact.

Tested original Unity SHA-256:
`0b284861d6c99fc479b83c2f2efe39f261410d0b0120f12c341b275ae5d6e50b`.
Prepared result: `Builds/controlglasses-patch/ImmerseTheBay-local-air2ultra.apk`,
SHA-256 `3749dfdc42b10fe50784f92b38cf26d004e20ad5f4ac02cb90093d141bad1946`.
Signature verification and ZIP alignment passed. The final APK changes only
`classes.dex`; Android Manifest, native libraries and resources are unchanged.
All 3,030 decoded classes were compared. Outside the patched method, seven
classes only omit explicit static `false`/`0L` initializers, which retain their
default zero values; no other method changes were found.

On Samsung, use Settings → Samsung DeX → Connected display → **Mirrored**.
Changing only `dex_on_external_display` through ADB did not survive reconnection.
The actual Mirrored setting restored the glasses to 3840×1080 at 60 Hz in this
test. Launch ImmerseTheBay through ControlGlasses after its SDK is ready.

Each fresh Unity build embeds the original vendor class again. Reapply and
verify this local patch after rebuilding until the upstream SDK supports the
device's display naming. This does not establish general Android 16 support.

## Device replacement and rollback

The local signer differs from the official signer, so Android will not accept
an in-place update. Replacement requires a normal uninstall/install. Restore
the backed-up preferences **before first launch**, then restore only previously
authorized permissions and confirm USB access. Never bypass signature checks.

To roll back, uninstall the local build, reinstall the preserved official APK,
restore preferences before launching it, and reauthorize required permissions.
Both packages retain their existing 4KB ELF library alignment; passing ZIP
alignment does not establish support for 16KB-page devices.
