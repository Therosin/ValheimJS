---
title: Manage users, roles and permissions
description: Private administration through native manage commands.
---

# Manage users, roles and permissions

Valheim administrators can use the native `!manage:roles`, `!manage:permission` and `!manage:user`
commands in chat. Alternatively, delegate access by granting an account `valheimjs.manage`.
Commands and replies are private. They execute on the world authority and work on dedicated
servers and player hosts. No JS plugin is needed to register them.

The host checks the caller's verified platform identity against Valheim's existing admin list
using `ZNet.IsAdmin`. That grants access to these management commands without importing an admin
role or granting all other plugin permissions. Valheim admins retain management access even if
their ValheimJS account has an explicit denial; the game admin list is the recovery authority.
For delegated users, normal permission rules and denials apply. Access is rechecked before execution.

## Create a role and give it to a player

The first policy initialization creates three default roles:

| Role | Initial permissions |
|---|---|
| `admin` | `*` (all permissions) |
| `staff` | None; configure the permissions your staff need |
| `player` | None; default for accounts without a saved access record |

Default definitions are seeded once. Editing or deleting them is preserved across restarts;
existing role assignments, grants and denials are never replaced by defaults. If `player` is
deleted, accounts without saved policy start with no role. Accounts with an explicitly saved
empty role list stay empty.

Valheim's admin list continues to grant recovery access to management commands. It does not
automatically assign the ValheimJS `admin` role or grant all plugin permissions. To assign it:

```text
!manage:user assign self=true role=admin
```

```text
!manage:permission create name=bank.read description="Read bank account links"
!manage:roles create name=banker permissions=bank.read description="Bank staff"
!manage:user assign name="Some Player" role=banker
!manage:user check name="Some Player" permission=bank.read
```

The first word after the command is its action. Named options follow the action, with double
quotes for spaces. Unlike ordinary free-text commands, management actions reject trailing text,
duplicate options and unknown option names. Each command has a `help` action; no action shows help.
Contextual help is also available through `!help manage:roles create`,
`!help manage:permission update` or `!help manage:user grant`.

## Roles

| Command | Effect |
|---|---|
| `!manage:roles list page=1` | List role names, eight per page |
| `!manage:roles show name=banker` | Show description and permission grants |
| `!manage:roles create name=banker permissions="bank.read,bank.deposit"` | Create a new role; duplicates fail |
| `!manage:roles update name=banker permissions="bank.read"` | Replace the role's complete permission list |
| `!manage:roles update name=banker description="Bank staff"` | Update its description |
| `!manage:roles update name=banker permissions=""` | Clear its permission grants |
| `!manage:roles delete name=banker` | Delete an unassigned role |

`permissions=` is a comma-separated list. Omitted update fields are preserved. Role names and
permission names are case insensitive. Rename is not supported; create a new role and reassign
users explicitly. Assigned roles cannot be deleted until they are unassigned from every account.

## Permission catalogue

```text
!manage:permission list
!manage:permission show name=bank.read
!manage:permission create name=bank.read description="Read bank links"
!manage:permission update name=bank.read description="Inspect bank account links"
!manage:permission delete name=bank.read
```

The catalogue describes permission names. Creating a definition grants nobody access; roles and
account grants control access. Permission checks also support names without catalogue entries,
so plugins and administrators can use a permission node directly. Deleting a catalogue entry
does not disable the corresponding permission check. Definitions referenced by explicit role
grants or account grants/denials must have those references removed before deletion.

## Select and inspect users

```text
!manage:user list page=1
!manage:user show
!manage:user show self=true
!manage:user show name="Some Player"
!manage:user show platform=Steam_76561198000000000
!manage:user show id=0123456789abcdef0123456789abcdef
```

`show` without a selector shows your own account. Other user operations require exactly one
selector: `id=`, `platform=`, `name=` or `self=true`. Names match the complete known player name,
case insensitively. Ambiguous names fail; use the stable account id or platform identity instead.
Users must already have an account created from a verified connection. Offline accounts can
be managed too. The list includes account ids for selection.

## Change a user's profile and access

```text
!manage:user update name="Some Player" displayName="Bank Teller" locale=en-GB
!manage:user assign name="Some Player" role=banker
!manage:user unassign name="Some Player" role=banker
!manage:user grant name="Some Player" permission=bank.read
!manage:user deny name="Some Player" permission=bank.read
!manage:user revoke name="Some Player" permission=bank.read
!manage:user check name="Some Player" permission=bank.read
```

Profile updates change only display name and locale. Account id, platform identity and observed
player name are host-managed; these commands do not rewrite them or delete user accounts.
Empty quoted values clear profile fields. Role assignment preserves other roles and direct policy.

`grant` adds a direct grant and removes an exact direct denial. `deny` does the reverse. `revoke`
removes both exact direct overrides, so an inherited role grant may become effective again.
Explicit account denials override role grants and wildcard grants. `permission="*"` can be used
for a global direct grant/denial; there are no hierarchical wildcards or role inheritance.

To delegate administration:

```text
!manage:user grant name="Trusted Player" permission=valheimjs.manage
```

Successful mutations are logged with the actor, command/action and selected target. Query
results stay in private command replies. These commands use the native services; JS plugins
still have no profile or policy editing API. See [Accounts](accounts.md) for shared metadata.
