# Load test (ADR 0014)

Azure Load Testing definition for 25 concurrent underwriters, plus a 2-user smoke run. The procedure, pass/fail criteria and how
to read the results are in `docs/operations/runbook.md` §Load test; the measured runs are in `docs/evaluation/results/*-loadtest/`.

| File | What it is |
|---|---|
| `config.yaml`, `smoke.yaml` | Test definitions (full run, smoke run) |
| `underwriters.jmx` | The JMeter plan; reads `host`, `quote`, `users`, `duration`, `ramp`, `docs` as properties |
| `user.properties`, `smoke.properties` | Those properties for each run |
| `lossrun-export.csv` | Upload input |
| `server-metrics.kql` | Server-side evidence for a run window (Log Analytics) |
| `summarize.py` | Summarizes the downloaded results CSV |

**The committed files name no environment.** They hold tokens that `render.ps1` fills from the Terraform outputs of `infra/`:

| Token | Terraform output |
|---|---|
| `{{keyvault_uri}}` | `key_vault_uri` (the `devauth-users` secret the plan signs in with) |
| `{{function_host}}` | `function_app_hostname` |
| `{{function_app_name}}` | `function_app_name` |

```powershell
powershell -File infra/loadtest/render.ps1        # writes infra/loadtest/out/ (gitignored)
az load test update --load-test-resource <load testing resource> -g <resource group> --test-id lossrun-12b-underwriters --load-test-config-file infra/loadtest/out/config.yaml
```

`docs` in the properties files lists document ids of the POC environment's load-test quote; on another environment, replace
them with the ids of a quote seeded there.
