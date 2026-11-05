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

## Roadmap

See [ROADMAP.md](ROADMAP.md) for the development plan,
milestones, and implementation priorities.
