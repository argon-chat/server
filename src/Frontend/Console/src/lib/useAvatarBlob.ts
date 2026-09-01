import { ref, watch, type Ref } from "vue";
import { useFileStorage } from "@/store/fileStorage";

const avatarCache = new Map<string, string>();

type AvatarType = "user" | "server";

function makeCacheKey(
  type: AvatarType,
  fileId: string | null,
  ownerId: string | null | undefined,
) {
  return `${type}:${ownerId ?? "unknown"}:${fileId ?? "null"}`;
}

export function useAvatarBlob(
  fileId: Ref<string | null>,
  ownerId: Ref<string | null | undefined>,
  type: AvatarType,
) {
  const loaded = ref(false);
  const loading = ref(true);
  const blobSrc = ref("");
  const fileStorage = useFileStorage();

  const fetchAvatar = async () => {
    if (!ownerId.value || !fileId.value) {
      loading.value = false;
      loaded.value = false;
      blobSrc.value = fileStorage.FAILED_ADDRESS;
      return;
    }

    const key = makeCacheKey(type, fileId.value, ownerId.value);

    if (avatarCache.has(key)) {
      const cachedSrc = avatarCache.get(key);
      if (cachedSrc) {
        blobSrc.value = cachedSrc;
        loading.value = false;
        loaded.value = blobSrc.value !== fileStorage.FAILED_ADDRESS;
        return;
      }
    }

    loading.value = true;
    let src: string;

    if (type === "user") {
      src = await fileStorage.fetchUserAvatar(fileId.value, ownerId.value);
    } else if (type === "server") {
      src = await fileStorage.fetchServerAvatar(fileId.value, ownerId.value);
    } else {
      throw new Error(`Unknown avatar type: ${type}`);
    }

    avatarCache.set(key, src);
    blobSrc.value = src;
    loading.value = false;
    loaded.value = src !== fileStorage.FAILED_ADDRESS;
  };

  watch([fileId, ownerId], fetchAvatar, { immediate: true });

  return {
    loaded,
    loading,
    blobSrc,
  };
}
