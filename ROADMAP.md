# NodeDeck Roadmap

> **Status** — Development plan. Each phase ends when it meets its acceptance criteria. There are no fixed deadlines.

NodeDeck is an open, offline-first protocol and a set of implementations that expose commands, state, events, and dynamic control screens. A single mobile client can operate different systems.

Each application or device embeds a gateway that publishes its own capabilities. In a later phase, one gateway will be able to connect to another and aggregate its commands and state. Local commands work independently of the internet and Telegram.

## Priorities

1. **P0 — WPF MVP.** Replace the Bluetooth keyboard with a mobile client that works over Wi-Fi and Bluetooth LE. Test it with a real application that has many screens and commands.
2. **P1 — ESP32-S3 MVP.** Learn ESP-IDF and control a lighting and gate prototype locally over Wi-Fi. Then integrate it safely with the existing gate drive.
3. **P2 — Gateway aggregation.** Connect ESP32 devices, Raspberry Pi devices, and WPF applications through an aggregating gateway.
4. **P3 — Internet and Telegram.** Add secure remote access, followed by an adapter for the existing Telegram gateway.

WPF and ESP32 development can proceed in parallel once the minimal protocol is stable. Work on the WPF MVP can proceed while the ESP32 board is in transit.

## Phase 0 — Minimal protocol contract

- [ ] Define the protocol version and message format (`requestId`, `type`, `payload`, and `result` or `error`).
- [ ] Describe registered commands, arguments, state, and events in `capabilities`.
- [ ] Define a declarative UI schema for screens, groups, buttons, switches, and indicators. Give commands stable identifiers independent of button placement.
- [ ] Define the screen description, dynamic command availability, and execution-time permission checks separately.
- [ ] Define client identification, device pairing, permissions, access revocation, and confirmation for hazardous actions.
- [ ] Define `accepted`, `completed`, and `rejected` responses, errors, timeouts, and protection against executing a command twice with the same `requestId`.
- [ ] Prepare JSON test messages and a shared conformance suite for .NET and ESP32.

**Acceptance criteria.** A demo client can retrieve the schema and current state and execute a command through a stable, documented contract. The gateway rejects unknown commands and invalid arguments.

## Phase 1 — WPF MVP over Wi-Fi (P0)

- [ ] Implement `NodeDeck.Core` and embeddable `NodeDeck.AspNetCore` / `NodeDeck.Hosting` for .NET 8+.
- [ ] Add command registration, asynchronous handlers, and publication of state and events.
- [ ] Connect the existing WPF shortcut infrastructure to the same application handlers used by buttons and hotkeys. Run UI operations through the `Dispatcher`.
- [ ] Implement local HTTP and WebSocket/SignalR transport and client reconnection.
- [ ] Build a minimal Android client with a gateway list, dynamic screens, buttons, current state, and error reporting.
- [ ] Pair devices initially using a QR code containing a short-lived, single-use token.
- [ ] Store UI schemas in JSON initially. Defer the visual editor until after the MVP if needed.
- [ ] Test multiple screens, visually identical actions mapped to different commands, WPF context changes, and unavailable commands.
- [ ] Test on a local network with internet access disconnected.

**Acceptance criteria.** An Android user can select screens and reliably execute real WPF commands without keyboard emulation. Local control continues when internet access is lost.

## Phase 2 — WPF MVP over Bluetooth LE (P0)

- [ ] Investigate BLE GATT roles and test the Bluetooth adapters on the Windows computers that run WPF before choosing the final topology.
- [ ] Implement a BLE transport that uses the shared command and state models independently of Wi-Fi.
- [ ] Add message fragmentation, size limits, timeouts, delivery acknowledgments, and reconnection.
- [ ] Add secure device pairing, authentication, and protocol-level protection against replayed commands.
- [ ] Allow switching between Wi-Fi and BLE while retaining the current screen and command identifiers.
- [ ] Test several physical Android devices under normal WPF application operating conditions.
- [ ] Document Android background execution and notification delivery limits, including the lack of guaranteed delivery while disconnected.

**Acceptance criteria.** With Wi-Fi completely unavailable, a connected phone controls the same WPF commands over BLE. Retransmitting a packet does not accidentally execute the action twice.

## Phase 3 — ESP32-S3 platform bring-up and local MVP (P1)

### 3.1. Test bench

- [ ] When the ESP32-S3 arrives, install **ESP-IDF**, build, flash, and run `hello_world`, and learn to use the serial monitor, logs, and debugger.
- [ ] Explore GPIO inputs and outputs, safe output levels at startup and reset, and the watchdog.
- [ ] Build a low-voltage prototype with LEDs or a relay module. Keep the gate and mains voltage disconnected.
- [ ] Connect the ESP32-S3 to the local Wi-Fi network and start a minimal HTTP/WebSocket service.

### 3.2. NodeDeck Embedded Gateway

- [ ] Implement the minimal command, state, and UI schema contract in C/C++ using ESP-IDF.
- [ ] Implement `light.on`, `light.off`, and `gate.trigger` on the **simulator**, with `light.state` and `gate.state` reporting their state.
- [ ] Pass the same protocol conformance tests as the .NET gateway.
- [ ] Test the Android client against the ESP32 using the standard screens and client logic.
- [ ] Add local authorization, command rate limits, and safe output states on failure.

### 3.3. Physical gate and lighting

- [ ] Review the gate controller documentation for its external control input, status signals, and safety requirements.
- [ ] Connect the ESP32 only through the designated control interface, with the required galvanic isolation and electrical protection. Keep the drive controller and its safety circuits in place.
- [ ] Preserve the existing controls, limit switches, photocells, obstacle protection, and manual release.
- [ ] Define when remote commands are allowed and how they are confirmed. Where sensors are available, distinguish command acceptance from confirmation that the gate has opened or closed.
- [ ] Test power loss, restart, disconnection, duplicate commands, and invalid sensor states.
- [ ] Switch mains lighting using certified switching devices rated for the load and follow electrical safety requirements.

**Acceptance criteria.** The same Android client controls the local ESP32 gateway, and the prototype passes its tests consistently. Connect to the physical gate only after verifying the existing safeguards and safe behavior on failure. This phase works offline.

> The ESP32-S3 supports Wi-Fi and Bluetooth LE. It lacks Bluetooth Classic support. BLE on ESP32 is a separate, optional phase that can follow the local Wi-Fi MVP.

## Phase 4 — Gateway-to-gateway communication and Raspberry Pi (P2)

- [ ] Define a Gateway Client API for discovery, connection, `capabilities` retrieval, state subscriptions, and remote command invocation.
- [ ] Implement an aggregating gateway in .NET for Windows and Linux, and verify deployment on Raspberry Pi.
- [ ] Use stable addresses such as `gatewayId/commandId` to distinguish commands from different devices.
- [ ] Forward state and errors with their source and mark disconnected nodes as `offline`.
- [ ] Prevent routing loops and check permissions on every routed invocation.
- [ ] Test **WPF ↔ Gateway ↔ ESP32**, then add Raspberry Pi.

**Acceptance criteria.** One client can see multiple nodes and their screens through the aggregator. Each node also remains directly accessible.

## Phase 5 — Remote access over the internet (P3)

- [ ] Choose a secure remote access channel, such as a VPN or an outbound connection to a trusted server. Keep the unsecured ESP32 API off the public internet.
- [ ] Add roles, key and certificate management, action auditing, and device revocation.
- [ ] Define gate-specific restrictions for remote commands, confirmation requirements, and behavior when reliable state is unavailable.
- [ ] Test slow and unstable connections, delays, duplicate messages, and lost acknowledgments.

**Acceptance criteria.** Authorized users can control permitted devices remotely. Local access continues when the internet connection fails.

## Phase 6 — Optional Telegram adapter (P3)

- [ ] Document the existing Telegram gateway and its limitations.
- [ ] Implement a Telegram ↔ NodeDeck adapter through the shared Command API so each device uses its existing command logic.
- [ ] Map Telegram users and chats to NodeDeck permissions and enforce a strict command allowlist.
- [ ] Add confirmation for critical actions, replay protection, timeouts, and logging.
- [ ] Add events, notifications, and a message queue if needed. Report delivery failures distinctly from successful command execution.

**Acceptance criteria.** Telegram invokes the same commands with the same checks as the local client. Gateways continue to operate when Telegram is disconnected.

## Backlog

- [ ] Build a visual UI schema editor. Choose between WPF, Avalonia, and the web later.
- [ ] Support screen schema export, import, and versioning. Validate schemas against the commands actually registered.
- [ ] Add more controls, localization, and themes.
- [ ] Add application notifications, event history, and operator acknowledgment.
- [ ] Support photo and file transfers and chat integration through interchangeable providers.
- [ ] Implement a full ESP32 BLE transport if use cases require it.
- [ ] Publish the SDK on NuGet and provide integration documentation, examples, and automated releases.

## Project principles

- **Protocol first.** .NET and ESP-IDF have independent implementations with a shared contract and conformance tests. The .NET implementation must be usable without a C++ wrapper.
- **Offline first.** Local commands work independently of the cloud, Telegram, and any shared internet gateway.
- **Security by default.** The gateway checks availability and permissions on each invocation, regardless of what the UI displays.
- **Safety first.** The gate's existing hardware safeguards provide physical protection. NodeDeck must preserve them and must never bypass them.
- **Thin clients.** Android renders the supplied schema. The gateway owns commands and rules.
- **Incremental delivery.** Test each MVP on real hardware and exercise hazardous operations on a prototype first.

## References

- [ESP-IDF getting started guide for ESP32-S3](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/get-started/start-project.html)
- [ESP32-S3 hardware overview](https://www.espressif.com/products/socs/esp32-s3)
- [ESP-IDF HTTP Server](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-reference/protocols/esp_http_server.html)
- [ESP-IDF NimBLE GATT Server example](https://github.com/espressif/esp-idf/tree/master/examples/bluetooth/ble_get_started/nimble/NimBLE_GATT_Server)
