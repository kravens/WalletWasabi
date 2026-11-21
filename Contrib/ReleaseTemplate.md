**Unofficial preview build** of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi) from this fork, bringing hardware-wallet coinjoin for **Trezor, Coldcard, Foundation Passport Prime, Krux and SabiSigner** into one build. This is **Preview 11**: the feature set of [v2.8.5.10](https://github.com/kravens/WalletWasabi/releases/tag/v2.8.5.10), which it supersedes, rebuilt on upstream Wasabi master of 19 November 2026 (after v2.8.5) and on the Trezor branch rebased on it the same day (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759)).

> ⚠️ **Experimental software.** These builds are **not signed by the Wasabi team**. Use small amounts you can afford to lose. The built-in updater still points at official releases: accepting its update gives you the official build **without** hardware-wallet coinjoin.

## What changed since v2.8.5.10

- **Built on upstream master after v2.8.5** (this is the `v{version}` build). It fixes the crash "Collection was modified; enumeration operation may not execute" that came when the coin list read a coin's labels while a transaction was processed, and the crash when quitting from the tray icon on Linux. Also in it: the exchange rate in other fiat currencies, a pay-to-many warning, coinjoining into another wallet pays only that wallet's own payments, a max coinjoin fee rate below 1 sat/vByte, NBitcoin 10.0.21 with new mainnet DNS seeds, steadier compact filter downloads, and Tor waited on until it has bootstrapped.
- **Trezor branch after review** (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759) at `9e0fdd7474`): a wallet whose device lost its authorization stops and says so, instead of restarting into a device prompt nobody answers; press Play to authorize again. A round with none of your inputs to sign no longer crashes, and signing over RPC is no longer cut off after 5 minutes.
- **Coldcard, Passport Prime, Krux and SabiSigner** are the same code as Preview 10, restacked on the rebased Trezor branch.
- **New, not in use yet:** libusb is bundled for every platform for a future bridge-free Trezor transport (upstream PRs [#15313](https://github.com/WalletWasabi/WalletWasabi/pull/15313) and [#15314](https://github.com/WalletWasabi/WalletWasabi/pull/15314)). The Trezor still goes through the bridge in this build.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, reading wallets written by Previews 1-4, and enabling coinjoin later for the vendor of the connected device.

## Devices

| Device | Status |
|---|---|
| **Trezor** (Model T, Safe 3, Safe 5) | Most mature; mainnet coinjoin verified end-to-end with earlier previews of this stack. Stock firmware. Rounds and max fee rate confirmed on the device. Needs a Trezor bridge (Suite's, a standalone `trezord`, or `pytrezord`). |
| **Coldcard Mk4/Mk5** | Requires [custom Edge firmware `coinjoin-fw-2`](https://github.com/kravens/firmware/releases/tag/coinjoin-fw-2) (branch `feature/slip19-coinjoin`, 6.6.1X); `coinjoin-fw-1` images are refused. Unattended under an HSM policy you approve on the device. |
| **Coldcard Q** | **Not supported. Do not flash the custom firmware** — the Q has no HSM mode, and a bad image is unrecoverable on a retail Q. |
| **Foundation Passport Prime** | Developer demo, protocol v2. Stock KeyOS does not open the USB interface for a sideloaded app; no hardware verification yet. |
| **Krux / SabiSigner** | Needs the coinjoin signing extension (Krux) or a [SabiSigner](https://github.com/kravens/SabiSigner), plus the updated `kruxd` daemon (default port 21326). Import from the connect screen; SabiSigner signs taproot only. |

Full tester notes in [`PREVIEW.md`](https://github.com/kravens/WalletWasabi/blob/preview11/PREVIEW.md).

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
