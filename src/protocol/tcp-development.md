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

On the Android connection screen select the Bluetooth development profile, tap **Connect**, and grant Nearby devices permission. The phone advertises the NodeDeck GATT service; the Console app scans and connects as a BLE central. The computer and phone do not need to be on the same Wi-Fi network. The Windows listener tracks connections per BLE address and can accept multiple phones, subject to the adapter's capacity. Each phone accepts one gateway at a time. The Visual Studio launch profiles `NodeDeck.Console - TCP` and `NodeDeck.Console - Bluetooth LE` select those transports under the debugger. Starting without debugging opens a console menu.

The development BLE GATT service UUID is `a1b2c3d4-e5f6-4a1b-8c9d-001122334455`; each protocol envelope is UTF-8 JSON terminated by `\n`, fragmented into 20-byte GATT writes/notifications and reassembled at the other end.

### Listener profiles

The three JSON launch profiles load the example files from the console project's `Settings` directory. Run one from the repository root with:

```powershell
dotnet run --project src/examples/dotnet-console/NodeDeck.Console --launch-profile "NodeDeck.Console - TCP (JSON)"
dotnet run --project src/examples/dotnet-console/NodeDeck.Console --launch-profile "NodeDeck.Console - Bluetooth LE (JSON)"
dotnet run --project src/examples/dotnet-console/NodeDeck.Console --launch-profile "NodeDeck.Console - TCP + Bluetooth LE (JSON)"
```

The combined profile passes both files to `--settings`. The gateway accepts TCP connections while the BLE scanner looks for a phone. Relative settings paths are resolved from the working directory first and then from the directory containing the executable.

Use `--save-settings` to write the selected listener profile and exit. Then use `--settings` to start the gateway with that JSON file. Keep supplying the shared secret separately with `--secret`.

```powershell
dotnet run --project src/examples/dotnet-console/NodeDeck.Console -- --transport tcp --bind 127.0.0.1 --port 5050 --save-settings listener.json
dotnet run --project src/examples/dotnet-console/NodeDeck.Console -- --settings listener.json
```

The `Type` field selects `tcp` or `bluetooth`. Use `--transport bluetooth --save-settings listener.json` to generate a profile containing the GATT UUIDs, scan mode, and write chunk size. Both profiles support `InitialMaxMessageBytes` before protocol negotiation and a per-listener `LogLevel` of `Trace`, `Debug`, `Info` (default), `Warn`, `Error`, `Fatal`, or `Off`. The default NLog configuration sends matching listener events to the console.

The initial BLE characteristics use the plain GATT protection level and do not require bonding/encryption. Use only a temporary development secret while testing; this profile is not suitable for production credentials.

In the Android app, select the Wi-Fi development profile and tap **Configure** to change its saved host, port, or secret. A physical phone and computer must be on the same Wi-Fi network; use the computer's LAN IPv4 address (for example `192.168.1.20`), and allow inbound TCP port `5050` in the computer firewall. For an Android emulator connecting to a gateway on its host computer, use `10.0.2.2`.

On Windows, the Android emulator build requires an installed Android SDK and MAUI Android workload. The console gateway can be run on Windows, Linux, or macOS with the .NET 10 SDK.

## Device identification and reconnecting

Configure `ServiceUuid`, `ClientToGatewayCharacteristicUuid` and `GatewayToClientCharacteristicUuid` in the gateway's `Settings/bluetooth.json`. Set the same three distinct UUIDs in the phone's Bluetooth profile settings. The service UUID selects a discovery group; the characteristic UUIDs define the two message directions. Use different service UUIDs to separate nearby gateway groups. The current BLE topology is unchanged: the phone advertises and Windows scans.

Use `--gateway-id warehouse-1 --gateway-name "Warehouse gateway"` to configure the identity returned by `protocol.hello`. Set **Expected gateway ID** in the phone profile to reject an unintended gateway before sending the shared secret. This comparison is an identity check, not cryptographic authentication. For TCP, the phone still uses a configured IP/hostname and port; it performs no Wi-Fi broadcast discovery. The expected gateway ID check applies to both transports.

Android uses one persisted installation ID across all connection profiles. A successful login from that installation ends its previous gateway session. On logout, both ends release their BLE resources, and the Windows scanner remains available for the next connection. The phone stops advertising after a gateway subscribes, then starts fresh advertising on the next connection attempt.

## Executor and node configuration

Both roles are constructed by `GatewayFactory` and run through `IGateway`. Use a complete JSON file with `--gateway-settings`. The `Mode` is `Executor` or `Node`; `GatewayId`, `Name`, `Description`, `SharedSecret` and `Listeners` configure either role. A node additionally has `Gateways`, a list of peer IDs, fallback names/descriptions, TCP addresses/ports and authentication secrets.

Run these in separate terminals from the repository root:

```powershell
dotnet run --project src/examples/dotnet-console/NodeDeck.Console -- --gateway-settings Settings/gateway-executor.json
dotnet run --project src/examples/dotnet-console/NodeDeck.Console -- --gateway-settings Settings/gateway-node.json
```

The supplied executor listens on loopback port 5051. The node listens on port 5050 and offers a connection command for that executor. To use another machine, change the executor's bind address and the node's peer `Host`. To accept phones over BLE, add a listener object with `Type: "bluetooth"` and the UUID settings to the node's `Listeners`. Node-to-peer connections currently use TCP. Add further executors or nodes to `Gateways`; IDs must be globally unique, and a node cannot list itself or duplicate peer IDs.

The phone authenticates to the node with the node's secret and expected gateway ID. It receives peer connection buttons with names and descriptions; selecting one replaces the displayed catalog with that gateway's commands. A return button goes back to the node directory. Different phones can select different gateways, even when those gateways expose identical command IDs. After a runtime metadata change, the node refreshes its directory on the next health check (default 30 seconds); a phone sees changes on its next catalog refresh.

The node never reroutes a failed command to another gateway. Forwarded requests retain their IDs and carry visited gateway IDs plus a hop budget; backward routes and loops are rejected. `RouteTimeout` defaults to 15 seconds per request and can be configured up to one minute. A timeout can leave a command's outcome unknown, so reconnecting never replays it. Directory probes reconnect independently. `ConnectedGateways` exposes the peers currently known to be connected.

Set default executor metadata in JSON or, without a complete settings file, use `--gateway-name` and `--gateway-description`. Call `IGateway.UpdateMetadata(name, description)` to override both at runtime. The gateway ID stays stable.

Each gateway logs active sessions every minute and expires sessions after five minutes without heartbeat responses. Keep gateway and phone updated together. Hardware verification: connect via BLE, leave the session, reconnect several times; switch the same phone between BLE and TCP; repeat with two phones; disable the phone's radio or suspend the app, then confirm session cleanup and reconnection. Automated TCP tests cover the protocol and node lifecycle, but cannot verify a particular BLE adapter/phone combination.
