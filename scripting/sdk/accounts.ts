import { context } from "./runtime";
import type { Account } from "./api/types";

/** Thin facade. Authority, identity, storage and publisher ownership are enforced by the host. */
export const AccountService = {
    available(): boolean { return context().host.api.AccountService.available(); },
    get(id: string): Account | null { return context().host.api.AccountService.get(id); },
    findByPlatform(platformId: string): Account | null { return context().host.api.AccountService.findByPlatform(platformId); },
    fromPeer(peerId: string): Account | null { return context().host.api.AccountService.fromPeer(peerId); },
    getData<T extends object = Record<string, unknown>>(id: string): T | null {
        return context().host.api.AccountService.getData(id) as T | null;
    },
    readPublishedData<T extends object = Record<string, unknown>>(id: string, publisher: string): T | null {
        return context().host.api.AccountService.readPublishedData(id, publisher) as T | null;
    },
    setData(id: string, data: object): void { context().host.api.AccountService.setData(id, JSON.stringify(data)); },
    deleteData(id: string): void { context().host.api.AccountService.deleteData(id); }
};

/** Read-only plugin access to host-owned policy. */
export const PermissionService = {
    available(): boolean { return context().host.api.PermissionService.available(); },
    hasRole(id: string, role: string): boolean { return context().host.api.PermissionService.hasRole(id, role); },
    hasPermission(id: string, permission: string): boolean { return context().host.api.PermissionService.hasPermission(id, permission); }
};
