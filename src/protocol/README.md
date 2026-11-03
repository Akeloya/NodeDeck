# NodeDeck Gateway Protocol 0.1

Status: **draft contract**, not yet implemented in the SDK. This document defines communication between a universal client and a gateway, independently of the implementation language and transport. “Must” indicates a mandatory compatibility requirement. Field names and enumeration values are case-sensitive.

## Documents

- [Messages, authentication, authorization, capabilities, and command execution](wire.md).
- [Declarative screen schema and client actions](ui.md).
- [Complete capabilities example](examples/capabilities.json).
- [Complete screen example](examples/screens.json).
- [Message exchange sequence](examples/session.json).
- [Initial .NET TCP and Bluetooth LE development profiles and run instructions](tcp-development.md).

## Connection flow

1. The client establishes a secure connection and calls `protocol.hello`.
2. Both parties negotiate a version; the client calls `auth.login` with a shared secret.
3. The client calls `capabilities.get` to obtain commands, limits, and additional endpoints.
4. The client calls `ui.get` to obtain screens, layouts, controls, and actions.
5. The client collects parameters from linked controls and calls `command.invoke`.
6. The gateway immediately returns `completed`, `rejected`, or `accepted` with an operation token.
7. For `accepted`, the client polls `operation.get` until a terminal state is reached; the action in the schema determines UI blocking.

## Version scope

The common JSON envelope, version negotiation, authentication and authorization, capabilities and screen retrieval, command invocation, and operation polling are mandatory. A gateway without screens may provide an empty UI. Any command may return `accepted`, so clients must support polling.

Images and other files are transferred through declared resource endpoints; RTSP and chats are declared as optional integrations. Providing a link does not imply that every client can open it. Version 0.1 defines no mandatory protocol for chats, event streams, file uploads, or camera control: these require separate named extension contracts. Clients skip unknown extensions.

Exact TCP framing, BLE GATT UUIDs and fragmentation, and HTTP routes must be defined in separate transport profiles before implementation. The JSON envelope does not define message boundaries in a byte stream. The profile must provide channel security; sending a secret over plaintext TCP does not conform to this contract.

## Implementation compatibility checks

- Before login, only `protocol.hello` and `auth.login` are available; other requests are rejected.
- Invalid secrets, expired sessions, and operation tokens belonging to another subject are rejected.
- The client handles unknown endpoints and unsupported controls correctly.
- All screen references to commands, controls, resources, and drawers resolve.
- Field values and scanning results become typed command arguments.
- `vertical`, `wrap`, `absolute`, pages, and both drawer sides are supported.
- Immediate responses require no polling; long-running operations complete through `operation.get`.
- Repeating `command.invoke` with the same identifier does not execute the action again within the deduplication window.
- Disconnection, errors, and waiting timeouts release the UI without falsely reporting success.

The JSON files in `examples` are consistent examples, not executable tests or formal JSON Schemas. These criteria do not imply that the corresponding ROADMAP milestones have been implemented.

In `session.json`, responses to `r3` and `r4` are omitted to avoid duplication: their `result` values are the complete contents of `capabilities.json` and `screens.json`, respectively. The array records a sequence; messages are transmitted individually over the network. Secrets, addresses, and tokens in the examples are illustrative, and `example.operator-chat/1` is a fictional contract.
