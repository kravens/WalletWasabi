# Wasabi Preview — hardware wallet coinjoin

This is an **unofficial preview build** of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi) from the `kravens` fork. It merges all of the hardware-wallet coinjoin work (Trezor, Coldcard, Passport Prime, Krux) into one build, based on the official **v2.8.2** release plus the upstream fixes merged up to 3 September 2026, so people can try it and give feedback before the work is split into reviewable upstream PRs.

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
- Requires our [custom Coldcard firmware](https://github.com/kravens/firmware), branch [`feature/slip19-coinjoin`](https://github.com/kravens/firmware/tree/feature/slip19-coinjoin). It is now built on Coinkite's **Edge** line (6.6.1X) — the only Coldcard line that signs taproot, and Wasabi rounds are taproot. It adds SLIP-19 ownership proofs and the coinjoin HSM rules, and is under review upstream as [Coldcard PR #685](https://github.com/Coldcard/firmware/pull/685). (Mk3 firmware is too old.)
- Prebuilt image on the [`coinjoin-fw-2` release](https://github.com/kravens/firmware/releases/tag/coinjoin-fw-2): `6.6.1X` Edge for Mk4 **and Mk5**, with SHA256 manifest, install steps and the way back to official Coinkite firmware. The `coinjoin-fw-1` images (5.6.0 / 6.6.0X) are superseded; the 5.6.0 stable-line build cannot sign a taproot round.
- Unattended: isolation comes from an HSM policy you review and approve on the device (max sats leaving, transactions per period, fee per vbyte, minimum round inputs). Signing then runs under that policy without touching the device.
- Attended, no policy: the device asks on screen for every ownership proof (path, address, commitment digest) and for the round's PSBT. Slower — one confirmation per coin — but it needs no HSM setup and is the way to try a round before committing to a policy.
- **Coldcard Q: do not flash this firmware.** The Q has no HSM mode, and an earlier experimental Q build bricked a device: a validly signed image that fails before login is unrecoverable on a retail Q. The attended flow is verified on the Q *simulator* only. A Q image waits on Coinkite confirming a recovery path.

### Foundation Passport Prime — firmware work required
- Dev demo: [Coinjoin Signer on Foundation's app showcase](https://foundation.xyz/app-showcase/coinjoin-signer).
- **Stock KeyOS is not compatible.** Requires a custom KeyOS firmware build carrying the coinjoin protocol messages; without it the device will not respond.
- USB support is Windows-first for now; treat this integration as a developer demo.

### Krux (e.g. WonderMV) — developer demo
- Requires a Krux device flashed with the coinjoin signing extension and the `kruxd` companion daemon running on the host (default port 21326).
- The session policy and round budget are approved physically on the device; the wallet only connects to an already-authorized session.
- There is no import UI yet: mark an imported watch-only hardware wallet as Krux-backed by setting `"CoinJoinVendor": 3` in its wallet JSON file.

## What changed since Preview 2

- **Rebased on the updated Trezor branch, on upstream master after v2.8.2** (this is the `v2.8.2.3` build). Brings the upstream fixes merged up to 3 September 2026: reorg handling when using Bitcoin RPC, the synchronizer waiting for block headers to catch up, a newer filter checkpoint (block 960000), and a missing Windows startup registry key no longer being fatal.
- **Rounds the device would refuse are skipped up front.** The fee cap you confirm on the device now travels with the wallet's key chain, and a round priced between the wallet setting and that cap is skipped before any input is registered. Previously such a round had its inputs registered and then refused at signing, and the coordinator banned them, batch after batch.
- **Accounts read over the Trezor bridge are proven on the device.** The bridge is plain unauthenticated HTTP on localhost. An import over it, and enabling coinjoin later, now has the device show the first receive address of each account, and the wallet puts the same address on screen for you to compare. The wallet file is only written after the check passes. The RPC import methods return the shown addresses as `verifiedAddresses`.
- **Only a real Trezor Bridge is spoken to.** Whatever listened on port 21325 or 21328 used to be treated as the bridge; the wallet now asks the listener who it is first and refuses anything that does not answer like one.
- **A Trezor whose bridge session died is acquired again.** A restarted bridge, a USB error or a stopped wallet left a cached session that failed forever with "session not found", including the Continue of the authorization dialog. The wallet now checks the session before reusing it.
- **`importhardwarewallet` over RPC no longer enables coinjoin by default**, matching the GUI import: a bare call yields a watch-only wallet, and `enableCoinjoin: true` makes it a remote signer.
- **The pre-authorization "would every round be refused" check is gone.** It was skipped on a cold start and passed as soon as one qualifying round existed. The per-round checks, which never went away, are what protects the device from a round it would refuse.

## What changed in Preview 2

- **Rebuilt on Wasabi v2.8.2** (the `v2.8.2.2` build). Picks up upstream's reorg/sync fixes, the participant-input verification in coinjoin, and native Apple Silicon HWI. The pay-in-coinjoin-regardless-of-anonscore option is gone, because upstream removed it.
- **Coldcard firmware moved to the Edge line** (`coinjoin-fw-2`, 6.6.1X) so taproot rounds can be signed, and ownership proofs no longer require an HSM policy — see the Coldcard section.
- **Device enumeration survives a Trezor waiting for its passphrase.** HWI omits the model then; the wallet no longer fails the whole enumeration over it.
- **The Tools tab is back for hardware-backed wallets**, which are watch-only on the host and were losing it.
- **Current Trezor Suite works again.** Suite now ships a rewritten bridge that rejected the wire format Preview 1 used, so the wallet could not connect while Suite was running. It now detects which bridge is present and speaks the matching format. Verified on a Model T against Suite 26.8.2, and against a legacy standalone bridge.
- **The daemon does the work, not the interface.** Every device operation goes through one service in the core with a backend per vendor, so `wassabeed` and the JSON-RPC API can do what the GUI can. `enumeratedevices` and the wallet status now report which vendor a device is.
- **Your device's own limits are visible again**, named for what they mean rather than for the brand, and the settings screen now says plainly which limits your device enforces and which Wasabi does.
- **A spent authorization is noticed before a round starts.** A Coldcard's HSM budget and a Krux's approved session both run out; previously the coinjoin would start and then fail at signing, which looks like a broken device rather than one doing its job.

### If you tested Preview 1, read this

Preview 1 stored the device round budget and the max mining fee rate under Trezor-specific names. Preview 2 renamed them and **does not migrate the old values**: on first start these two settings fall back to their defaults of **50 rounds** and **5 sat/vByte**.

If you had set them *lower* than that, your device will be asked to authorize a **larger** budget than you originally chose. Open the coinjoin settings and set both back to what you want **before** authorizing anything. Every other setting, and your wallets themselves, carry over untouched.

## Feedback

Open an issue on this fork (`kravens/WalletWasabi`) or reply in the Wasabi Slack. Wallet logs (`Logs.txt` in the data folder) plus your OS and device model make reports actionable.
