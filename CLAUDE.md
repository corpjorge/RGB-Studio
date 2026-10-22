# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

An unpacked vendor SDK drop from GIGABYTE — **RGB Fusion SDK, build `B19.0311.1`** (SDK guide v1.1.6, dated 2019-03-11). It is not an application project. It contains prebuilt 32-bit control DLLs, three MFC demo apps that exercise them, and two PDF reference manuals. There is no build system at the root, no test suite, and no version control.

Everything below `B19.0311.1/` is vendor-supplied. Treat the DLLs and the PDFs as read-only reference material; new work should be *consumers* of these DLLs, kept outside the vendor tree.

```
B19.0311.1/
  RGB Fusion Software Development Kit Guide.pdf   authoritative docs for GLedApi + GvLedLib
  AORUS DDR Software Development Kit.pdf          authoritative docs for LEDCtrl
  DLLs/            deployable runtime binaries (all x86) — this is the canonical copy
  Sample/          three independent MFC demos, one per device family
```

Note the directory is misspelled `Sample/Motherbaord/` (not "Motherboard"); the `DLLs/Motherboard/` spelling is correct.

## Three independent API families

There is no unified entry point. Each device family is a separate DLL with its own conventions, its own init call, and its own error-code scheme. They do not share types.

| Family | DLL | Header / binding | Sample | Init |
|---|---|---|---|---|
| Motherboard zones | `GLedApi.dll` | none — `LoadLibrary` + `GetProcAddress` | `Sample/Motherbaord/GLedApi/` | `dllexp_InitAPI()` |
| VGA & peripherals | `GvLedLib.dll` | `GvLedLib.h` + `GvLedLib.lib` | `Sample/Peripherals/LedDemo/` | `GvLedInitial()` / `GvLedInit()` |
| AORUS DDR memory | `LEDCtrl.dll` | `LEDCtrl.h` + `LEDCtrl.lib` | `Sample/AORUS_DDR/` | `dllexp_LibInitial()` |

`GLedApi` returns Win32 codes (`ERROR_SUCCESS` 0x0, `ERROR_INVALID_OPERATION` 0x10DD, `ERROR_NOT_SUPPORTED` 0x32, `ERROR_INSUFFICIENT_BUFFER` 0x7A). `GvLedLib` returns its own `GV_LED_API_*` codes (0 OK, 1 fail, 2 device unavailable, 3 param error).

### Motherboard control flow (`GLedApi.dll`)

The whole API is a staged write-then-commit against the IT8295 MCU. The sequence is fixed:

1. `dllexp_InitAPI()` — required before anything except `dllexp_GetSdkVersion`.
2. `dllexp_GetMaxDivision()` → `iMaxDivs`, the number of addressable LED zones (max 8 or 9, FW-dependent). Returns `-1` when there are no RGB zones — that is the signal to fall through to the monochrome branch, not an error.
3. `dllexp_GetLedLayout(BYTE[iMaxDivs], iMaxDivs)` — per-zone type: `0` NA, `1` A_LED (analog), `2` D_LED_TYPE1 (audio-shield digital strip), `3` D_LED_TYPE2 (user digital strip on the D_LED header).
4. Fill a `LedSettingData[iMaxDivs]` array — you must always send the *full* array sized to `iMaxDivs`, even to change one zone.
5. `dllexp_SetLedData((PBYTE)array, iMaxDivs * sizeof(LedSettingData))` — writes to the MCU but does **not** take effect.
6. `dllexp_Apply(iApplyBit)` — commits. `iApplyBit` is a zone bitmask (bit 0 = division 0, …), or `-1` for all.

`LedSettingData` (`Sample/Motherbaord/GLedApi/testGLedApi/testGLedApiDlg.h:49`) is the struct the PDF calls `LEDSETTING`. Its fields happen to be naturally aligned at 16 bytes with no packing pragma — if you redeclare it for interop, assert `sizeof == 16`, because `dllexp_SetLedData` takes a raw byte count and silently misinterprets a padded layout. `dwColor` is `0xWWRRGGBB` — `WW` is a separate white channel, only meaningful on external strips. There is no "off" mode: turn LEDs off with `Mode_Sel = 4` (Static) and `dwColor = 0`.

Mode values: `0` Null, `1` Pulse, `2` Music, `3` Color Cycle, `4` Static, `5` Flash, `8` Transition, `10`–`18` Digital Modes A–I. Digital modes are strip-only, and the two digital strip types support different subsets. The meaning of `wTime_base0..2`, `CtrlVal0` and `CtrlVal1` changes per mode — section 3.2 of the SDK guide PDF has the per-mode tables and is the only complete source.

Two behaviours branch off this main flow:

- **Music mode** (`Mode_Sel = 2`) is software-driven. The MCU goes passive and all LEDs go dark; the host must do its own beat detection and drive `dllexp_BeatInput(0|1)` on each beat.
- **Monochrome boards** take a different path entirely: `dllexp_MonocLedCtrlSupport()` returns a mode bitmask (1 static, 2 pulse, 4 music, 0 unsupported), then `dllexp_GetRGBPinType()` selects which pin-control function to call, then `dllexp_SetMonocLedMode(mode)` plus `dllexp_RGBPin_Type1(pin1, pin2, pin3)` with 0/1 per pin. `LedSettingData` is not used here.

**RGB strip calibration** (`dllexp_RGBCalibration_Step1/2/3` → `dllexp_GetCalibrationValue(step, colorSeen)` → `dllexp_RGBCalibration_Done`) exists because third-party strips wire R/G/B to the header pins in arbitrary order. Each step drives one primary and asks the *user* which colour they actually see. It is inherently interactive — there is no way to automate it. Per the PDF, strips only appear at division index ≥ 4.

**BIOS profile persistence** (`dllexp_GetEzLedModeProfileSupport`, `dllexp_SetLedModeToBios`, `dllexp_SetLedColorValueToBios`, `dllexp_SaveToBios`, …) is a parallel API that stores a fallback effect in BIOS so lighting survives reboot before the host software loads. It uses **a different, smaller mode enum** than `LedSettingData.Mode_Sel` — the sample maps between them explicitly in `OnBnClickedBtnMbset` (`testGLedApiDlg.cpp:364`): 0→0 off, 1→1 pulse, 3→2 cycle, 4→3 static, 5→4 flash. This whole group is **undocumented in the PDF**; the sample is the only reference.

### Peripherals (`GvLedLib.dll`)

Two unrelated discovery mechanisms coexist:

- `GvLedInitial(int& count, int* idArray)` fills an array of device-type IDs (`DEVICE_*` in `GvLedLib.h`), then calls take that ID, with `-1` meaning all devices.
- `GvLedInit(VID, PID, int& index)` probes a specific USB VID/PID (all Gigabyte devices are VID `0x1044`) and returns an opaque index. `LedDemoDlg.cpp:143` uses this path with a hardcoded PID table, and pairs it with `GvLedIsConnect(index)` for hot-plug polling on a 1s timer.

Generic effects go through `GvLedSet(index, GVLED_CFG)`. Device classes that don't fit that struct get their own families — `GvLedXSet/XGet/XSave` (cases, headsets), `GvLedKSet/KSetSingle/KGetDef` (XK700 keyboard, including a 107-key per-key mask), `GvLedMSet/MGet/MSave` (XM300 mouse), `GvLedAM300Set/Get/Save`. The `*Save` variants persist to device flash; without them settings revert.

### AORUS DDR (`LEDCtrl.dll`)

The smallest API: `dllexp_LibInitial()` → `dllexp_GetSuportDevice(DWORD&)` (bit 0x1 = DDR present) → `dllexp_GetSuportPattern(dev, DWORD&)` → `dllexp_SetEffect(dev, mode, r, g, b, brightness, speed, applyNow)`. Pattern bits double as the mode value: `0x1` static, `0x2` pulse, `0x4` flash, `0x8` colour cycle, `0x10` double flash, `0x20` droplet, `0x40` colour droplet, `0x80` wave, `0x100` colour strobe, `0x200` cascade, `0x400` colour cascade (`0x7FF` = all supported). Brightness 0–9, speed 0–5.

## Building the samples

All three are **MFC dialog apps, Win32/x86 only**, on long-dead toolsets:

| Sample | Project | Toolset | MFC | Notes |
|---|---|---|---|---|
| Motherboard | `GLedApi.sln` | v110 → **retargeted in-tree to v145** | static | only sample with a `.sln` |
| AORUS DDR | `Sample.vcxproj` | v140 (VS2015) | dynamic | declares x64 configs that cannot link |
| Peripherals | `LedDemo.vcproj` | VS2008 format | static | MSBuild cannot build `.vcproj` at all |

```bash
msbuild "B19.0311.1/Sample/Motherbaord/GLedApi/GLedApi.sln" /p:Configuration=Release /p:Platform=Win32
```

```bash
msbuild "B19.0311.1/Sample/AORUS_DDR/Sample.vcxproj" /p:Configuration=Release /p:Platform=Win32
```

`testGLedApi.vcxproj` has already been edited in place from v110 to **v145** (both the `Debug|Win32` and `Release|Win32` property groups). The DDR sample still declares v140 in four places; retarget it with `/p:PlatformToolset=v145 /p:WindowsTargetPlatformVersion=10.0` on the command line instead of editing it, unless there's a reason to make the change permanent. `LedDemo.vcproj` must be upgraded to `.vcxproj` first (`devenv LedDemo.vcproj /Upgrade`) — MSBuild has not supported the VS2008 format since VS2010.

**On this machine right now:** VS 18 Community at `C:\Program Files\Microsoft Visual Studio\18\Community`, MSBuild at `MSBuild\Current\Bin\MSBuild.exe`. Only toolset **v145** is installed (no v110, no v140), and **`atlmfc` is not installed**. The v145 retarget above is accepted, but the build then stops at `error MSB8041: this project requires the MFC libraries`. All three samples are MFC apps, so none compiles until the "C++ MFC for latest v145 build tools (x86 & x64)" component is added via the VS Installer's Individual Components tab. Verify with `Get-ChildItem "$vs\VC\Tools\MSVC\*\atlmfc"`.

**x86 only.** Every shipped DLL and both import libraries are 32-bit (`.lib` symbols carry the leading-underscore cdecl decoration). The x64 configurations in `Sample.vcxproj` are vestigial and will fail at link. Consumers in any language must be 32-bit processes.

## Running: deployment and privileges

Each DLL pulls in siblings it loads dynamically, so partial copies fail at runtime with unhelpful errors. Ship the whole folder next to the `.exe`:

- **Motherboard:** `GLedApi.dll` + `yccV2.dll` + `layout.ini`. `GLedApi.dll` `LoadLibrary`s `yccV2` and parses `layout.ini` for board-specific zone maps; missing either yields "Load ycc.dll fail." or "Can't find layout.ini".
- **Peripherals:** `GvLedLib.dll` + `GvDisplay.dll` + `GvBiosLib.dll` + `GPCIDrv.sys` / `GPCIDrv64.sys`.
- **DDR:** `LEDCtrl.dll` + `MFCSPD.dll` + `SKYX.dll` + `yccV2.dll`. `LEDCtrl.dll` loads `MFCSPD.dll` and `yccv2.dll` dynamically.

**Administrator is required.** `yccV2.dll` calls `CreateServiceW`/`StartServiceW` to install and start a kernel-mode WDF driver for low-level bus access, so both the motherboard and DDR paths need elevation to initialize. The peripherals folder ships its own kernel drivers (`GPCIDrv.sys`, `GPCIDrv64.sys`), so assume the same requirement there. Changelog for 1.1.6 is "Fix some driver security issue" — expect AV/EDR interest in these drivers.

Also expect conflicts with GIGABYTE's own RGB Fusion application if it is running: both drive the same MCU.

Per the SDK guide: Windows 7/8.1/10 (32- and 64-bit), IT8295 MCU, Intel X299/H310/B360/H370/Z370 and AMD AX370/X399/AX470 boards, and the VC++ 2012 redistributable.

## Known inconsistencies — verify against binaries, not docs

The vendor tree is internally inconsistent in ways that will waste hours if taken at face value.

**The peripherals sample cannot be built or run as shipped.** Three separate breaks, all confirmed by inspecting the binaries:
- `GvLedLib.lib` resolves exactly ten symbols: `GvLedInit`, `GvLedIsConnect`, `GvLedKGetDef`, `GvLedKSet`, `GvLedMGet/MSave/MSet`, `GvLedXGet/XSave/XSet`. `LedDemoDlg.cpp` also calls `GvLedInitial` and `GvLedAM300Set/Get/Save` → unresolved externals. The generic `GvLedSet`/`GvLedGet` that the header and PDF present as the main entry points are missing from it too.
- `DLLs/Peripherals/GvLedLib.dll` exports `dllexp_GvLedInitial`, `dllexp_GvLedSet`, … — the `dllexp_`-prefixed names from the PDF, none of which match the undecorated names in the header or the `.lib`. The shipped DLL and the shipped import library are from different builds.
- That DLL imports `mfc110ud.dll` and `MSVCR110D.dll` (the *debug* VS2012 runtimes, not redistributable) and `GvIllumLib.dll`, which is not shipped anywhere in this tree.

If peripherals support is needed, bind by name with `LoadLibrary`/`GetProcAddress` against the actual `dllexp_Gv*` exports rather than trusting `GvLedLib.h`, and expect the missing `GvIllumLib.dll` to still block loading.

**Prefer `DLLs/` over the copies under `Sample/`.** `Sample/Motherbaord/GLedApi/{Debug,Release}/GLedApi.dll` are stale 2018-02-05 builds with 31 exports; `DLLs/Motherboard/GLedApi.dll` is 2018-06-14 with 32 (it adds `dllexp_Disable_IT8295_DLedStrip`). `ycc.dll` in those folders is a 2017 build superseded by `yccV2.dll`.

**Headers beat PDFs on names and signatures.** The DDR PDF documents `dllexp_GetSupportDevice`/`dllexp_GetSupportPattern` and a 7-argument `dllexp_SetEffect`; the DLL actually exports `dllexp_GetSuportDevice`/`dllexp_GetSuportPattern` (vendor typo) and `LEDCtrl.h` declares `dllexp_SetEffect` with an 8th `bool ApplyNow`.

**PDFs beat headers on device IDs.** `GvLedLib.h` and the SDK guide assign conflicting values above `0x2001`/`0x3001` — e.g. the header has `0x4003` = AORUS H5 while the PDF has `0x4003` = XTC700 Case and `0x4007` = AORUS H5. Confirm against real hardware before relying on either.

**Sample comments are unreliable.** `testGLedApiDlg.cpp:262` labels mode `5` as "Color cycle" when 5 is Flash (3 is Color Cycle — the file's own switch statement gets this right). `SampleDlg.cpp:94` says "pattern=>Static" while passing `0x10`, which is Double Flash.

**`GvLedLib.h` comments are mojibake.** They were written in legacy Chinese ANSI codepages and render as garbage in UTF-8. The identifiers and numeric constants are intact and readable; don't try to "repair" the comment bytes, and don't infer meaning from the garbled text.

## The current application is at the repository root — start with `App/`

`App/` is a WPF app (**.NET 10, x64**) that controls every RGB device in the machine
through **OpenRGB's SDK server**, not through any vendor library. This is the live project; the
sections below about the 2019 SDK and `RgbFusionApp old/` are history, kept because their traps
still cost time if rediscovered.

**Run it** with the desktop shortcut, or `RGB Studio.cmd`, which starts OpenRGB elevated
(from its own folder — it needs its PawnIO modules there) and then the app. OpenRGB lives in
`../OpenRGB/`; **PawnIO must be installed** or SMBus is unavailable and the RAM disappears.

Layout: `Rgb/RgbService.cs` (all hardware access), `Effects/` (software effect engine),
`Rendering/DeviceRenderer.cs` (per-device drawing), `Controls/ColorPicker.xaml` (HSV wheel),
`Rgb/ZoneCatalog.cs` (the hand-mapped Z390 zones), `AppWatcher.cs` (foreground-app colour).

`AppWatcher` polls the foreground window and derives a colour from the executable's icon —
hue-bucketed by `saturation × value`, greys and near-blacks discarded, saturation floored so it does
not read as white on an LED. It feeds `Picker.SetColor`, so everything downstream (live paint,
`EffectEngine.UpdateColor`) already knows what to do. Two deliberate behaviours: RGB Studio's own
process is skipped, or picking a colour by hand would immediately overwrite itself; and an icon with
no colour at all (consoles, Unity Hub) leaves the previous colour alone instead of going grey.

### Devices on this machine

| Device | LEDs | Notes |
|---|---|---|
| Z390 AORUS PRO WIFI | 6 | 8 zones; 2 D_LED headers are empty (0 LEDs) |
| HyperX Alloy Elite | 128 | Keyboard zone is a **6×23 Matrix** with per-key names |
| ENE DRAM ×2 | 5 each | XPG Spectrix, reached over SMBus |

**Verified Z390 zone map** (names are generic in OpenRGB, so these were found by lighting each zone
one at a time): `Led 1` = AORUS logo, `Led 2` = RAM slot trim, `Led 3` = lower AORUS eagle,
`Led 4` = lower-left strip, **`Led 5` = case fans + CPU cooler**, `Led 8` = nothing (omitted).

### Rules this codebase learned the hard way

- **Set the `Direct` mode before sending colors.** In any other mode the firmware runs its own
  effect and silently ignores `UpdateLeds`. `SetCustomMode()` is *not* the same thing.
- **Send the whole device in ONE `UpdateLeds`.** Several `UpdateZoneLeds` calls in a row make the
  IT8297 apply the first and drop the rest.
- **There is no partial update** — every packet carries the full device state, so `RgbService` keeps
  a persistent colour buffer. Building a black frame and painting only the selection wipes
  everything else.
- **`Mode.ColorMode` decides where the colour goes**: `ModeSpecific` → inside the mode packet
  (Z390); `PerLed` → a separate `UpdateLeds` after switching (RAM, keyboard); `None` → the mode has
  no colour at all (Rainbow, Wave, Spectrum Cycle). Treating everything as `ModeSpecific` left the
  RAM stuck red and the keyboard white.
- **Never touch the hardware from the UI thread.** 128 LEDs at 15 fps plus the deliberate sleeps in
  mode changes froze the window. The effect engine runs on its own thread and `RgbService` holds a
  lock, because neither `OpenRgbClient` nor the state buffers are thread-safe.
- **Effects are per-zone assignments, not one global effect** — see `EffectEngine`. Each zone can
  run its own; a device-wide effect is drawn before zone effects so the more specific one wins.
- **Give each zone the full 0..1 position range.** Spreading one effect across all 144 LEDs sounds
  elegant and looks terrible: the motherboard is 4% of that range.
- **An addressable-strip zone reports 0 LEDs until you declare its length.** A strip cannot be
  interrogated — 10 LEDs and 60 look identical on the bus — so plugging one in changes nothing until
  `ResizeZone(device, zone, count)` is called, and OpenRGB forgets the number between runs.
  `StripSizes` persists it to `%AppData%\RgbStudio\tiras.json` and re-declares it on connect.
  `Zone.LedsMax > Zone.LedsMin` is what marks a zone as resizable.
- **Not our bug:** on the HyperX keyboard, `Static`, `Wave` and `Breathing` behave identically.
  Confirmed in OpenRGB's own UI — it is that driver, not this app.

## History: the 2019 vendor SDK is obsolete — RGB Fusion 3.x replaced it

**Verified end-to-end on 2026-08-04: LEDs confirmed changing colour on a Z390 AORUS PRO WIFI.**
The working consumer is `RgbFusionApp/`. It does **not** use anything in `B19.0311.1/`.

**The 2019 SDK cannot initialize on current Windows.** `dllexp_InitAPI` always dies with
`0xC0000005` inside the vendor DLL, because its kernel driver never loads:

| Driver | Loaded by | Failure |
|---|---|---|
| `gdrv.sys` | old `ycc.dll` (in `Sample/.../Release/`) | revoked by Microsoft, vulnerable-driver blocklist — CodeIntegrity event **3023** |
| `gdrv2.sys` | newer `yccV2.dll` (in `DLLs/Motherboard/`) | **code-signing certificate expired** — SCM event **7000** |

Do not debug the caller when `InitAPI` crashes, and do not suggest disabling Memory Integrity or
driver-signature enforcement. Install **RGB Fusion 3.x** (`C:\Program Files (x86)\GIGABYTE\RGBFusion`)
and use its libraries, which reach the hardware over **USB HID with no kernel driver at all**:

| Target | Library | Entry points |
|---|---|---|
| **Motherboard** | `LedLib2.dll` — .NET Framework 4.7.2, x86 | `LedLib2.LedObject.SetIT8297LedMode(LedMode, color, speed, brightness)` then `ApplyAll_v4()`; also `TurnOffAll()`, `SaveSettingToMcu()`, `bUseIT8297` |
| Peripherals & VGA | `GvLedLib.dll` — native, 79 exports, all `Cdecl` | `dllexp_GvLedInitial(ref count, buf, iSyncMBLed)`, `dllexp_GvLedSet(id, cfg)` |

RGB Fusion 3.x ships **no `GLedApi.dll`** — that whole API is gone.

Three things that will otherwise cost hours:

- **`new LedObject()` does not connect to the MCU — you must call `RescanGBLedPeripherals()`.**
  The constructor only identifies the chip. Skip the rescan and `bUseIT8297` still returns true while
  the internal device object is never built: `GBLedDevConnectState()` returns 0, every call returns
  `0` while the LEDs never change, and `SetLedBrightNess_IT8297`/`Reset_IT8295` throw
  `NullReferenceException`. This one missing call cost hours of chasing phantom bugs.
- **Prefer `SetLedEffect_v1` over `SetIT8297LedMode`.** The IT8297-specific entry point rejects modes
  the generic one accepts — including `StillMode` and `BeatMode`.
- **The `bXxxSupport` flags are wrong in both directions — ignore them.** Verified visually on IT8297,
  these 10 effects work: `StillMode`, `BreathMode`, `AutoMode`, `FlashMode`, `DFlashMode`,
  `RandomMode`, `RandomByIT8297`, `SWaveMode1`, `Wave1`, `Wave2`, plus `Off`. The other 10 are
  rejected with 4317 (`DarkOff`, `WaveMode`, `SceneMode`, `ConditionMode`, `StartSlideMode`,
  `MeteorMode`, `ShakingMode`, `ExpiosionMode`, `RecoverMode`, `DoubleMeteorMode`) — they target
  addressable D_LED strips, and this board reports **0 headers**. There is no "Static".
  Re-run `rgbfusion demo` to re-verify on other hardware; give each effect ~8 s, because the wave
  variants are subtle and get missed at 6 s.
- **`GVLED_CFG_V1` is 11 ints = 44 bytes**, and `nOn`/`nSync` are `int` — the 2019 header declares
  them `bool` (1 byte in C++), which silently misaligns the struct.
- **Admin is required, but not for the hardware.** `LedObject`'s constructor calls
  `EventLog.SourceExists`, which throws `SecurityException` unelevated.

`LedLib2` is undocumented internal GIGABYTE API; an RGB Fusion update could change it. It is
referenced with `Private=false` and resolved at runtime from the install folder
(`AppDomain.AssemblyResolve` + `SetDllDirectory`) because it needs its native siblings — see
`RgbFusionApp/SdkLocator.cs`. Close RGB Fusion before running, or both fight for the MCU.

## Do not enable music mode — it hangs the MCU

`LedMode.BeatMode` puts the IT8297 into a passive state waiting for software beat pulses
(`LedObject.BeatCtrl`). On 2026-08-06 this **wedged the chip at firmware level**: it stopped
answering USB enumeration and reappeared as `USB\VID_0000&PID_0002` with "Device Descriptor Request
Failed" on the same port (`5&356B5377&0&12`) where `VID_048D&PID_8297` had been. Every subsequent
call returned success while the LEDs stayed frozen.

A normal shutdown does **not** clear it — the PSU's 5 V standby rail keeps the MCU powered. Recovery
required unplugging the machine and holding the power button ~15 s to drain the capacitors.

No reliable software path out of that state was found, so the `beat` command was removed. Do not
reintroduce it without first learning how RGB Fusion itself exits music mode.

**Related trap:** when the MCU is hung, the SDK still returns `0` for everything. Treat "the LEDs did
not change" as the only real failure signal, and check that
`Get-PnpDevice | Where InstanceId -match 'VID_048D&PID_8297'` still reports `OK` before debugging code.

## Verification

There are no tests, no CI, and no mocking layer — the DLLs talk to real hardware over SMBus/HID. Verification is manual: build a sample, run it elevated on a supported board, and watch the LEDs. When adding a consumer, always check the `ERROR_*` / `GV_LED_API_*` return of every call; the samples frequently ignore them, and a silent failure and a successful write look identical from the host side.
