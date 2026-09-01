import { defineStore } from "pinia";
import { computed } from "vue";
import { createClient } from "@/lib/glue/accountConsole";
import { IonCallContext, IonInterceptor } from "@argon-chat/ion.webcore";

class Interceptors implements IonInterceptor {
  invokeAsync(ctx: IonCallContext, next: (ctx: IonCallContext, signal?: AbortSignal) => Promise<void>, signal?: AbortSignal): Promise<void> {
    const token = localStorage.getItem("access_token")

    if (token) {
      // `requestHeadets` until the move: a typo, and a silent one — the transport reads
      // `requestHeaders`, so every console call went out without its bearer token.
      ctx.requestHeaders = {
        Authorization: `Bearer ${token}`,
        ...ctx.requestHeaders,
      };
    }

    return next(ctx, signal);
  }
  
}

export const useApi = defineStore("api", () => {
  // Whatever host served this page. It used to be `https://console.argon.gl` outright, which meant
  // a self-hosted instance — and a developer running this locally — talked to Argon's production
  // console. The Ion services answer on a port of their own (8930), so the proxy in front has to
  // route them under the same origin the page came from; ../README.md says so where it names the
  // port.
  const rpcClient = computed(() =>
    createClient(location.origin, [new Interceptors()])
  );

  const consoleInteraction = computed(() => rpcClient.value.AccountConsole);
  const appsManagement = computed(() => rpcClient.value.AppManagement);
  const teamsManagement = computed(() => rpcClient.value.TeamConsole);

  const getRawClient = () => rpcClient;

  return {
    consoleInteraction,
    appsManagement,
    teamsManagement,
    getRawClient,
  };
});
