import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { CircleAlert } from "lucide-react";
import { useState } from "react";
import { useAuth } from "../auth/auth.ts";
import { API_BASE, ENVIRONMENT_LABEL } from "../env.ts";
import { useTitle } from "./hooks.ts";

const schema = z.object({
  user: z.string().trim().min(1, "Enter the user name (applicant, underwriter, uwmanager or admin)."),
  password: z.string().min(1, "Enter the password."),
});

type Values = z.infer<typeof schema>;

/**
 * Demo-host sign-in (LOSSRUN_DEV_AUTH=basic). The credentials are checked against GET /me and kept in sessionStorage
 * for this tab only. With VITE_AUTH=entra this screen is never shown (MSAL redirects to Entra).
 */
export function LoginScreen() {
  useTitle("Sign in");
  const signIn = useAuth((s) => s.signIn);
  const [failure, setFailure] = useState<string | null>(null);
  const { register, handleSubmit, formState } = useForm<Values>({ resolver: zodResolver(schema) });

  const onSubmit = async (v: Values) => {
    setFailure(null);
    const res = await fetch(`${API_BASE}/me`, { headers: { Authorization: `Basic ${btoa(`${v.user}:${v.password}`)}` } }).catch(() => null);
    if (!res) return setFailure("The API did not answer. Check your connection and try again.");
    if (res.status === 401) return setFailure("That user name and password did not match.");
    if (!res.ok) return setFailure(`The API answered ${res.status}.`);
    signIn(v.user, v.password);
  };

  const err = formState.errors;
  return (
    <main id="main" className="login">
      <form className="login__card lr-panel" onSubmit={handleSubmit(onSubmit)} noValidate aria-labelledby="login-title">
        <h1 id="login-title">Loss Run Intelligence</h1>
        <p className="lr-meta">{ENVIRONMENT_LABEL}. Demo host sign-in.</p>
        {failure ? (
          <p className="lr-error" role="alert"><CircleAlert size={14} aria-hidden="true" />{failure}</p>
        ) : null}
        <div className="lr-field">
          <label className="lr-label" htmlFor="login-user">User name</label>
          <input id="login-user" className="lr-input" autoComplete="username" aria-invalid={!!err.user} aria-describedby={err.user ? "login-user-err" : undefined} {...register("user")} />
          {err.user ? <p id="login-user-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.user.message}</p> : null}
        </div>
        <div className="lr-field">
          <label className="lr-label" htmlFor="login-password">Password</label>
          <input id="login-password" type="password" className="lr-input" autoComplete="current-password" aria-invalid={!!err.password} aria-describedby={err.password ? "login-password-err" : undefined} {...register("password")} />
          {err.password ? <p id="login-password-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.password.message}</p> : null}
        </div>
        <button type="submit" className="lr-btn lr-btn--primary" disabled={formState.isSubmitting}>{formState.isSubmitting ? "Signing in…" : "Sign in"}</button>
      </form>
    </main>
  );
}
