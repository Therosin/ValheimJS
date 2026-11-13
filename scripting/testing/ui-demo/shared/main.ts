import { definePlugin, log, ui } from "../../../sdk/index";

const PANEL = `
components:
  field:
    type: setting-row
    props:
      label: { param: label }
      hint: { param: hint }
    children:
      - slot: control

panel:
  type: window
  props:
    title: ValheimJS UI Demo
    subtitle: Flat declarative panels
    icon: settings
    width: 860
    height: 620
  children:
    - type: tabs
      state: { tab: 0 }
      props: { active: { local: tab } }
      children:
        - type: tab
          props: { id: general, label: General, width: 150 }
          children:
            - type: section
              props:
                title: General Settings
                description: Values below are bound to plugin state.
              children:
                - use: field
                  args: { label: Enabled, hint: Toggle a bound boolean }
                  slots:
                    control:
                      - type: toggle
                        props: { value: { bind: "enabled" } }
                - use: field
                  args: { label: Name, hint: Bound text value }
                  slots:
                    control:
                      - type: input
                        props: { value: { bind: "name" }, placeholder: Player }
                - use: field
                  args: { label: Scale, hint: Bound numeric value }
                  slots:
                    control:
                      - type: slider
                        props: { value: { bind: "scale" }, min: 0, max: 2 }
                - use: field
                  args: { label: Mode, hint: Select an option }
                  slots:
                    control:
                      - type: select
                        props: { value: { bind: "mode" }, options: [Left, Center, Right] }
            - type: footer
              children:
                - type: button
                  props: { label: Save, variant: primary, onPress: save }
                - type: button
                  props: { label: Close, onPress: close }
        - type: tab
          props: { id: modules, label: Modules, width: 150 }
          children:
            - type: list-row
              props: { icon: check, label: UI compiler, secondary: "ready" }
            - type: list-row
              props: { icon: check, label: Bindings, secondary: "active" }
            - type: list-row
              props: { icon: dot, label: Theme assets, secondary: "fallback" }
`;

export default definePlugin({
    version: "1.0.0",

    setup(ctx) {
        if (!ui.available()) return;
        ctx.state.values = { enabled: true, name: "Tester", scale: 1, mode: "Center" };
        ctx.state.closed = false;
        ctx.state.document = ui.define(PANEL, { source: "ui-demo.yaml" });
    },

    onServerTick(ctx) {
        if (!ui.available() || ctx.state.panel || ctx.state.closed) return;
        const doc = ctx.state.document as string | undefined;
        if (!doc) return;

        ctx.state.panel = ui.open(doc, {
            mount: "window",
            modal: false,
            get: key => (ctx.state.values as Record<string, unknown>)[key],
            set: (key, value) => {
                (ctx.state.values as Record<string, unknown>)[key] = value;
                log(`ui demo: ${key}=${String(value)}`);
            },
            onEvent: (path, event, value) => {
                log(`ui demo: ${path} ${event}${value === undefined ? "" : "=" + String(value)}`);
                if (event === "close") {
                    const panel = ctx.state.panel as string;
                    ui.close(panel);
                    ctx.state.panel = "";
                    ctx.state.closed = true;
                }
            }
        });
        log(`ui demo opened: ${ctx.state.panel}`);
    }
});
