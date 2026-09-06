# Privacy Policy for Backtrack

**Last updated:** September 2026

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
All screen recordings, replay buffers, audio files, bookmarks, metadata, and video clips remain entirely on your local storage devices (or transfer exclusively to your designated paired machines). Nothing is ever uploaded to cloud servers without your explicit action.

---

### 4. Google Drive Integration (Optional)
Backtrack includes an optional feature allowing you to upload clips directly to your personal Google Drive account.

- **Scopes Requested**: Backtrack requests the `https://www.googleapis.com/auth/drive.file` and `https://www.googleapis.com/auth/drive.metadata.readonly` scopes.
- **Access & Data Usage**: Backtrack only accesses files and folders created by the app or explicitly selected by you. It reads folder metadata solely to let you navigate and select a destination folder in your Drive. Your email address is fetched once to display which account is signed in (and can be masked via Streamer Mode).
- **Local Credential Storage**: OAuth 2.0 access and refresh tokens are stored exclusively on your local computer, encrypted with the Windows Data Protection API (DPAPI). Backtrack has no servers or telemetry; your credentials and videos never pass through any third-party or developer servers.
- **Data Sharing & Sale**: Backtrack does not share, transfer, or sell your Google user data to any third party. Video uploads are sent directly from your computer to Google's official Drive API endpoints.
- **Revocation**: You can disconnect your Google account at any time by clicking "Sign out" within Backtrack's Google Drive dialog, or by revoking access in your [Google Account Security Settings](https://myaccount.google.com/permissions).
- **Google Limited Use Policy Compliance**: Backtrack's use and transfer to any other app of information received from Google APIs will adhere to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.

---

### 5. Open Source
Backtrack is fully open source. You can independently review the entire source code, network implementations, and RPC protocols at [https://github.com/ilyambr/backtrack](https://github.com/ilyambr/backtrack).
