export {};

// The storage-bucket API, which TypeScript's DOM library does not describe yet. Used by
// src/store/fileStorage.ts to keep cached avatars in a bucket of their own.
declare global {
  interface StorageBucketOptions {
    persisted?: boolean;
    quota?: number;
    expires?: DOMHighResTimeStamp;
    durability?: "relaxed" | "strict";
  }

  interface StorageBucket extends StorageManager {
    readonly name: string;
    readonly indexedDB: IDBFactory;
    readonly caches: CacheStorage;

    setExpires(expires: DOMHighResTimeStamp): Promise<void>;
    expires(): Promise<DOMHighResTimeStamp | null>;
    getDirectory(): Promise<FileSystemDirectoryHandle>;
  }

  interface StorageBucketManager {
    open(name: string, options?: StorageBucketOptions): Promise<StorageBucket>;
    keys(): Promise<string[]>;
    delete(name: string): Promise<void>;
  }

  interface Navigator {
    readonly storageBuckets: StorageBucketManager;
  }
}
