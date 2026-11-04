import { afterEach, expect, test } from "bun:test";
import { AccountService, PermissionService } from "../sdk/accounts";
import type { PluginContext } from "../sdk/runtime";

const globals = globalThis as { __vjs_context?: PluginContext };
afterEach(() => { delete globals.__vjs_context; });

test("account facade delegates queries and publishes data without accepting a write namespace", () => {
    const calls: unknown[][] = [];
    const account = { id: "account-id", knownName: "Player" };
    globals.__vjs_context = { host: { api: {
        AccountService: {
            available: () => true,
            get: (id: string) => { calls.push(["get", id]); return account; },
            findByPlatform: (id: string) => { calls.push(["platform", id]); return account; },
            fromPeer: (id: string) => { calls.push(["peer", id]); return account; },
            getData: (id: string) => { calls.push(["own", id]); return { visited: true }; },
            readPublishedData: (...args: unknown[]) => { calls.push(["read", ...args]); return { balance: 42 }; },
            setData: (...args: unknown[]) => calls.push(["write", ...args]),
            deleteData: (id: string) => calls.push(["delete", id])
        },
        PermissionService: { available: () => true, hasRole: () => false, hasPermission: () => true }
    } } } as unknown as PluginContext;
    expect(AccountService.available()).toBe(true);
    expect(AccountService.get("id")).toBe(account);
    expect(AccountService.findByPlatform("Steam_42")).toBe(account);
    expect(AccountService.fromPeer("9007199254740993")).toBe(account);
    expect(AccountService.getData("id")).toEqual({ visited: true });
    expect(AccountService.readPublishedData<{ balance: number }>("id", "bank")?.balance).toBe(42);
    AccountService.setData("id", { visited: true });
    AccountService.deleteData("id");
    expect(calls).toContainEqual(["write", "id", '{"visited":true}']);
    expect(calls).toContainEqual(["read", "id", "bank"]);
    expect(calls).toContainEqual(["peer", "9007199254740993"]);
    expect(PermissionService.available()).toBe(true);
    expect(PermissionService.hasRole("id", "admin")).toBe(false);
    expect(PermissionService.hasPermission("id", "shop.use")).toBe(true);
});
