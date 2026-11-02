# Application schema

`ui.get` accepts `{}` and returns `revision`, `capabilitiesRevision`, `startScreenId`, and `screens`. The capabilities catalog and UI form a consistent pair: if `capabilitiesRevision` differs from the retrieved catalog revision, the client refreshes both documents and does not execute actions from an inconsistent schema. For a gateway without screens, `screens` is empty and `startScreenId` is omitted.

## Screens and drawers

A screen contains a unique `id`, `title`, `description`, `presentation`, and `root`; an optional `enterCommand` specifies an application command to execute when opening it. The complete schema is shown in [examples/screens.json](examples/screens.json).

`presentation.kind`:

- `page`: a regular screen participating in the navigation stack.
- `drawer`: a sliding panel over the current page; requires `side: left | right`, with an optional `width` in logical units. Width is constrained by the device's available area. The background is inactive while the drawer is open; closing it returns to the underlying page.

A `navigate` action with `screenId` opens a page or drawer according to its `presentation`. Navigation is therefore explicitly defined by the schema and does not require a dedicated remote command when only the client's UI changes. `closeDrawer` closes the open drawer; at most one drawer is open at a time. `back` returns to the previous page, or closes the drawer first if one is open.

Navigating to a page closes the current drawer; opening another drawer replaces the current one. On the root page, `back` does nothing. If an `enterCommand` is required, the view is replaced only after it completes successfully.

If the screen in the controlled application must also change, the target screen specifies an `enterCommand` with `commandId`, `arguments`, and `wait`. It runs before opening the screen, including the initial screen and subsequent entries. Only `completed` opens the screen; an error or unknown outcome preserves the current screen and displays the reason. Navigating back or restoring an already open page does not invoke the command again. `enterCommand` only permits `literal` arguments because the target screen's controls have not yet been created.

## Layout

The `root` and nested containers have `type: container`, a `layout`, and ordered `children`.

| `layout.kind` | Rules |
| --- | --- |
| `vertical` | Top to bottom in `children` order, scrolling when height is insufficient |
| `wrap` | Left to right with line wrapping; `itemWidth` specifies the item width, constrained by the container width; each row is as tall as its tallest item |
| `absolute` | A coordinate canvas with required `width` and `height`; each child has `bounds: {x, y, width, height}` |

All dimensions are nonnegative; width and height are positive. Units are logical (independent of display density). `vertical` and `wrap` allow `gap` and `padding` (both default to 0). These are not used for `absolute`. The origin is at the top left, X increases to the right, and Y increases downward; bounds must lie within the canvas. Later children are drawn over earlier ones. The canvas scales down uniformly to the available width, without upscaling; it scrolls if height is insufficient. Coordinates alone cannot guarantee identical fonts across platforms for an exact appearance.

Containers may be nested. The schema contains no HTML, executable JavaScript, or arbitrary expressions.

## Controls and values

Each node has an `id` unique within its screen and a `type`. Common optional fields are `label`, `description`, `visible` (default true), `enabled` (default true), and `bounds` for direct children of an absolute container. These are descriptive snapshots, not access-control mechanisms.

| `type` | Fields and value type |
| --- | --- |
| `label` | `text`; no value to pass to a command |
| `button` | `label`, `action`; no value |
| `textInput` | `initialValue` (default `""`), optional `placeholder`; string |
| `numberInput` | `initialValue`, optional `minimum`, `maximum`, `step`; number |
| `toggle` | `initialValue` (default false); boolean |
| `select` | `options: [{label, value}]`, optional `initialValue`; `value` is a string |
| `image` | `resourceId`, optional `alt`; resources come from capabilities |
| `container` | `layout`, `children`; no value |

A `numberInput` without an initial value and a `select` without a selection have no value. A `select` control's initial and selected values must be present in options. Invalid numeric input is not converted to 0. Control values belong to a screen instance; a new entry creates initial values, while navigating back preserves the existing instance's values. Opening a drawer does not reset the page.

Unknown types are displayed as unavailable placeholders. Actions depending on their values are disabled with an explanation. An unknown layout makes the corresponding container unavailable. The client must not guess a type or silently invoke a command without a required argument.

## Actions and linked controls

`button.action` is one of the following actions:

| `kind` | Fields |
| --- | --- |
| `navigate` | `screenId` |
| `back` | None |
| `closeDrawer` | None |
| `invoke` | `commandId`, `arguments`, optional `wait` and `onCompleted` |
| `scanBarcode` | `targetControlId`, `formats`, optional `then` |
| `openEndpoint` | `endpointId` from capabilities |

`invoke.arguments` maps parameter names to sources:

```json
{
  "barcode": { "source": "control", "controlId": "barcode", "property": "value" },
  "quantity": { "source": "control", "controlId": "quantity", "property": "value" },
  "mode": { "source": "literal", "value": "normal" }
}
```

References resolve only within the current screen (or current drawer); in 0.1, `property` is always `value`. When the button is pressed, the client snapshots all arguments; subsequent edits do not change the submitted invocation. Missing optional arguments are omitted; a missing required argument prevents submission and produces a validation error. Objects and arrays may be passed through `literal`. Arbitrary computations are unsupported.

`onCompleted` allows only `navigate`, `back`, or `closeDrawer` and runs exactly once within the active action after an immediate or deferred `completed`. The returned `output` is available for the client to display; arbitrary result-to-control binding is not defined in 0.1. `rejected`, `failed`, and lost status do not trigger navigation.

### Barcodes

`scanBarcode` starts the local camera or scanner. `targetControlId` must reference a `textInput` on the same screen; `formats` is a nonempty list drawn from `ean13`, `ean8`, `code128`, and `qr`. Successful recognition writes the string to the field without numeric conversion; leading zeros are preserved. If specified, `then` runs an `invoke` action that reads the updated value. Other `then` action kinds are not allowed in 0.1.

Cancellation, denied camera permission, an unsupported format, or an unavailable scanner do not trigger `then` or modify the field. The client displays the reason; the user can enter the value manually. Scanning support is a client capability, not a gateway command or a consequence of gateway camera support.

### Waiting and UI blocking

`wait` contains `block: none | control | screen | application` and `timeoutMs` (a positive integer). Defaults are `block: control` and `timeoutMs: 30000`. `control` blocks the initiating button; `screen` blocks the current screen or drawer and navigation away from it; `application` blocks interaction with the entire client, except for a local option to stop waiting. For `enterCommand`, `none` and `application` are allowed, with `application` as the default.

Blocking lasts from invocation submission until a terminal response or the local end of waiting, including the initial network request. For `accepted`, the client stores the token and displays a waiting indicator, along with progress when available. With `none`, the result is displayed without blocking. The client prevents repeated activation of the same running action regardless of visual blocking.

`timeoutMs` limits only client waiting, not gateway execution. On timeout, connection loss, an unrecoverable authentication or authorization error, or a manual stop to waiting, the client releases the UI and reports “outcome unknown”, preserving any available `requestId` and `operationToken` for recovery. After waiting ends locally, a late result does not trigger unexpected automatic `onCompleted` navigation; the result may be shown as a notification. A gateway cannot indefinitely block the client through its schema.
