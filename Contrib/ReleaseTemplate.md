**Unofficial preview build** of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi) from this fork, bringing hardware-wallet coinjoin for **Trezor, Coldcard, Foundation Passport Prime, Krux and SabiSigner** into one build. This is **Preview 8**: the feature set of [v2.8.3.7](https://github.com/kravens/WalletWasabi/releases/tag/v2.8.3.7), which it supersedes, plus Coldcard and wallet-settings fixes, rebuilt on the official Wasabi **master** branch as of 14 October 2026 (after v2.8.4) and on the Trezor branch rebased the same day (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759)).

> ⚠️ **Experimental software.** These builds are **not signed by the Wasabi team**. Use small amounts you can afford to lose. The built-in updater still points at official releases: accepting its update gives you the official build **without** hardware-wallet coinjoin.

## What changed since v2.8.3.7

- **Built on upstream master as of 14 October 2026, after v2.8.4** (this is the `v{version}` build). Brings what was merged since Preview 7: the v2.8.4 release notes, the BitBox02 Nova is recognized, the crash on opening Receive after a restart is fixed, a Send flow no longer stays alive through the event bus after it is closed, the JSON-RPC server's authentication path is hardened and its onion forwarding listens on loopback only, the coordinator checks every output script against the inputs of every round, the release pipeline is pinned (appimagetool and the AppImage runtime are hash-checked, runner images, actions and installer tools are fixed to known versions), and the Tor manager was prepared for picking free ports.
- **Trezor branch rebased** (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759) at `b96c7b019b`) on that master. The only change was adapting the device-signing path to upstream's reworked request scheduling: the device still signs once up front, and the signatures still go out on the usual spread-out schedule. No behaviour changed for any device.
- **Coldcard: an unplugged device stops the coinjoin** (fork PR [#3](https://github.com/kravens/WalletWasabi/pull/3)). Before every round the device is asked whether its policy is still running; a Coldcard that is gone stops the coinjoin with "Connect and unlock your device, then press Play" instead of failing to register round after round with nothing shown. Play is offered at once. A Coldcard reboots when unplugged, so plugged back in it offers its saved policy at login: approve it there and Play continues without a new prompt, decline it and Play asks the device to approve the policy again. A HID failure while proving or signing is reported the same way. Verified on a Mk4: unplugged between rounds and during input registration, the coinjoin stops with that message; an earlier build continued without HSM mode after the policy was declined at login, which this build fixes. Not yet verified: an unplug during the signing phase, which still gets that round's inputs banned by the coordinator; the coinjoin stops right after that round.
- **Enabling coinjoin later works for a Coldcard.** A Coldcard wallet imported without coinjoin got "No Trezor device found" from "Enable coinjoin" in the wallet settings, since that button only knew the Trezor. It now finds the wallet's device by its fingerprint and enables coinjoin the way that device signs: a Trezor adds its coinjoin account as before, a Coldcard is switched to sign coinjoins from its existing account (it approves its policy when you first press Play), and a Passport or Krux is told to import the wallet again with coinjoin on. Verified on a Mk4.
- **No surprise restart after enabling coinjoin or taproot.** Wasabi used to close and reopen right after either button, which looked like a crash. It now says the change is saved and offers "Restart now" or "Later"; both can be enabled before one restart.
- **Passport Prime and Krux** are the same code as Preview 7, restacked on the rebased Trezor branch; so is the Coldcard code apart from the fixes above.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, and reading wallets written by Previews 1-4.

## Devices

| Device | Status |
|---|---|
| **Trezor** (Model T, Safe 3, Safe 5) | Most mature; mainnet coinjoin verified end-to-end with earlier previews of this stack. Stock firmware. Rounds and max fee rate confirmed on the device. Needs a Trezor bridge (Suite's, or standalone `trezord`). |
| **Coldcard Mk4/Mk5** | Requires [custom Edge firmware `coinjoin-fw-2`](https://github.com/kravens/firmware/releases/tag/coinjoin-fw-2) (branch `feature/slip19-coinjoin`, 6.6.1X); `coinjoin-fw-1` images are refused. Unattended under an HSM policy you approve on the device, or attended with one confirmation per coin. |
| **Coldcard Q** | **Do not flash the custom firmware** — the Q has no HSM mode, and a bad image is unrecoverable on a retail Q. Attended flow verified on the simulator only. |
| **Foundation Passport Prime** | Developer demo, protocol v2. Stock KeyOS does not open the USB interface for a sideloaded app; no hardware verification yet. |
| **Krux / SabiSigner** | Needs the coinjoin signing extension (Krux) or a [SabiSigner](https://github.com/kravens/SabiSigner), plus the updated `kruxd` daemon (default port 21326). Import from the connect screen; SabiSigner signs taproot only. |

Full tester notes in [`PREVIEW.md`](https://github.com/kravens/WalletWasabi/blob/preview8/PREVIEW.md).

## Download

| Platform | File |
|---|---|
| Windows | [Wasabi-{version}.msi](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}.msi) · [zip](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-win-x64.zip) |
| macOS Apple Silicon | [Wasabi-{version}-arm64.dmg](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-arm64.dmg) · [zip](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-macOS-arm64.zip) |
| macOS Intel | [Wasabi-{version}.dmg](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}.dmg) · [zip](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-macOS-x64.zip) |
| Linux x64 | [AppImage](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}.AppImage) · [deb](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}.deb) · [tar.gz](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-linux-x64.tar.gz) · [zip](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-linux-x64.zip) |
| Linux arm64 | [AppImage](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-arm64.AppImage) · [deb](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-arm64.deb) · [tar.gz](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-linux-arm64.tar.gz) · [zip](https://github.com/kravens/WalletWasabi/releases/download/v{version}/Wasabi-{version}-linux-arm64.zip) |

## Verifying

`SHA256SUMS.txt` lists every asset and is PGP-signed; the packages themselves are unsigned by the Wasabi team.

```
sha256sum -c SHA256SUMS.txt --ignore-missing
gpg --verify SHA256SUMS.txt.sig SHA256SUMS.txt
```

PGP key `2536 F69E 9C37 2566 2B6C 146B DCF1 7F7A 0127 2020` (Kevin Ravensberg).
