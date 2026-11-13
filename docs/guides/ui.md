---
title: Declarative UI
description: YAML panels, imports, components, bound values and events.
---

# Declarative UI

ValheimJS owns the UI runtime. Plugins ship YAML panels; the host compiles and renders them with
the flat dark theme, native layout and input handling. A document contains **one root `panel`**
and optional reusable **components** (which are just reusable panels).

## Define and open

```ts
import { ui } from "../../../sdk/index";

const document = ui.define(`
imports:
  widgets: ./widgets.yaml

components:
  field:
    type: row
    children:
      - type: text
        props: { text: { param: label } }
      - slot: control

panel:
  type: window
  props: { title: Player Settings, width: 720, height: 520 }
  children:
    - type: section
      props: { title: General, description: Profile and UI options }
      children:
        - use: field
          args: { label: Name }
          slots:
            control:
              - type: input
                props: { value: { bind: "player.name" } }
`);

const panel = ui.open(document, {
    mount: "window",             // overlay | window | popup
    modal: true,
    get: key => values[key],
    set: (key, value) => { values[key] = value; },
    onEvent: (path, event, value) => { /* press, change, close, ... */ }
});

ui.invalidate(panel);            // refresh bound values next tick
ui.close(panel);
```

`ui` is client-only; on a dedicated server it is not installed.

## Imports

```yaml
imports:
  widgets: ./widgets.yaml
```

`ui.define(yaml, { imports: { widgets: widgetsYaml } })` supplies imported documents in memory.
`source` is used in compile errors. Imports are recursive; missing or circular imports fail.
An imported document contributes its `components`; if it has a `panel`, that panel is exposed as a
component under the import alias. Component names are per plugin; collisions fail at compile.

## Values

| Syntax | Meaning |
|---|---|
| literal | `"text"`, `42`, `true`, lists and maps |
| `{ param: name }` | Component argument supplied by `args` |
| `{ bind: "key" }` | Host-resolved value; `get`/`set` on the open panel |
| `{ local: key }` | State scoped to the expanded component/panel |
| `slot: name` / `slots:` | Named insertion point |

Bindings are symbolic. The compiler never evaluates them; text and input can share the same key.
`get` is polled on the host tick (or after `ui.invalidate`); `set` is called for controls. Inputs
do not overwrite while focused; they commit on end-edit/submit.

## Panels

Host-owned panel kinds:

`window`, `panel`, `row`, `column`, `grid`, `scroll`, `spacer`, `divider`, `text`, `image`,
`icon`, `button`, `input`, `toggle`, `slider`, `select`, `tabs`, `tab`, `section`, `setting-row`,
`list-row`, `footer`.

- `window` has title/subtitle/icon, close/collapse, drag, fixed width/height and anchor.
- `row`/`column` use horizontal/vertical layout; `gap`, `padding`, `align`, `width`, `height`,
  `grow`.
- `text`: `text`, `size` (`sm`/`md`/`lg`/`title`/`section`), `tone`, `bold`, `align`.
- `button`: `label`, `variant` (`primary`/`secondary`/`ghost`/`danger`), `onPress` event name.
- `input`: `value`, `placeholder`, `change`/`submit` events.
- `toggle`: `value`, `label`.
- `slider`: `value`, `min`, `max`, `whole`, `format`.
- `select`: `value`, `options` (string list).
- `tabs`: children are `tab` panels; `active` can be a local or bound value.
- `section`/`setting-row`/`list-row`/`footer`: the flat settings layout from the reference.

## Local state

```yaml
components:
  section:
    type: panel
    state: { open: false }
    children:
      - type: button
        props: { label: Toggle, onPress: toggle }
```

`{ local: open }` resolves from the nearest expanded panel/component scope. Components keep
independent state per instance. Window collapse/drag and tab selection are host-owned; the plugin
only observes events if it needs to persist them.

## Validation and limits

- one root `panel` per document;
- unknown panel types/keys, missing arguments/slots and component cycles fail at compile;
- component names are per plugin; imported aliases cannot collide;
- max 2048 nodes, depth 32, 128 components per document, 512 KiB YAML;
- no expressions or code in YAML; logic stays in JS callbacks.
