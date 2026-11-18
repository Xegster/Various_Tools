# SamsungRoot

Restores Magisk root on Samsung phones that "lost root" (built around the Galaxy S10 family, SM-G970F / SM-G973F).

```powershell
.\Restore-SamsungRoot.ps1              # Auto: check, try recovery boot, flash if needed
.\Restore-SamsungRoot.ps1 -Mode Check  # read-only status
.\Restore-SamsungRoot.ps1 -Mode RecoveryBoot
.\Restore-SamsungRoot.ps1 -Mode Flash -Serial <adb serial>
```

Needs: adb, USB debugging authorised on the phone, an unlocked bootloader, Odin 3.14.4, and a
Magisk-patched AP for the phone's exact firmware build (defaults to `C:\Xegster\Android Rooting`,
matched by the phone's build id, e.g. `G970FXXSEFUL1`).

## What we learned (2026-11-18, SM-G970F, Android 11)

- On these phones Magisk lives in the **recovery** partition. Root only exists when the phone boots
  through recovery. A normal boot = no `su`, Magisk app shows N/A, even though nothing is broken.
- `adb reboot recovery` boots through the patched recovery reliably. The physical combo
  (Vol Up + Bixby + Power, release at the red text) is timing-sensitive and failed repeatedly.
  The script uses the ADB route.
- Odin was run **AP only** (BL/CP/CSC empty, Re-Partition off), so data was kept. Auto Reboot was
  turned off for the working flash. It was not proven to be required, but it avoids an unwanted
  normal boot straight after the flash.
- Odin showed two ports; the ghost port ("Can't open the serial(COM) port", failed 1) is harmless
  when the other port says PASS.
- Samsung's `flash_recovery_sec` / `applypatch` can restore stock recovery on a normal boot.
  `applypatch: Failed to find any match` in logcat means the patched recovery was left alone.
- Do not plain-restart afterwards. Reboot from Magisk or use `-Mode RecoveryBoot`.

## Tested

- 2026-11-18, SM-G973F (Android 12, G973FXXUHHXJ1): `-Mode Check` and `-Mode RecoveryBoot` worked;
  the phone regained root without any flashing. The test found and fixed a bug (adb "device not
  found" while the phone reboots was treated as fatal).
- `-Mode Flash` (Odin) was done by hand on SM-G970F, not through the script. The script's Flash
  path has not been run end to end.

## Gotchas

- Reading `ro.boot.flash.locked` / verified boot can report LOCKED/green when Shamiko or Play
  Integrity Fix is installed (they spoof it). The script's "bootloader locked" refusal for Flash
  mode could therefore misfire on a rooted phone; use it on a phone that has lost root.
- Ad-blockers that write `/system/etc/hosts` (AdAway in direct mode) leave their blocklist behind
  when root is lost. Symptom: sites such as `t.co` resolving to localhost. Fix: restore the stock
  two-line hosts file (`127.0.0.1 localhost` / `::1 ip6-localhost`) as root.

## Later: full app

Odin is a GUI, so the flash step is semi-manual. A full app could replace it with a Heimdall or
raw-protocol flasher, a firmware/patched-AP library keyed by build id, and a multi-phone view.
