# TCP development profile

The first .NET demo uses newline-delimited UTF-8 JSON over TCP: each envelope is one JSON object followed by `\n`. This framing is for local development and is not a production transport profile. It does not provide TLS or peer authentication. Do not expose this listener to the internet or use a real shared secret on an untrusted network.

Run the console gateway from the repository root:

```powershell
dotnet run --project src/examples/dotnet-console/NodeDeck.Console -- --bind 0.0.0.0 --port 5050 --secret nodedeck-dev
```

The demo publishes `phone.hello`, `phone.status`, and `phone.action`. Each accepts `{ "message": "..." }`; the console prints the invoked command and the client receives a reply. Choose a different secret for your local network with `--secret`.

For Bluetooth LE on Windows, run:

```powershell
dotnet run --project src/examples/dotnet-console/NodeDeck.Console -- --transport bluetooth --secret nodedeck-dev
```

In the phone app select **Bluetooth LE**, grant Nearby devices permission, and tap **Connect**. The phone advertises the NodeDeck GATT service; the Console app scans and connects as a BLE central. The computer and phone do not need to be on the same Wi-Fi network. The initial profile supports one phone connection at a time. The Visual Studio launch profiles `NodeDeck.Console - TCP` and `NodeDeck.Console - Bluetooth LE` select those transports under the debugger. Starting without debugging opens a console menu.

The development BLE GATT service UUID is `a1b2c3d4-e5f6-4a1b-8c9d-001122334455`; each protocol envelope is UTF-8 JSON terminated by `\n`, fragmented into 20-byte GATT writes/notifications and reassembled at the other end.

The initial BLE characteristics use the plain GATT protection level and do not require bonding/encryption. Use only a temporary development secret while testing; this profile is not suitable for production credentials.

In the Android app, enter the gateway address and port. A physical phone and computer must be on the same Wi-Fi network; use the computer's LAN IPv4 address (for example `192.168.1.20`), and allow inbound TCP port `5050` in the computer firewall. For an Android emulator connecting to a gateway on its host computer, use `10.0.2.2`.

On Windows, the Android emulator build requires an installed Android SDK and MAUI Android workload. The console gateway can be run on Windows, Linux, or macOS with the .NET 10 SDK.
