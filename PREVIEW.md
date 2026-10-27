# Wasabi Preview — hardware wallet coinjoin

This is an **unofficial preview build** of [Wasabi Wallet](https://github.com/WalletWasabi/WalletWasabi) from the `kravens` fork. It merges all of the hardware-wallet coinjoin work (Trezor, Coldcard, Passport Prime, Krux) into one build, based on the official Wasabi **v2.8.5** release (27 October 2026), so people can try it and give feedback before the work is split into reviewable upstream PRs.

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
- Prebuilt image on the [`coinjoin-fw-2` release](https://github.com/kravens/firmware/releases/tag/coinjoin-fw-2): `6.6.1X` Edge for Mk4 **and Mk5**, with SHA256 manifest, install steps and the way back to official Coinkite firmware. The `coinjoin-fw-1` images (5.6.0 / 6.6.0X) are superseded and **refused** by this build: a firmware that does not understand the full policy gets a clear error instead of a weaker policy.
- Isolation comes from an HSM policy you review and approve on the device at the first Play (max sats leaving, transactions per period, fee per vbyte, minimum round inputs). Signing then runs under that policy without touching the device. HSM commands have to be enabled on the device (Settings > Advanced/Tools > Spending Policy > HSM Mode).
- **Coldcard Q: not supported, and do not flash this firmware.** The Q has no HSM mode, so this build refuses it, and an earlier experimental Q build bricked a device: a validly signed image that fails before login is unrecoverable on a retail Q.

### Foundation Passport Prime — protocol v2, nothing verified on hardware yet
- Dev demo: [Coinjoin Signer on Foundation's app showcase](https://foundation.xyz/app-showcase/coinjoin-signer). The app and its wire protocol live in the `coinjoin.nl` repository under `passport/coinjoin-signer`.
- The wallet speaks the app's **wallet-rpc protocol v2**: the session you approve on the device is a random token every request carries, the fee budget you approve is the **total for the whole session** (the wallet composes it as your fee-rate cap applied to every round of your round budget), the session lasts 12 hours, and taproot is signed when the firmware says it can.
- **Stock KeyOS is not compatible.** The app carries the USB HID transport, but KeyOS reserves the USB interface messages for Foundation-signed apps, so a sideloaded app on a retail Prime cannot open it yet; a request to allow it is filed with Foundation (see `docs/keyos-usbdev-request.md` in the app repository). The wallet matches the Prime's own USB identity and picks the wallet-rpc interface by its HID usage page, on Windows, Linux and macOS. Until KeyOS grants the interface, nothing can be run against a Passport; treat this integration as a developer demo whose wire format is pinned by tests on both sides.

### Krux and SabiSigner — via kruxd
- Requires the `kruxd` companion daemon from the `coinjoin.nl` repository running on the host (port 21326), driving either a Krux flashed with the coinjoin signing extension or a [SabiSigner](https://github.com/kravens/SabiSigner) (`kruxd.py sabi`). Update kruxd: this build reads the accounts over its new `/xpub` endpoint.
- The session policy and round budget are approved physically on the device; the wallet only connects to an already-authorized session, and refuses one the device has not approved or has used up.
- **Import from the connect screen:** while kruxd answers, the device is listed like any other. The import reads both accounts over the bridge, pinned to the fingerprint of the session the device approved. kruxd cannot make the device show an address, so the wallet shows the first receive address instead: **compare it in the device's own address explorer before funding**.
- A SabiSigner signs **taproot only** in a round, so imported wallets receive to taproot by default; coins of any other type stay out of rounds.
- Importing with the coinjoin box unchecked gives a plain watch-only wallet; re-import with it checked to coinjoin.

## What changed since Preview 9

- **Built on the official v2.8.5 release** (this is the `v2.8.5.10` build), not on a master snapshot. v2.8.5 is a security release: transactions that spend your coins are verified against forged signatures before the wallet believes them (the Electrum GHSA-6jp4-8v48-cc5v class of attack from a malicious server), Brotli decompression is disabled in the HTTP clients, and .NET 10.0.14 and NBitcoin 10.0.17 close disclosed dependency vulnerabilities. Also in it: the private payment coin selector, coinjoin payments below 10,000 sats are refused instead of getting stuck, a zero credential is never taken from a request that depends on the receiver, coinjoins that pay exactly the fees charged are accepted, the history refreshes when coinjoin costs are saved, wallet recovery keeps loading while filter headers catch up, peers whose handshake finishes after shutdown are disconnected, and the unreachable Nostr relays are replaced.
- **Trezor branch rebased** (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759) at `a7c07182e8`) on v2.8.5 without a code change; only upstream's new forged-transaction test was adapted to the branch's key-chain signing call. The signature verification above applies to device-signed coinjoins as well: a coinjoin the device signed verifies like any other transaction.
- **Coldcard, Passport Prime, Krux and SabiSigner** are the same code as Preview 9, restacked on the rebased Trezor branch.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, reading wallets written by Previews 1-4, and enabling coinjoin later for the vendor of the connected device.

## What changed since Preview 8

- **Built on upstream master as of 20 October 2026, after v2.8.4** (this is the `v2.8.4.9` build). Brings what was merged since Preview 8: PayJoin is removed, files and folders are opened without going through a shell, a coinjoin whose transaction leaves out one of your registered inputs gets a signature withheld so it cannot be broadcast, the Alices that signed are kept when another signature fails, the `setlabel` RPC, the Windows installer derives its component GUIDs from the install path, release builds no longer embed the checkout path, and on the coordinator side the configured blame-round input minimum is honoured, bans are persisted and the indexer is gone.
- **Trezor branch** (PR [#14759](https://github.com/WalletWasabi/WalletWasabi/pull/14759) at `de5afaf018`):
  - Play starts the coinjoin the same way for every wallet; there is no authorization dialog any more. The music box says "Confirm coinjoin on your device" while the device asks, and says why when the start stopped (no bridge, no device, not authorized).
  - The device is asked only when the wallet has something to mix: PlebStop and the other start checks run first, so an empty coinjoin account no longer prompts the device.
  - A device call that does not come back within five minutes is cut off, and the next start asks for a new authorization instead of the round hanging.
  - A Trezor whose bridge session another wallet's coinjoin holds is left alone instead of being taken over.
- **Coldcard: segwit rounds sign on `coinjoin-fw-2`.** The round handed to the device now carries every input's previous output, other participants' included. Without them the Mk4 answered "Signing failed late" on every round that had no taproot coin of ours, so a segwit-only Preview 8 wallet could not finish a round. Larger PSBTs take longer to sign (about 4 ms per byte on a Mk4), so a very large round can still outlast a coordinator's signing phase.
- **Coldcard: taproot coins carry their taproot derivation** (BIP-371) in that PSBT, inputs and change alike. Preview 8 sent taproot keys in the plain BIP-32 field, where the device does not look for its taproot coins.
- **Coldcard: a device that is not connected when you press Play** now says "Connect and unlock your device, then press Play" instead of "The device did not authorize".
- **Closing a wallet lets go of its device.** The Coldcard connection is closed, and a Passport session is revoked, when the wallet closes, instead of staying open until Wasabi exits.
- **The settings panel "What the device reports it is enforcing" is gone.** The Coldcard shows the policy on its screen when you approve it, the log records it, and the warning that your saved limits differ from what the device enforces stays.
- **Passport Prime:** requests to the device go one at a time (the inputs of a round ask for their ownership proofs in parallel, and nothing pairs a reply with its request), and a Passport that stops answering needs a new authorization, as a Coldcard does. Still not verified on hardware.
- **Krux and SabiSigner:** the fee-rate cap Wasabi enforces for them is read from the settings as they are now, so a change applies at once.
- **macOS:** the USB device a Coldcard or Passport is opened through is kept alive explicitly for as long as it is used.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, reading wallets written by Previews 1-4, and enabling coinjoin later for the vendor of the connected device. The Coldcard and wallet-settings fixes of Preview 8 are now part of the vendor branches themselves.

## What changed since Preview 7

- **Built on upstream master as of 14 October 2026, after v2.8.4** (this is the `v2.8.4.8` build). Brings what was merged since Preview 7: the v2.8.4 release notes, the BitBox02 Nova is recognized, the crash on opening Receive after a restart is fixed, a Send flow no longer stays alive through the event bus after it is closed, the JSON-RPC server's authentication path is hardened and its onion forwarding listens on loopback only, the coordinator checks every output script against the inputs of every round, the release pipeline is pinned (appimagetool and the AppImage runtime are hash-checked, runner images, actions and installer tools are fixed to known versions), and the Tor manager was prepared for picking free ports.
- **Trezor branch rebased** (PR #14759 at `b96c7b019b`) on that master. The only change was adapting the device-signing path to upstream's reworked request scheduling: the device still signs once up front, and the signatures still go out on the usual spread-out schedule. No behaviour changed for any device.
- **Coldcard: an unplugged device stops the coinjoin** (fork PR [#3](https://github.com/kravens/WalletWasabi/pull/3)). Before every round the device is asked whether its policy is still running; a Coldcard that is gone stops the coinjoin with "Connect and unlock your device, then press Play" instead of failing to register round after round with nothing shown. Play is offered at once. A Coldcard reboots when unplugged, so plugged back in it offers its saved policy at login: approve it there and Play continues without a new prompt, decline it and Play asks the device to approve the policy again. A HID failure while proving or signing is reported the same way. Verified on a Mk4: unplugged between rounds and during input registration, the coinjoin stops with that message; an earlier build continued without HSM mode after the policy was declined at login, which this build fixes. Not yet verified: an unplug during the signing phase, which still gets that round's inputs banned by the coordinator; the coinjoin stops right after that round.
- **Enabling coinjoin later works for a Coldcard.** A Coldcard wallet imported without coinjoin got "No Trezor device found" from "Enable coinjoin" in the wallet settings, since that button only knew the Trezor. It now finds the wallet's device by its fingerprint and enables coinjoin the way that device signs: a Trezor adds its coinjoin account as before, a Coldcard is switched to sign coinjoins from its existing account (it approves its policy when you first press Play), and a Passport or Krux is told to import the wallet again with coinjoin on. Verified on a Mk4.
- **No surprise restart after enabling coinjoin or taproot.** Wasabi used to close and reopen right after either button, which looked like a crash. It now says the change is saved and offers "Restart now" or "Later"; both can be enabled before one restart.
- **Passport Prime and Krux** are the same code as Preview 7, restacked on the rebased Trezor branch; so is the Coldcard code apart from the fixes above.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, and reading wallets written by Previews 1-4.

## What changed since Preview 6

- **Built on upstream master as of 11 October 2026** (this is the `v2.8.3.7` build). Brings what was merged since Preview 6: the network tip is taken from validated block headers rather than from what peers claim, Tor 0.4.9.13, coinjoin payments whose round failed after signing are retried, Wasabi restarts correctly, the main window no longer closes when Wasabi loses focus on macOS, "Select all" in Exclude Coins is disabled while coinjoining, the `excludefromcoinjoin` RPC works again, and a single-coin pocket is expanded in manual send.
- **The hardware-wallet code had a security review** of the whole preview stack; the points below are what it changed.
- **Trezor branch after review** (PR #14759 at `88e0f4a6a5`): the wallet looks a script's key up in its index instead of scanning every key for every input and output of a round, and a device acquired for an authorization you decline is released again, so neither the bridge session nor the device stays held.
- **Coldcard: taproot is opt-in.** A Coldcard wallet is imported with its segwit account only. "Enable taproot" in the wallet settings adds the BIP-86 account, after which taproot coins can be received and coinjoined; this needs the Edge firmware (`coinjoin-fw-2`). Not yet verified on a Mk4: the xpub read outside HSM mode, the signing time of the bigger PSBT a taproot round needs, and sending or showing an address for taproot coins.
- **Coldcard: the limits the device approved are the limits the client picks rounds by.** The device enforces the policy it approved until it reboots. A fee cap raised, or a minimum round size lowered, in the settings afterwards does not apply until the device approves a new policy; a cap lowered or a minimum raised applies at once. Preview 6 read the current settings, so an edited cap could have the device refuse a round after its inputs were registered, which gets them banned.
- **Coldcard: the minimum round size is checked against the fewest inputs a round can close with**, for blame rounds too, instead of the most it can take; and a handshake reply that does not fit its layout is refused before it is read.
- **Krux and SabiSigner:** a kruxd answer that is not JSON, lacks its fields, or names a script type this build does not know no longer stops the detection of every other device. Unknown script types are left out rather than treated as segwit, a bridge that stalls while answering is given up on, and an error page from whatever listens on the port is reported with its status.
- **Passport Prime:** a Passport that is attached but cannot be opened (no udev rule, another program holding it) no longer makes the connect screen fail for every other device; the reason is logged, and an import of it says what went wrong.
- **Every device: a round only ever touches coins the device can sign.** Coins of a type the device cannot sign are left out before a round is picked, and the round's outputs are kept to types the device can sign, so a wallet never mixes itself into coins its own device could not mix again. A SabiSigner signs taproot only; a Passport without taproot support, segwit only.
- **Carried along as before:** the peer fix from Preview 1, the Windows publish fix, the serial-port probe fix, and reading wallets written by Previews 1-4.

## What changed since Preview 5

- **Built on upstream master after v2.8.3** (this is the `v2.8.3.6` build), not on a release. Brings the fixes merged since v2.8.3: `Config.json` is no longer discarded on a syntax error, a newly created wallet no longer sits on the loading screen until the next block, any Tor socket error means Tor is not running, "Some funds are rejected" only shows when a coin was banned, the gap limit in the Resync Wallet dialog, the Send button in discreet mode, the Manual Control checkbox, the frozen exchange rate, coinjoin fees broken down into mining fee, wasted dust and payments, the "only use private funds for coinjoin payments" toggle, and the bounded credential requests.
- **Trezor branch after review** (PR #14759 at `229132a190`). For a Trezor coinjoin wallet:
  - Sending or showing an address while coinjoining no longer ends the coinjoin's device session. It used to cost the authorization, and a round past input registration got its inputs banned.
  - A spent or lost authorization (rounds used up, bridge restarted) is asked for again at the next start, instead of every round failing silently.
  - The device signs once at the start of the signing phase, then the signatures go out on the usual random schedule, so their timing no longer links your inputs.
  - Segwit change of a send is streamed with the right script type; this build sends from a coinjoin wallet with segwit change, which Preview 5 could not.
  - Adding the coinjoin account to a synced wallet rescans from the wallet's birth, so coins already in it (for example from Trezor Suite's coinjoin account) show up.
  - Signet talks to the device as Testnet, the account picked for a send can also pay its fee, and a send-all over both accounts says it cannot instead of quietly leaving the coinjoin coins out.
  - Two starts while the device asks give one prompt, and a stop while it asks keeps the coinjoin stopped.
  - Detecting or importing a device, and HWI signing with another Trezor, are refused while a wallet coinjoins with its device, and stopping one wallet no longer stops the bridge another wallet still coinjoins through.
- **A device wallet always coinjoins into itself.** Preview 5 refused to start when another output wallet was saved and greyed out the picker, so a wallet with an old setting could not coinjoin at all. The output wallet is now simply the wallet itself.
- **A spent authorization is not a refusal.** When the device stops signing because its authorization ran out or its session is gone, the next start asks for a new one, instead of the coinjoin stopping with "the device refused to sign". Every vendor's key chain reports this the same way.

## What changed since Preview 4

- **Rebuilt on Wasabi v2.8.3** (this is the `v2.8.3.5` build). Brings upstream's Taproot support for PayJoin, the stricter security controls (PayJoin over Tor only, compact-filter validation, net-group diversity), blame-round input verification, the fix for payments stalling when only non-private coins are banned, and the always-visible music box.
- **Rebuilt on the rebased Trezor branch** (PR #14759 on v2.8.3). A device authorization that times out no longer crashes the application, and the SLIP-25 coinjoin account is the wallet's own: enabling coinjoin on an imported Trezor later sets the only valid change type itself, and the RPC no longer asks for a restart.
- **Every device wallet records which vendor signs its coinjoins**, Trezor included, in one wallet-file field. Turning coinjoin off is that field going back to none. Wallets written by earlier previews are read as before: a SLIP-25 account means Trezor, the old Coldcard flag means Coldcard.
- **Coldcard: `coinjoin-fw-2` is required.** Earlier previews silently fell back to a weaker HSM policy when the firmware rejected a field; this build refuses and says so.
- **The peer fix from Preview 1 is still needed on v2.8.3** (a block download timeout must not disconnect a filter-serving peer) and is carried along.
- **A serial port HWI cannot open no longer counts as a device.** HWI lists any such port as a Jade with an error, so an unrelated USB-serial board made the connect screen claim a second hardware wallet was attached.
- **A device wallet only coinjoins into itself, and a refusal stops the coinjoin.** A device approves outputs to its own accounts only, so choosing another output wallet made the device refuse every round while the client kept retrying, one authorized round at a time. The output wallet is now fixed for device-signed wallets, and a refusal during a round stops the coinjoin with the reason on the status line.
- About 250 lines less than Preview 4 for the same behaviour: the vendor layer now builds on what the Trezor branch already answers instead of re-asking it.

## What changed since Preview 3

- **Rebuilt on the trimmed Trezor branch** (this is the `v2.8.2.4` build): the same behaviour as Preview 3 in about 600 fewer lines.
- **SabiSigner and Krux are found and imported from the connect screen** (and from `importhardwarewallet` over RPC) while kruxd answers, instead of by hand-editing the wallet file. Every bridge call has a timeout, an unapproved or spent session is refused before a round starts, and coins of a type the device will not sign stay out of rounds.
- **Passport Prime speaks protocol v2** of its coinjoin app, see the section above; the session is revoked, and the device zeroizes its keys, whenever the wallet lets go of it.
- **Coldcard and Passport share one HID transport**, so the Passport client gains Linux and macOS and loses its Windows-only copy.
- **The connect screen's transport check asks the right vendor.** It used to answer yes as soon as any vendor's transport was up, which hid the Trezor bridge warning.

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
