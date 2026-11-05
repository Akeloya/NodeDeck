# NodeDeck

Build once. Control anywhere.

NodeDeck is an open-source, platform-independent gateway framework that enables applications and embedded devices to expose commands, states, events, and dynamically generated control interfaces to universal clients.

Built with an offline-first approach, NodeDeck supports direct communication over local networks and Bluetooth, without requiring cloud infrastructure.

Gateways can connect to other gateways, allowing distributed systems built with .NET, ESP32, Raspberry Pi, and other platforms to be managed through a unified interface.

## Project phylosophy

- There is no mandatory central server.
- There is no dependence on the Internet.
- There is no dependence on a specific programming language.
- Gateways can provide functionality on their own.
- Gateways can be combined with each other.
- Any compatible client can display the screens they provide.

## Gateway protocol

See [src/protocol/README.md](src/protocol/README.md) for the draft gateway protocol,
authentication, capabilities, UI schemas, and command execution examples.

## Client localization

The MAUI client uses English as its neutral language and includes Russian translations.
The system UI language selects the translation; other languages fall back to English.
UI strings live in `src/apps/android/NodeDeckClient/Resources/Strings/AppResources.resx`
and `AppResources.ru.resx`, with matching properties in `AppResources.cs`.
Built-in connection names are localized when displayed, including previously saved
Russian names. Custom connection names retain their original text.

## Bluetooth LE troubleshooting on Windows

NodeDeck's Bluetooth transport requires Bluetooth Low Energy (BLE). If your adapter
appears as **Generic Bluetooth Adapter** in Windows Device Manager, the installed
generic driver may not provide working BLE support, even if Bluetooth is enabled.

If the Bluetooth connection does not work or times out, install the appropriate
Windows driver from the adapter manufacturer's official website, then reconnect
the adapter and try again. Windows may not automatically download or prompt you
to install the required driver when the adapter is first connected. The adapter
hardware must also support BLE; installing a driver cannot add BLE to a
Bluetooth Classic-only adapter.

## Roadmap

See [ROADMAP.md](ROADMAP.md) for the development plan,
milestones, and implementation priorities.
