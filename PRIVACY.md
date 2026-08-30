# Privacy Policy for Backtrack

**Last updated:** August 2026

Backtrack and the Backtrack Stream Deck Plugin are designed with user privacy and local control as core principles.

---

### 1. No Data Collection or Telemetry
Backtrack does not collect, log, track, or transmit any personal information, telemetry, usage statistics, crash metrics, or analytics to any third-party or external servers.

---

### 2. Network Communication & Remote Features
Backtrack operates without central cloud infrastructure. Network activity is limited strictly to user-configured connections:

- **Local IPC & Stream Deck**: By default, communication between Backtrack and its Stream Deck plugin occurs exclusively over your local computer (`127.0.0.1` loopback WebSocket IPC).
- **Remote OBS Connection**: If you enable **"OBS is on a different PC"**, Backtrack communicates directly with the specified host address and port over your local area network (LAN) or private VPN/Tailscale connection.
- **Peer-to-Peer Clip Sharing**: If you enable **"Share my clips with another PC"**, Backtrack establishes direct, authenticated point-to-point connections between your paired devices on your local network or private VPN. Video streaming, transcoding, and clip transfers travel directly between your two machines without passing through any external cloud relays or servers.

---

### 3. Media Storage & Ownership
All screen recordings, replay buffers, audio files, bookmarks, metadata, and video clips remain entirely on your local storage devices (or transfer exclusively to your designated paired machines). Nothing is ever uploaded to cloud servers.

---

### 4. Open Source
Backtrack is fully open source. You can independently review the entire source code, network implementations, and RPC protocols at [https://github.com/ilyambr/backtrack](https://github.com/ilyambr/backtrack).
