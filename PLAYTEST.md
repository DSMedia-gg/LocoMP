# LocoMP — Playtest guide

A friendly, step-by-step guide to installing and playing **LocoMP** (multiplayer for Derail Valley)
in its current **pre-alpha** state. If you were handed a `LocoMP` folder or a `LocoMP.zip` and told
"try this," start here.

> **Read this first — expectations.** LocoMP is early and under active development. It is good enough
> for two friends to load into the same world, see each other, and run trains together — which is
> exactly what this test is for. It is **not** finished, some things are rough, and you will hit bugs.
> That is the point of a playtest: finding them. See [Known rough edges](#known-rough-edges) before
> you decide something is "broken."

---

## 1. What you need

| Thing | Notes |
|---|---|
| **Derail Valley** (legal Steam copy) | Both players must be on the **same game build** — currently **B99.7** (`99-build2702`). If one of you is on the Steam beta branch and the other isn't, update so you match. |
| **Unity Mod Manager (UMM)** | The mod loader. One-time setup. [Download here](https://www.nexusmods.com/site/mods/21). |
| **The LocoMP mod folder** | The `LocoMP` folder from the zip you were sent. Both players install the **exact same build** — mismatched builds are refused at connect (cleanly, with a message; not a crash). |
| **A way to reach each other** | Same house/LAN is easiest. Over the internet you need a VPN overlay or a port-forward — see [Connecting over the internet](#4-connecting-over-the-internet). |

There is nothing to buy, no account, and no launcher. LocoMP runs entirely inside your normal game.

---

## 2. Install (do this once, on both PCs)

### 2a. Install Unity Mod Manager

1. Download and run **Unity Mod Manager**.
2. In UMM, pick **Derail Valley** from the game dropdown (it usually auto-detects the folder; if not,
   point it at your `Derail Valley` install folder).
3. Choose install method **DoorstopProxy** (the default) and click **Install**. UMM patches the game
   so mods load on launch. You only ever do this once.

### 2b. Drop in the LocoMP folder

1. Unzip the LocoMP folder if it came as a zip. You should have a folder named **`LocoMP`** containing
   `LocoMP.dll`, `Info.json`, a few other `.dll` files, and `LICENSE`.
2. Copy that whole **`LocoMP` folder** into the game's **`Mods`** folder:

   ```
   <your Steam library>\steamapps\common\Derail Valley\Mods\LocoMP\
   ```

   The path must end up `...\Mods\LocoMP\Info.json` — i.e. the `LocoMP` folder sits directly inside
   `Mods`, not double-nested (`Mods\LocoMP\LocoMP\...` is wrong).

   > Not sure where the game is installed? In Steam: right-click **Derail Valley → Manage → Browse
   > local files**.

3. Launch the game. During the loading screen you'll briefly see the UMM overlay list its mods.
   **LocoMP** should be in the list and switched **on**. You can open the UMM overlay any time in-game
   with **`Ctrl`+`F10`** to confirm it loaded (and toggle it).

That's it — you're installed. The only visible sign in a normal session is a new **MULTIPLAYER**
button that LocoMP adds to the game's main menu and pause menu.

---

## 3. Your first session (same PC to test, then LAN)

The golden rule: **one person hosts, everyone else joins.** The host's loaded world *is* the shared
world — everyone plays in the host's save.

Both hosting and joining need a **world already loaded** — LocoMP hooks into your live game, so you
start from *inside* your session, not from the main menu.

### Host (Player A)

1. Start the game and **load a save** (Continue, or load/create a career) so you're standing in the
   world as normal.
2. Press **`Esc`** to open the pause menu → click **MULTIPLAYER**.
3. Go to the **Host** tab and fill in:
   - **Your name** — how you'll show up to others.
   - **Port** — leave at **`8877`** unless you have a reason to change it.
   - **Password** — optional; leave blank for an open session.
   - Everything else has sane defaults (see [Host options](#host-options-explained)).
4. Click **Host session**. The overlay closes and you're back in your world — now hosting. Your world
   keeps running; opening LocoMP menus never pauses a live session.

> The **Host** button is greyed out until a world is loaded. If you see *"Load your world first…"*,
> you're still at the main menu — Continue into a save, then reopen with `Esc` → MULTIPLAYER.

### Join (Player B)

1. Start the game and **load a save** (any save — you'll be dropped into the host's world, but the
   mod still needs your game to be live first).
2. Press **`Esc`** → **MULTIPLAYER** → **Direct Join** tab.
3. Fill in:
   - **Your name**.
   - **Address** — the host's IP. `127.0.0.1` for the same PC; the host's LAN IP (e.g. `192.168.x.x`)
     on the same network; or the host's VPN/overlay IP over the internet (see next section).
   - **Port** — `8877` (must match the host).
   - **Password** — only if the host set one.
4. Click **Join**. A loading cover appears while you connect and sync; when it clears you're in the
   host's world and should see each other.

Your last five servers are remembered under **Recent** — one click refills the address and port next
time. (Names and addresses persist between runs; passwords are never saved.)

### Proving the install before you involve a friend

You can confirm the mod **loaded** on your own PC without anyone else: launch, load a save, and check
that the **MULTIPLAYER** button appears in the pause menu (and that LocoMP shows **on** in the
`Ctrl`+`F10` overlay). Real multiplayer needs two machines, though — the simplest end-to-end test is
**two PCs on the same network**: one hosts, the other Direct-Joins the host's `192.168.x.x` LAN IP on
port `8877`. Once that works locally, moving to the internet is just swapping in an overlay IP
(Section 4).

---

## 4. Connecting over the internet

**Important:** LocoMP currently connects by **direct IP and port (UDP 8877)** only. The "Friends via
Steam" tab you'll see in the menu is **not active yet** — it's reserved for a later version. So to
play with someone who isn't on your local network, you have two options:

### Option A — a VPN overlay (recommended, easiest)

Install something like **Tailscale**, **ZeroTier**, or **Radmin VPN** on both PCs. These put both
machines on one virtual LAN with no router configuration.

1. Both players install the overlay and join the same network/tailnet.
2. The **host** finds their overlay IP (e.g. Tailscale shows a `100.x.y.z` address).
3. The **joiner** uses that overlay IP as the **Address**, port `8877`.

This is the least-hassle path and what we recommend for a first test.

### Option B — port-forwarding (host only)

If you'd rather not use an overlay, the **host** forwards **UDP port 8877** on their router to their
PC's local IP, and allows it through their firewall. The joiner then connects to the host's **public
IP** (search "what is my IP"). Only the host forwards a port; joiners never need to.

> Whichever option you pick, only the **host** needs to be reachable. Joiners just need to be able to
> reach the host's address.

---

## 5. Host options explained

Defaults are fine for a first test — but here's what each Host-tab control does:

| Option | Default | What it does |
|---|---|---|
| **Port** | `8877` | UDP port the session listens on. Joiners must use the same number. |
| **Password** | *(open)* | Blank = anyone with your address can join. Set one to gate it. |
| **Max players** | `32` | Admission cap. A full server now queues extra joiners in a real waiting line rather than bouncing them. |
| **Autosave (seconds)** | `120` | How often the host's world is saved. Floored so you can't accidentally hammer the disk. |
| **Shared career (classic co-op)** | off | On = everyone shares one career/economy (classic co-op). Off (default) = each player keeps their **own** progression, money and licenses. |
| **Fresh career (ignore saved)** | off | Start the session from a clean career instead of your saved one. |
| **Auto-grant my licenses to joining players** | off | Joiners inherit the host's held licenses while enabled — handy so a new tester isn't blocked by locked jobs/regions. |
| **Only stream nearby trains/players** | off | Bandwidth saver — only replicates things near you. Leave off for a small test. |

---

## 6. Known rough edges

This is pre-alpha. None of the below is news to us — no need to report them unless they're worse than
described:

- **Steam "Friends" join isn't wired up yet.** Use Direct Join with an IP (Section 4).
- **You must be in a loaded world to host or join.** You can't start a session from the main menu; the
  menu will tell you so.
- **Train coupling gaps can look slightly off.** Streamed consists may sit a touch wide or compressed
  between cars. Cosmetic; being tuned.
- **Build/version must match exactly.** If a join is refused, the most common cause is one side on a
  different LocoMP build or a different Derail Valley build. The refusal message will say so — it's a
  clean reject, not a crash.
- **Expect the occasional desync or hitch.** This is what the test is for. If something looks wrong,
  a rejoin often clears it.

---

## 7. Reporting bugs (this is the valuable part)

When something goes wrong, the logs are gold. Please grab:

1. **The game log** — `output_log.txt`, in the game folder under
   `Derail Valley\DerailValley_Data\output_log.txt` (or `...\Player.log` depending on build). Copy it
   right after the problem, before relaunching.
2. **The UMM log** — open the UMM overlay with **`Ctrl`+`F10`** and check the **Logs** area, or grab
   `Derail Valley\Mods\UnityModManager.log`.
3. **What you did** — "I was hosting, my friend joined, we coupled two locos, his end froze." A short
   sequence beats a screenshot.

For deeper detail there's a built-in dev panel (also **`Ctrl`+`F10`** area / LocoMP's own overlay) that
shows live session state — handy if you're comfortable poking around, optional otherwise.

Send the log(s) + a one-line description back to whoever gave you this. Thank you — genuinely, a good
bug report is the most useful thing you can produce right now.

---

## Reminders

- LocoMP is an independent, fan-made project. It is **not** affiliated with, authorized, or endorsed by
  **Altfuture s.r.l.**, the developers of Derail Valley.
- You need your own legal copy of Derail Valley. LocoMP ships no game code or assets.
- Parts of LocoMP are developed with AI assistance under human direction and review.

Happy shunting. 🚂
