**Unofficial preview build** of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi) from this fork, bringing hardware-wallet coinjoin for **Trezor, Coldcard, Foundation Passport Prime, Krux and SabiSigner** into one build. This is **Preview 10**: the feature set of [v2.8.4.9](https://github.com/kravens/WalletWasabi/releases/tag/v2.8.4.9), which it supersedes, rebuilt on the official Wasabi **v2.8.5** security release (27 October 2026) and on the Trezor branch rebased on it the same day (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759)).

> ⚠️ **Experimental software.** These builds are **not signed by the Wasabi team**. Use small amounts you can afford to lose. The built-in updater still points at official releases: accepting its update gives you the official build **without** hardware-wallet coinjoin.

## What changed since v2.8.4.9

- **Built on the official v2.8.5 release** (this is the `v{version}` build). v2.8.5 is a security release: transactions that spend your coins are verified against forged signatures before the wallet believes them, Brotli decompression is disabled in the HTTP clients, and .NET 10.0.14 and NBitcoin 10.0.17 close disclosed dependency vulnerabilities. Also in it: the private payment coin selector, coinjoin payments below 10,000 sats are refused instead of getting stuck, coinjoins that pay exactly the fees charged are accepted, the history refreshes when coinjoin costs are saved, wallet recovery keeps loading while filter headers catch up, and the unreachable Nostr relays are replaced.
- **Trezor branch rebased** (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759) at `a7c07182e8`) on v2.8.5 without a code change. The Coldcard, Passport Prime, Krux and SabiSigner code is the same as in Preview 9, restacked on it.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, reading wallets written by Previews 1-4, and enabling coinjoin later for the vendor of the connected device.

## Devices

| Device | Status |
|---|---|
| **Trezor** (Model T, Safe 3, Safe 5) | Most mature; mainnet coinjoin verified end-to-end with earlier previews of this stack. Stock firmware. Rounds and max fee rate confirmed on the device. Needs a Trezor bridge (Suite's, or standalone `trezord`). |
| **Coldcard Mk4/Mk5** | Requires [custom Edge firmware `coinjoin-fw-2`](https://github.com/kravens/firmware/releases/tag/coinjoin-fw-2) (branch `feature/slip19-coinjoin`, 6.6.1X); `coinjoin-fw-1` images are refused. Unattended under an HSM policy you approve on the device. |
| **Coldcard Q** | **Not supported. Do not flash the custom firmware** — the Q has no HSM mode, and a bad image is unrecoverable on a retail Q. |
| **Foundation Passport Prime** | Developer demo, protocol v2. Stock KeyOS does not open the USB interface for a sideloaded app; no hardware verification yet. |
| **Krux / SabiSigner** | Needs the coinjoin signing extension (Krux) or a [SabiSigner](https://github.com/kravens/SabiSigner), plus the updated `kruxd` daemon (default port 21326). Import from the connect screen; SabiSigner signs taproot only. |

Full tester notes in [`PREVIEW.md`](https://github.com/kravens/WalletWasabi/blob/preview10/PREVIEW.md).

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
