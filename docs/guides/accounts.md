---
title: Accounts and shared user data
description: Resolve stable accounts, publish small shared values and query access policy.
---

# Accounts and shared user data

An account identifies a player across connections and character-name changes. ValheimJS resolves
it from the verified connection's platform identity and assigns a persistent account id.
The session peer id is only a lookup key for the current connection.

`AccountService` owns identity and profile details. `PermissionService` separately owns role
definitions, assignments and permission grants/denials. Plugins can query these services and
publish small shared data records. They cannot create accounts, edit profiles or change access
policy through the JS API. A separate user-management API is future work.

## Publish something another plugin needs

A bank should keep its accounts, ledger and transactions in its own database. It can publish
the bank's account id against the ValheimJS user account, so another plugin can find that bank
account and call the bank's exported operations:

```ts
import { AccountService, use } from "../../../sdk/index";

// Running in the bank plugin, during an authoritative callback:
AccountService.setData(userAccountId, { accountId: bankAccountId });

// Running in a shop plugin:
type BankApi = {
    get_account(id: string): { withdraw(amount: number): void; deposit(amount: number): void };
};
const link = AccountService.readPublishedData<{ accountId: string }>(
    userAccountId, "bank"
);
if (link) {
    use<BankApi>("bank", bank => {
        const account = bank.get_account(link.accountId);
        account.withdraw(10); // The bank validates and performs the operation.
    });
}
```

The host supplies the writing plugin's identity. There is no write-publisher parameter: a shop
can read the bank's record but can only replace/delete its own record. `getData(accountId)` reads
the caller's record; `deleteData(accountId)` removes it. Missing records return `null`.
Records must be JSON objects, at most 16 KiB in UTF-8 with nesting limited to 32 levels.
`setData` replaces the complete record; merge explicitly when needed.

Published data is intentionally visible to other plugins on the authority. Keep secrets and
ordinary plugin state in your own database. The shared record is a lookup link; the bank's
exports own transaction validation and actual debits. Declare `depends=bank@*` in the consuming
plugin's manifest so the provider loads first. `use` consumes exports on the same process;
cross-process communication still uses networking.

Provider exports are ordinary JS calls, but the provider's SDK copy is bound to the provider:
`database()`, `fs()` and scoped `ctx.host.api` keep the provider's storage scope when a consumer
calls an exported operation. Pass the consumer's identity explicitly if the operation needs it;
`use` provides the consumer context as its second argument. See
[Sharing code between plugins](plugin-anatomy.md#sharing-code-between-plugins).

## Find an account

```ts
const account = AccountService.fromPeer(String(peerId));
const sameAccount = AccountService.findByPlatform("Steam_76561198000000000");
const offlineAccount = AccountService.get(accountId);
```

Queries return a detached snapshot or `null`. A snapshot contains `id`, canonical `platformId`,
`knownName`, `createdAt`, `lastSeenAt` (Unix milliseconds), and the host-owned `profile` fields
`displayName` and `locale`. Mutating the returned object does not persist anything.
Steam identities with `Steam_`, `steam:` or bare digits normalize to the same identity. Other
providers retain their identifier under a lowercase provider prefix. IP addresses are not account
identities. This version binds one platform identity to one account; identity linking is not exposed.

The host observes fully identified peers and the local player when hosting. Disconnecting clears
the session lookup while preserving the account and its shared records. `lastSeenAt` records the
latest connection/name observation, not a continuously updated presence timestamp.

## Authority and storage

Both services require a live session with server authority. Check `AccountService.available()`
or `PermissionService.available()` when needed. Before a session exists, and on a client connected
to another host, queries and writes throw rather than opening a local copy of the server's data.
A player host gains access while hosting; its process branch remains `client`.

Register callbacks/commands during setup and query accounts from authoritative callbacks once a
session exists. There is no client replication or remote account-management API yet.

Account records live under `ValheimJS/services/accounts/`; policy lives under
`ValheimJS/services/permissions/`. Both reuse the document database backend and atomic per-document
writes, independently of `data/<plugin>/db/`. These host folders are outside plugin filesystem
sandboxes. Back them up alongside plugin data. Account metadata survives plugin unload and reload.
Storage errors are reported as failures; failed writes do not replace the cached value.

## Roles and permissions

```ts
import { PermissionService } from "../../../sdk/index";

PermissionService.hasRole(accountId, "moderator");
PermissionService.hasPermission(accountId, "bank.read");
```

Role and permission names are case insensitive. Permissions are exact names; a stored `*` grants
all permissions. There is no hierarchical wildcard or inherited role graph. An explicit account
denial overrides direct grants and role grants, including `*`. Unassigned permissions are denied.
Accounts without saved policy receive the `player` role, which initially grants nothing.
The default roles are `admin` (`*`), `staff` (no grants) and `player` (no grants). They are
seeded once; later customization and existing account policy survive restarts.
Valheim's admin list is not imported into account policy;
it provides recovery access to the native [management commands](management.md).

Native host code configures policy through `PermissionService.DefineRole(name, permissions)` and
`SetAccess(accountId, roles, grants, denials)`. Define roles before assigning them. These are trusted
host methods; no policy-editing methods are exposed to JS plugins. All service calls belong on the
Unity thread, including native calls after asynchronous work.

Administrators can edit profiles and policy through private `!manage:user`, `!manage:roles` and
`!manage:permission` commands. See [Management](management.md) for selectors and examples.

Commands may declare `roles` (any listed role suffices) and `permissions` (all are required).
The registry checks live policy before validation and again before execution, so revoking access
during async validation prevents execution. It does not undo work already started.
Command users expose `account`, `hasRole(name)` and `hasPermission(name)` alongside private replies:

```ts
async execute(ctx: ChatCommandContext) {
    if (!ctx.user.account) throw new Error("Account identity is unavailable.");
    const published = AccountService.getData(ctx.user.account.id) ?? {};
    AccountService.setData(ctx.user.account.id, { ...published, greeted: true });
    ctx.user.sendMessage("Your account metadata is saved.");
}
```
