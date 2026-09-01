import { logger } from "@argon/core";
import { defineStore } from "pinia";
import { ref } from "vue";
import delay from "@/lib/delay";

export type GroupReport = {
  name: string;
  usedBytes: number;
  percentOfQuota: number | null;
  percentOfGroupsTotal: number;
};

export type StorageUsageReport = {
  quotaBytes: number | null;
  storageUsedBytes: number | null;
  totalFreeBytes: number | null;
  groups: GroupReport[];
};

export type UsageDetails = Partial<
  Record<
    | "caches"
    | "indexedDB"
    | "fileSystem"
    | "serviceWorkerRegistrations"
    | string,
    number
  >
>;

export async function getStorageUsageReport(
  cacheGroupNames: readonly string[] = [
    "images",
    "stickers",
    "voices",
    "gifs",
    "videos",
    "files",
  ]
): Promise<StorageUsageReport> {
  let quota: number | null = null;
  let storageUsed: number | null = null;
  let usageDetails: UsageDetails | null = null;

  try {
    if (typeof navigator !== "undefined" && navigator.storage?.estimate) {
      const est = await navigator.storage.estimate();
      quota = typeof est.quota === "number" ? est.quota : null;
      storageUsed = typeof est.usage === "number" ? est.usage : null;
      usageDetails = (est as any).usageDetails ?? null;
    }
  } catch {}

  const existing = new Set<string>(await caches.keys());
  const groupEntries: Array<{ name: string; usedBytes: number }> = [];

  for (const name of cacheGroupNames) {
    let usedBytes = 0;
    if (existing.has(name)) {
      const cache = await caches.open(name);
      const responses = await cache.matchAll();
      for (const resp of responses) {
        const raw = resp.headers.get("X-Size");
        const n = raw ? Number(raw) : 0;
        if (Number.isFinite(n) && n > 0) usedBytes += n;
      }
    }
    groupEntries.push({ name, usedBytes });
  }

  const fsBytes = usageDetails?.fileSystem;
  if (typeof fsBytes === "number" && Number.isFinite(fsBytes) && fsBytes >= 0) {
    groupEntries.push({ name: "fileSystem", usedBytes: fsBytes });
  }

  const idbBytes = usageDetails?.indexedDB;
  if (
    typeof idbBytes === "number" &&
    Number.isFinite(idbBytes) &&
    idbBytes >= 0
  ) {
    groupEntries.push({ name: "indexedDB", usedBytes: idbBytes });
  }
  const groupsTotalUsed = groupEntries.reduce((s, g) => s + g.usedBytes, 0);

  const groups: GroupReport[] = groupEntries.map((g) => ({
    name: g.name,
    usedBytes: g.usedBytes,
    percentOfQuota:
      quota && quota > 0 ? round2((g.usedBytes / quota) * 100) : null,
    percentOfGroupsTotal:
      groupsTotalUsed > 0 ? round2((g.usedBytes / groupsTotalUsed) * 100) : 0,
  }));

  const totalFree =
    quota != null
      ? Math.max(
          0,
          quota - (storageUsed != null ? storageUsed : groupsTotalUsed)
        )
      : null;

  return {
    quotaBytes: quota,
    storageUsedBytes: storageUsed,
    totalFreeBytes: totalFree,
    groups,
  };
}

function round2(v: number) {
  return Math.round(v * 100) / 100;
}

export const pruneAll = async (pruneLocalStorage = true) => {
  await pruneIndexDb();
  await pruneBuckets();
  await pruneCache();

  if (pruneLocalStorage) localStorage.clear();

  location.reload();
};

export const pruneIndexDb = async () => {
  const allIndexDbs = await indexedDB.databases();

  for (const db of allIndexDbs) {
    try {
      indexedDB.deleteDatabase(db.name ?? "");
    } catch (e) {
      logger.error(e);
    }
  }
};

export const pruneCache = async () => {
  const allCaches = await window.caches.keys();

  for (const storage of allCaches) {
    try {
      await window.caches.delete(storage);
    } catch (e) {
      logger.error(e);
    }
  }
};

export const pruneBuckets = async () => {
  const allStorages = await navigator.storageBuckets.keys();

  for (const storage of allStorages) {
    try {
      await navigator.storageBuckets.delete(storage);
    } catch (e) {
      logger.error(e);
    }
  }
};

export const useFileStorage = defineStore("files", () => {
  const imagesCache = ref<Cache | null>(null);
  async function initCache(name: string, target: typeof imagesCache) {
    if (!("caches" in window)) {
      logger.fatal("Cache API is not supported");
      throw new Error("Cache API is not supported");
    }

    try {
      target.value = await caches.open(name);
      logger.success(`Cache "${name}" opened successfully.`);
    } catch (err) {
      logger.fatal("Failed to open cache", err);
    }
  }

  async function initStorages() {
    await initCache("images", imagesCache);
  }

  const FAILED_ADDRESS = "https://none/none.png";

  function unwrap(s: string) {
    return s.replaceAll("-", "");
  }

  const locks = new Map<string, string>();

  async function fetchByFileId({
    fileId,
    cache,
    fileUrlBuilder,
    allowFallback,
  }: {
    fileId: string;
    cache: Cache | null;
    fileUrlBuilder: (fileId: string) => string;
    allowFallback: boolean;
  }): Promise<string> {
    if (!cache) {
      logger.error(new Error("Cache is not initialized"));
      return allowFallback ? fileUrlBuilder(fileId) : FAILED_ADDRESS;
    }

    if (locks.has(fileId)) {
      while (locks.has(fileId)) await delay(100);
    }
    locks.set(fileId, fileId);

    try {
      const key = `https://argon-cache.local/${fileId}`;
      const req = new Request(key);

      const cached = await cache.match(req);
      if (cached) {
        const blob = await cached.blob();
        return URL.createObjectURL(blob);
      }

      const fileUrl = fileUrlBuilder(fileId);
      const response = await fetch(fileUrl);

      await delay(1000);
      if (!response.ok) {
        logger.error(new Error(`Failed to fetch file from ${fileUrl}`));
        return allowFallback ? fileUrlBuilder(fileId) : FAILED_ADDRESS;
      }

      const blob = await response.blob();
      const respForCache = new Response(blob, {
        headers: {
          "X-Inserted-At": Date.now().toString(),
          "X-Size": blob.size.toString(),
        },
      });
      await cache.put(req, respForCache);

      return URL.createObjectURL(blob);
    } catch (err) {
      logger.error(err);
      return allowFallback ? fileUrlBuilder(fileId) : FAILED_ADDRESS;
    } finally {
      locks.delete(fileId);
    }
  }

  async function fetchUserAvatar(fileId: string, userId: string) {
    return fetchByFileId({
      fileId,
      cache: imagesCache.value,
      fileUrlBuilder: (x) =>
        `https://eu.argon.zone/${x}`,
      allowFallback: false,
    });
  }

  async function fetchServerAvatar(fileId: string, serverId: string) {
    return fetchByFileId({
      fileId,
      cache: imagesCache.value,
      fileUrlBuilder: (x) =>
        `https://eu.argon.zone/${x}`,
      allowFallback: false,
    });
  }

  async function fetchImageByFileId(fileId: string, serverId: string) {
    return fetchByFileId({
      fileId,
      cache: imagesCache.value,
      fileUrlBuilder: (x) =>
        `https://eu.argon.zone/${x}`,
      allowFallback: true,
    });
  }

  return {
    initStorages,
    fetchServerAvatar,
    fetchUserAvatar,
    fetchImageByFileId,
    FAILED_ADDRESS,
  };
});
