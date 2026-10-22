**Unofficial preview build** of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi) from this fork, bringing hardware-wallet coinjoin for **Trezor, Coldcard, Foundation Passport Prime, Krux and SabiSigner** into one build. This is **Preview 9**: the feature set of [v2.8.4.8](https://github.com/kravens/WalletWasabi/releases/tag/v2.8.4.8), which it supersedes, plus Coldcard signing fixes and the reviewed vendor code, rebuilt on the official Wasabi **master** branch as of 20 October 2026 (after v2.8.4) and on the Trezor branch rebased on 22 October (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759)).

> ⚠️ **Experimental software.** These builds are **not signed by the Wasabi team**. Use small amounts you can afford to lose. The built-in updater still points at official releases: accepting its update gives you the official build **without** hardware-wallet coinjoin.

## What changed since v2.8.4.8

- **Built on upstream master as of 20 October 2026, after v2.8.4** (this is the `v{version}` build). Brings what was merged since Preview 8: PayJoin is removed, files and folders are opened without going through a shell, a coinjoin whose transaction leaves out one of your registered inputs gets a signature withheld so it cannot be broadcast, the Alices that signed are kept when another signature fails, the `setlabel` RPC, the Windows installer derives its component GUIDs from the install path, release builds no longer embed the checkout path, and on the coordinator side the configured blame-round input minimum is honoured, bans are persisted and the indexer is gone.
- **Trezor branch** (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759) at `de5afaf018`): Play starts the coinjoin the same way for every wallet, with no authorization dialog; the music box says "Confirm coinjoin on your device" while the device asks, and why when the start stopped. The device is asked only when the wallet has something to mix, a device call that does not come back within five minutes is cut off, and a Trezor that another wallet's coinjoin holds is left alone.
- **Coldcard: segwit rounds sign on `coinjoin-fw-2`.** The round handed to the device now carries every input's previous output; without them the Mk4 answered "Signing failed late" on every round with no taproot coin of ours. Taproot coins now carry their BIP-371 derivation too, which is where the device looks for its taproot inputs and change. Larger PSBTs take longer to sign, so a very large round can still outlast a coordinator's signing phase.
- **Coldcard:** a device that is not connected when you press Play says "Connect and unlock your device, then press Play".
- **Closing a wallet lets go of its device**: the Coldcard connection is closed and a Passport session revoked, instead of staying open until Wasabi exits.
- **The settings panel "What the device reports it is enforcing" is gone.** The Coldcard shows its policy when you approve it, the log records it, and the warning about limits that differ from the device's stays.
- **Passport Prime:** requests to the device go one at a time, and a Passport that stops answering needs a new authorization. **Krux and SabiSigner:** the fee-rate cap Wasabi enforces is read from the settings as they are now.
- **macOS:** the USB device a Coldcard or Passport is opened through is kept alive for as long as it is used.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, reading wallets written by Previews 1-4, and enabling coinjoin later for the vendor of the connected device.

## Devices

| Device | Status |
|---|---|
| **Trezor** (Model T, Safe 3, Safe 5) | Most mature; mainnet coinjoin verified end-to-end with earlier previews of this stack. Stock firmware. Rounds and max fee rate confirmed on the device. Needs a Trezor bridge (Suite's, or standalone `trezord`). |
| **Coldcard Mk4/Mk5** | Requires [custom Edge firmware `coinjoin-fw-2`](https://github.com/kravens/firmware/releases/tag/coinjoin-fw-2) (branch `feature/slip19-coinjoin`, 6.6.1X); `coinjoin-fw-1` images are refused. Unattended under an HSM policy you approve on the device. |
| **Coldcard Q** | **Not supported. Do not flash the custom firmware** — the Q has no HSM mode, and a bad image is unrecoverable on a retail Q. |
| **Foundation Passport Prime** | Developer demo, protocol v2. Stock KeyOS does not open the USB interface for a sideloaded app; no hardware verification yet. |
| **Krux / SabiSigner** | Needs the coinjoin signing extension (Krux) or a [SabiSigner](https://github.com/kravens/SabiSigner), plus the updated `kruxd` daemon (default port 21326). Import from the connect screen; SabiSigner signs taproot only. |

Full tester notes in [`PREVIEW.md`](https://github.com/kravens/WalletWasabi/blob/preview9/PREVIEW.md).

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
