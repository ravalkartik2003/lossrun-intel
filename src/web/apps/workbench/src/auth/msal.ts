import { PublicClientApplication, InteractionRequiredAuthError, type AccountInfo } from "@azure/msal-browser";
import { ENTRA } from "../env.ts";
import { setEntraTokenSource } from "./auth.ts";

// VITE_AUTH=entra: Entra ID sign-in with MSAL (redirect flow). The API reads the app roles (Applicant, Underwriter,
// UWManager, Admin) from the access token; the UI reads them back through GET /me.

let app: PublicClientApplication | null = null;
let account: AccountInfo | null = null;

export async function initEntra(): Promise<void> {
  app = new PublicClientApplication({
    auth: { clientId: ENTRA.clientId, authority: ENTRA.authority, redirectUri: window.location.origin },
    cache: { cacheLocation: "sessionStorage" },
  });
  await app.initialize();
  const result = await app.handleRedirectPromise();
  account = result?.account ?? app.getAllAccounts()[0] ?? null;
  if (!account) {
    await app.loginRedirect({ scopes: [ENTRA.scope] });
    return;
  }
  setEntraTokenSource(
    async (forceRefresh) => {
      try {
        return (await app!.acquireTokenSilent({ scopes: [ENTRA.scope], account: account!, forceRefresh })).accessToken;
      } catch (e) {
        if (e instanceof InteractionRequiredAuthError) await app!.acquireTokenRedirect({ scopes: [ENTRA.scope], account: account! });
        throw e;
      }
    },
    () => app!.acquireTokenRedirect({ scopes: [ENTRA.scope], account: account! }),
  );
}

export async function signOutEntra(): Promise<void> {
  await app?.logoutRedirect({ account: account ?? undefined });
}
