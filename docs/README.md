---
title: ValheimJS documentation
description: Plugin workflows, native runtime ownership and future work.
---

# ValheimJS documentation

For writing and running plugins, start with the [plugin guides](guides/README.md). For changing
the runtime, start with [runtime ownership](runtime.md) and the repository [README](../README.md).

| Task | Read |
|---|---|
| Build a first plugin | [Getting started](guides/getting-started.md) |
| Choose shared, server or client code | [Process branches](guides/process-branches.md) |
| Understand hooks, async work and exports | [Plugin anatomy](guides/plugin-anatomy.md) |
| Register private commands and contextual help | [Chat commands](guides/chat-commands.md) |
| Administer users, roles and permissions | [Management](guides/management.md) |
| Store per-user state and share account links | [Accounts](guides/accounts.md) and [recipes](guides/recipes.md) |
| Send events or explicit sync frames | [Networking](networking.md) |
| Use native typed RPC directly | [C# RPC bus](rpc-bus.md) |
| Bind client keys | [Keyboard bindings](keyboard.md) |
| Check exact JS signatures | [API reference](guides/api-reference.md) and [generated declarations](../scripting/sdk/api/generated.ts) |
| Diagnose or package a plugin | [Troubleshooting](guides/troubleshooting.md), [publishing](guides/publishing.md) |
| Inspect capture points or future work | [Hook map](hook-map.md), [API roadmap](api-roadmap.md) |

The API version is **1**. The roadmap separates proposed work from implemented behavior;
decompiled mod research is reference material, not an inventory of ValheimJS features.
