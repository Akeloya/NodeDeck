# Gateway communication

## 1. Common envelope

A message is a UTF-8 JSON object. For each valid request message, the gateway returns one response with the same `requestId`, `type`, and `protocolVersion`. Responses may arrive in a different order than requests. JSON with duplicate keys is invalid.

```json
{
  "protocolVersion": "0.1",
  "requestId": "req-101",
  "type": "command.invoke",
  "accessToken": "session-secret",
  "payload": {
    "commandId": "inventory.lookup",
    "arguments": { "barcode": "4601234567890", "quantity": 2 }
  }
}
```

`requestId` is a nonempty string of up to 128 characters, unique for each new attempt within the authenticated subject/device scope. A random UUID is recommended. `payload` is required, including when it is `{}`. `accessToken` is omitted before authentication and on gateway-originated heartbeat requests. Each new status query uses a new `requestId`; retrying a command whose response was lost uses the original identifier.

A response contains exactly one of `result` or `error`; `payload` and `accessToken` are absent:

```json
{
  "protocolVersion": "0.1",
  "requestId": "req-101",
  "type": "command.invoke",
  "result": { "status": "completed", "output": { "found": true } }
}
```

```json
{
  "protocolVersion": "0.1",
  "requestId": "req-101",
  "type": "command.invoke",
  "error": {
    "code": "unauthorized",
    "message": "Authentication required",
    "retryable": false
  }
}
```

Envelope errors, authentication or authorization errors, and unknown `type` values are returned through `error`. Rejection of a correctly addressed command uses `result.status = rejected` with a nested `error`. The transport profile handles malformed JSON from which an identifier cannot be extracted; the gateway does not invent a correlation identifier.

Unknown optional fields are ignored. Unknown request types return `unsupported_type`. Changing the meaning of existing fields requires a new negotiable version. Numbers must be finite; integers are restricted to ±9007199254740991, and longer identifiers are represented as strings. Dates are UTC strings such as `2026-11-02T15:00:00Z`; intervals with the `Ms` suffix are in milliseconds.

## 2. Negotiation, authentication, and authorization

`protocol.hello` uses envelope version `0.1` as the initial version:

```json
{
  "supportedVersions": ["0.1"],
  "client": { "id": "phone-1", "name": "Android client" }
}
```

The result contains `selectedVersion`, `gatewayId`, `authMethods: ["sharedSecret"]`, and `maxMessageBytes`. The gateway selects a common version or returns `unsupported_version`. Only `hello` is allowed before `hello` completes. Until the limit is received, the maximum message size is 16384 bytes; afterwards, the advertised limit applies to the entire UTF-8 envelope in both directions. A large catalog requires a larger advertised limit; pagination is not defined in 0.1.

`auth.login` accepts `{ "method": "sharedSecret", "secret": "..." }`. The result contains `accessToken`, `expiresAt`, and `subjectId`. The token is an unpredictable opaque secret, valid until the specified time or revocation. Tokens are bound to the connection that authenticated them. Reconnection requires a fresh `hello` and `auth.login`; tokens cannot be reused on another connection. Expiration requires logging in again; 0.1 has no refresh mechanism. `auth.logout` with an empty payload revokes the current token and returns `{ "revoked": true }`, then closes the transport, including BLE GATT resources.

A minimal implementation may store a preconfigured password, including in firmware. There must be no shared default password for all gateways. Secrets and tokens may only be transmitted over a secure, authenticated channel. Tokens and secrets are not included in logs, UI schemas, or URLs. The gateway rate-limits login attempts, returning `rate_limited` with `retryAfterMs`.

`client.id` is metadata, not proof of identity. `subjectId` is determined by credentials on the gateway. A shared password implies shared permissions and a shared operation scope; individual device identification requires an authentication extension. The gateway checks permissions when retrieving the catalog, invoking commands, and reading any operation or resource. Hiding a button does not replace access checks. Revoking a session does not by itself cancel commands already started.

### Device sessions and heartbeat

The client persists a unique installation ID in `client.id` (1–128 characters) and uses it across BLE and TCP profiles. `client.name` is a display name (1–256 characters). A successful login replaces the previous session for the same device; failed authentication never evicts a session. Different devices can remain connected concurrently. An installation ID groups sessions but is not a security credential: clients sharing the gateway password can claim the same ID.

The gateway sends a request once a minute on each authenticated connection:

```json
{"protocolVersion":"0.1","requestId":"ping-unique-id","type":"session.ping","payload":{}}
```

The client responds on the same connection, even while a command is running:

```json
{"protocolVersion":"0.1","requestId":"ping-unique-id","type":"session.ping","result":{}}
```

Neither frame carries an access token. Only a successful response matching the outstanding ping ID, type and version counts as a heartbeat. Until a response arrives, retries reuse that ping ID. The gateway ends the session after five minutes without a valid pong (measured from login until the first pong), or at token expiration. Late pongs cannot revive it. This clock is independent of command execution and command traffic.

Logout, transport disconnect, server revocation, and a new login from the same device also end the session. Requests using an ended session on a still-open transport receive `error.code = unauthorized`; the phone shows “Not authorized” and requires reconnection. Commands already started are not replayed automatically. An ended session may leave a transport open to deliver that error, but no longer appears among active users or receives pings.

The gateway logs an active-session snapshot once a minute, including an empty snapshot when no users are active: session ID (not the bearer token), subject, device ID/name, transport and timestamps. Upgrade both gateway and phone for this heartbeat contract; earlier clients that do not answer `session.ping` will expire.

### Gateway nodes and routing

A gateway has a globally unique stable ID, a name, a description and a `kind` (`executor` or `node`) in `capabilities.gateway`. An executor exposes and runs its commands. A node maintains authenticated connections to the peers configured in its `Gateways` settings and periodically refreshes their availability, name and description. These peers can be executors or other nodes. Peers initiate no registration; the node connects to their configured TCP endpoints. Names and descriptions may change at runtime without changing IDs.

After authenticating to a node, `capabilities.get` returns connection commands, one per configured peer. Their titles and descriptions identify the peer; unavailable peers remain visible with `availability.enabled = false`. Each command has empty arguments. IDs use `gateway.connect.<hex-node-id>.<hex-peer-id>`, where IDs are uppercase hexadecimal UTF-8. These prefixes and `gateway.disconnect.<hex-node-id>` are reserved for directory navigation.

A successful connection command returns `result.status = completed`, `output = { "gatewayId": "...", "connected": true }`, and `refreshCapabilities = true`. The client then reloads capabilities and UI. The selected peer's commands and metadata become visible, along with a command to return to the node directory. Disconnecting returns the same shape with `connected = false`. Selection is independent for every authenticated phone session. A chain of nodes retains a return command for each enclosing directory.

The node authenticates the phone locally using its own shared secret, and authenticates to the selected peer using that peer's configured secret. The original `client.id` and command `requestId` are preserved; bearer tokens are never copied between hops. Thus direct and routed connections from the same phone replace each other at the executor. Heartbeat runs on each connection separately. Revocation, logout, expiration or disconnect of a phone's node session also closes its selected downstream session. Directory monitors have separate installation IDs and never evict phone sessions.

Every forwarded application request carries an optional envelope field:

```json
"route": { "visitedGatewayIds": ["office-node", "floor-node"], "remainingHops": 14 }
```

A direct request starts with an empty history and a budget of 16 forwarding hops. Each node appends its ID and decrements the budget before forwarding. It rejects a target already present in the history, including itself, with `route_loop`, and refuses another forward at budget zero with `route_hop_limit`. Every recipient, including an executor, checks the history before dispatch. The handshake for a selected downstream connection carries the same history, preventing backward login from evicting an existing session. Responses follow the established connection and are not new forwarded commands.

The node forwards only to the explicitly selected peer; it never broadcasts, searches for another executor, sends the command back, or automatically replays it after failure. A lost connection or route timeout returns `gateway_unavailable` (command result `failed`, with `refreshCapabilities = true`); the execution outcome may be unknown. The cached selected catalog then disables business commands but preserves the directory return command. An unavailable connection attempt returns `rejected / gateway_unavailable`. Recovery requires an explicit new connection command. Directory health checks may reconnect independently, but never resend user commands.

The UI revision of a downstream schema is preserved for command validation. A node adds its navigation command to capabilities and computes a corresponding capabilities revision. Names and descriptions participate in catalog revision changes.

## 3. Capabilities

`capabilities.get` accepts `{}` and returns the object shown in the [example](examples/capabilities.json):

| Field | Purpose |
| --- | --- |
| `gateway` | Stable `id`, human-readable `name` and `description` |
| `revision` | Opaque catalog revision |
| `features` | Supported named features |
| `limits` | Message size, polling intervals, and retention periods |
| `commands` | Commands available to the subject, with argument and result types |
| `resources` | Images and other readable files |
| `endpoints` | Additional interfaces, including cameras and chats |

A command contains `id`, `title`, `description`, `parameters`, `result`, and `availability`. The identifier is stable and independent of button placement. `availability` contains `enabled` and an optional `reason`; it is a snapshot, with the final check performed at invocation. Each new connection and any `schema_outdated` error require refreshing the catalog and UI. Change subscriptions are not defined in 0.1.

`parameters` and `result` use the following **custom, restricted type descriptor**, not arbitrary JSON Schema:

- `type`: `object`, `array`, `string`, `integer`, `number`, or `boolean`.
- Objects: `properties`, a `required` list, and `additionalProperties` (always `false` in 0.1).
- Arrays: `items`, with optional `minItems` and `maxItems`.
- Strings: optional `minLength`, `maxLength` (in Unicode code points), and `enum`.
- Numbers: optional inclusive `minimum` and `maximum`.
- `description` is allowed on any descriptor. `null` is unsupported; optional values are omitted.

The gateway validates all arguments without implicit string-to-number conversion. Unknown arguments are rejected. The client also validates arguments before sending them to improve usability.

A resource contains `id`, `mediaType`, `uri`, and `auth`. Images may also specify `width` and `height` in pixels. A URI may be relative to the gateway's secure origin; it may be inaccessible over BLE, in which case the client displays a placeholder. Binary data is not included in the main JSON. Uploading images from the client requires a separate extension; support cannot be inferred from the presence of readable resources.

An endpoint contains `id`, `kind` (`camera`, `chat`, or `custom`), `title`, `uri`, `protocol`, `auth`, and an optional `contract`. `protocol` identifies the connection method (`rtsp`, `https`, `wss`, etc.), while `contract` identifies a named application extension contract. For `chat` and `custom`, `contract` is required: a link alone is insufficient to implement a client.

`auth.mode`:

- `none`: no additional authentication is required.
- `gatewayBearer`: HTTP Authorization Bearer using the gateway token; allowed only for HTTPS/WSS on the same origin, without forwarding the header to another origin on redirect.
- `external`: the client authenticates separately with the service; the gateway token is not sent.

The client does not open external endpoints automatically or execute OS commands or scripts from URIs. RTSP requires a compatible player and its own access policy. An unknown contract is shown as an unsupported integration.

## 4. Command invocation

`command.invoke` accepts `commandId`, an `arguments` object, and an optional `uiRevision`. If the specified UI revision is no longer valid for this invocation, the gateway returns `rejected / schema_outdated` before starting. Clients without a UI may omit `uiRevision`.

| `result.status` | Meaning and required fields |
| --- | --- |
| `completed` | The command has already succeeded; `output` matches the command's `result` descriptor |
| `rejected` | The command has not started; `error` explains why |
| `failed` | The command started but failed before the response was sent; `error` |
| `accepted` | The command has been accepted for execution; `operationToken`, `pollAfterMs` |

The gateway sends `accepted` if a result cannot be returned within `limits.commandResponseTimeoutMs`. Acceptance does not mean success or confirm a physical device change. The gateway must register the operation before performing any side effect. A command with no useful output is described by an empty object and returns `{}`.

### Long-running operations

`operation.get` accepts `{ "operationToken": "opaque-token" }`. The token is opaque, is not a password, and is accessible only to the authenticated subject that invoked the command. Response:

```json
{
  "operationToken": "opaque-token",
  "status": "running",
  "progress": { "value": 0.4, "message": "Processing 4 of 10" },
  "pollAfterMs": 500
}
```

States: `queued → running → completed | failed`; `queued → completed | failed` is also allowed. Terminal states are immutable. `completed` requires `output`; `failed` requires `error`. `queued` and `running` require `pollAfterMs`; `progress` is optional, and `value` is a number from 0 to 1. Cancellation is not part of the 0.1 core: stopping polling does not cancel execution.

Between requests, the client waits at least the greater of `pollAfterMs` and `limits.minPollIntervalMs`. The next request is sent after the previous response arrives. Temporary errors cause the interval to increase; polling may resume with the same token after reconnection. Authorization and operation ownership are checked again.

`limits.operationRetentionMs` is the minimum result retention period after completion. Active operations are not removed by this TTL. An unknown, foreign, or already removed token returns `operation_not_found` without disclosing another subject's operation. After a gateway restart, an operation may be lost if persistent storage is unavailable; a missing result does not mean the action did not occur.

### Retries and lost responses

The gateway atomically reserves the `(subjectId, client.id, requestId)` key before any side effects of `command.invoke`. A retry with the same payload content returns the original response, including the same token for `accepted`, without re-execution. Comparison is structural: object key order does not matter; array order and values do. The session token is excluded from comparison. A different payload with the same key returns `request_conflict`.

The key is retained for at least `limits.deduplicationWindowMs` after the request is accepted and is not removed while the operation is active. The window limits the guarantee: automatic retries after it expires, or after a restart without a persistent journal, may be unsafe. `limits.deduplicationPersistent` indicates whether the journal survives restarts. A lost response means “outcome unknown”; the client does not generate a new `requestId` for an automatic retry. Once the window expires, the application domain state must be checked or the user must make an explicit decision.

## 5. Errors

An error contains `code`, `message`, and `retryable`; optional `details` is an object, and `retryAfterMs` specifies the minimum retry delay. The message is intended for humans; branching uses `code`. `retryable` does not authorize retrying a side effect with a new identifier.

Core codes: `invalid_request`, `unsupported_version`, `unsupported_type`, `unauthorized`, `forbidden`, `rate_limited`, `message_too_large`, `command_not_found`, `invalid_arguments`, `command_unavailable`, `schema_outdated`, `request_conflict`, `operation_not_found`, `execution_failed`, and `internal_error`. Application-specific codes use the `app.` prefix. Argument error `details` may include `parameter` and `reason`. Responses do not include internal exceptions or secrets.
