# Wasabi Preview — hardware wallet coinjoin

This is an **unofficial preview build** of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi) from the `kravens` fork. It merges all of the hardware-wallet coinjoin work (Trezor, Coldcard, Passport Prime, Krux) into one build, based on the official **v2.8.1** release, so people can try it and give feedback before the work is split into reviewable upstream PRs.

**⚠️ This is experimental software.**

- The builds are **not signed** by the Wasabi team. A PGP-signed SHA256 manifest is attached to each release; verify against it.
- Use **small amounts** you can afford to lose while testing.
- The built-in updater still points at **official releases**: if you accept its update, you get the official build **without** hardware-wallet coinjoin. To keep testing, decline and install the next preview from this fork instead.
- Everything here signs coinjoins on the device or under a device-enforced policy — the host never sees your keys — but the integration code is young.

## Supported devices

### Trezor (Model T, Safe 3, Safe 5) — most mature
- Stock firmware. Coinjoins land in the device's SLIP-25 coinjoin account, authorization is confirmed on the device (rounds + max mining fee rate).
- Needs the Trezor bridge: either the one bundled with Trezor Suite, or a standalone `trezord`. The wallet auto-manages bridge/USB contention.
- Mainnet coinjoin has been verified end-to-end with this stack.

### Coldcard Mk4/Mk5 — tested on Windows, Linux and Mac
- Requires our [custom Coldcard firmware](https://github.com/kravens/firmware): branch [`feature/slip19-coinjoin`](https://github.com/kravens/firmware/tree/feature/slip19-coinjoin), or [`feature/slip19-coinjoin-edge`](https://github.com/kravens/firmware/tree/feature/slip19-coinjoin-edge) for the edge/taproot line. It adds SLIP-19 ownership proofs and the coinjoin HSM rules. (Mk4/Mk5 only: the Q ships with HSM commands disabled, Mk3 firmware is too old.)
- A [prebuilt, PGP-signed Mk4 image](https://github.com/kravens/coinjoin.nl/tree/main/firmware) is available, with hash verification, SD-card/ckcc install steps and the way back to official Coinkite firmware.
- Isolation comes from an HSM policy you review and approve on the device (max sats leaving, transactions per period, minimum round inputs). Signing then runs unattended under that policy.

### Foundation Passport Prime — firmware work required
- Dev demo: [Coinjoin Signer on Foundation's app showcase](https://foundation.xyz/app-showcase/coinjoin-signer).
- **Stock KeyOS is not compatible.** Requires a custom KeyOS firmware build carrying the coinjoin protocol messages; without it the device will not respond.
- USB support is Windows-first for now; treat this integration as a developer demo.

### Krux (e.g. WonderMV) — developer demo
- Requires a Krux device flashed with the coinjoin signing extension and the `kruxd` companion daemon running on the host (default port 21326).
- The session policy and round budget are approved physically on the device; the wallet only connects to an already-authorized session.
- There is no import UI yet: mark an imported watch-only hardware wallet as Krux-backed by setting `"CoinJoinVendor": 3` in its wallet JSON file.

## Feedback

Open an issue on this fork (`kravens/WalletWasabi`) or reply in the Wasabi Slack. Wallet logs (`Logs.txt` in the data folder) plus your OS and device model make reports actionable.
