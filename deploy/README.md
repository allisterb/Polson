# Deploying the studio to another Google Cloud project

Nothing in the source names a Google Cloud project, region, bucket or service URL. Everything that ties a
deployment to one is configuration, listed here in the order it has to exist. The service, the image repository
and the runtime account's project number all follow from the project; the rest is recreated by hand.

The commands are PowerShell. On a machine where gcloud cannot find Python, set
`$env:CLOUDSDK_PYTHON` to its bundled interpreter first. `$P` is the new project and `$R` its region:

```powershell
$P = 'polson-ad-studio'; $R = 'us-east4'
gcloud config set project $P
```

## 1. APIs

```powershell
gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com `
  secretmanager.googleapis.com logging.googleapis.com monitoring.googleapis.com --project $P
```

## 2. The runtime account

Cloud Run runs as the project's default compute account, `<project number>-compute@developer.gserviceaccount.com`:

```powershell
$N = gcloud projects describe $P --format='value(projectNumber)'
$SA = "$N-compute@developer.gserviceaccount.com"
```

## 3. Secrets

One per credential, each readable by the runtime account. The Gemini key belongs to the project it was issued in,
so its usage and billing stay there unless a new one is made in `$P`; the Qloo and Parallel keys are not Google
Cloud's and move as they are.

```powershell
foreach ($s in 'polson-agent-key', 'polson-parallel-key', 'polson-qloo-key') {
    Get-Clipboard | gcloud secrets create $s --project $P --replication-policy automatic --data-file=-
    gcloud secrets add-iam-policy-binding $s --project $P --member "serviceAccount:$SA" --role roles/secretmanager.secretAccessor
}
```

(Copy each value to the clipboard before its turn of the loop, or run the two lines once per secret.)

**For a public service only:** `polson-basic-auth`, holding `user:password`, the shared sign-in for everyone who
opens it (§5). A private service does not need it. Create it the same way:

```powershell
Get-Clipboard | gcloud secrets create polson-basic-auth --project $P --replication-policy automatic --data-file=-
gcloud secrets add-iam-policy-binding polson-basic-auth --project $P --member "serviceAccount:$SA" --role roles/secretmanager.secretAccessor
```

**Optional — Nemotron, for `Video.ask` and the first reading of `Documents.ask`.** Nemotron 3.5 Super VL runs on
NVIDIA's NIM API or Nebius Token Factory, not Google. With neither key, `Video.ask` refuses every call with
`NotConfigured`, `Documents.ask` reads with Gemini alone, and nothing else changes. Either key turns both on; with
both, the preferred service is tried first and the other is its backup.

**A deployment uses Token Factory, not NIM.** The engine prefers NIM by default, which suits local development, but
NIM's free tier is governed by the [NVIDIA API Trial Terms of Service](https://assets.ngc.nvidia.com/products/api-catalog/legal/NVIDIA%20API%20Trial%20Terms%20of%20Service.pdf)
(v. September 19, 2025, read 2026-10-21), and a deployed service is outside them:

- §1.2 and §1.4: trial use only — "internal testing and evaluation purposes, not in production" — without a paid
  subscription.
- §4.2: the API Service and its Generated Content may not be made available to others, which is what a service that
  judges or the public can drive does.
- §3.3(iv): NVIDIA may use what is sent and returned to improve its products, AI models included, so a supplied
  document that is confidential must never reach it. §2.6(a) and §4.3 exclude confidential, personal and regulated data
  outright.
- The rate limit is unpublished and the responses carry no rate-limit headers; developer-forum reports put it at
  40 requests a minute per key, shared by every visitor.

So on Cloud Run:

- **Token Factory**: create a `polson-nebius-key` secret, add `POLSON_NEBIUS_KEY=polson-nebius-key:latest` to
  `--set-secrets`, and add `POLSON_NEBIUS_BASE_URL=https://api.tokenfactory.us-north1.nebius.com/v1` (or your
  region's URL) **and `POLSON_NEMOTRON_PREFER=nebius`** to `--set-env-vars`. The base URL has no default, because the
  host is regional.
- **Leave `POLSON_NVIDIA_NIM_KEY` unset.** Kept as the backup, NIM would serve every read Token Factory failed — the
  same terms, reached whenever the paid service has a bad minute. With no backup, a failed Token Factory read falls
  back to Gemini for documents, and `Video.ask` answers with the failure.
- NIM on a deployment only under a paid NVIDIA subscription: then create a `polson-nvidia-nim-key` secret the same
  way and add `POLSON_NVIDIA_NIM_KEY=polson-nvidia-nim-key:latest` to `--set-secrets`. Its base URL defaults to
  `https://integrate.api.nvidia.com/v1`.

## 4. The bucket

Bucket names are global, so the current one cannot be reused:

```powershell
gcloud storage buckets create "gs://$P-artifacts" --project $P --location $R
```

## 5. The service

The service URL contains the project number, and `POLSON_SELF_URL` must be that URL: pointing it anywhere else
leaves the keepalive quietly holding nothing, and a run nobody watches stops (`src/adk_agent/keepalive.py`).

```powershell
$URL = "https://polson-studio-$N.$R.run.app"
gcloud run deploy polson-studio --source . --project $P --region $R `
  --no-allow-unauthenticated --cpu 4 --memory 4Gi --max-instances 1 --timeout 3600 `
  --set-secrets "POLSON_AGENT_PLATFORM_KEY=polson-agent-key:latest,POLSON_PARALLEL_KEY=polson-parallel-key:latest,POLSON_QLOO_KEY=polson-qloo-key:latest" `
  --set-env-vars "^;^POLSON_SELF_URL=$URL;POLSON_ARTIFACT_SERVICE_URI=gs://$P-artifacts;POLSON_MIRROR_URI=gs://$P-artifacts/studio;POLSON_ASK_SECONDS=300;POLSON_MAX_CREDIT_SECONDS=900;POLSON_BUDGET_TOKENS=3000000;POLSON_QLOO_BUDGET=360;POLSON_ASSETS_BUDGET=30;ADK_MAX_LLM_CALLS=500;POLSON_PEEK_STABLE_NAME=1;POLSON_ALLOW_ORIGINS=http://127.0.0.1:8080,http://localhost:8080"
gcloud run services add-iam-policy-binding polson-studio --project $P --region $R --member "serviceAccount:$SA" --role roles/run.invoker
```

`^;^` makes `;` the separator, because `POLSON_ALLOW_ORIGINS` contains a comma. The invoker grant is what lets the
keepalive reach a private service.

This deploy is **private**: only accounts with `roles/run.invoker` reach it, so it runs without basic auth and needs
no `polson-basic-auth` secret. The startup log says `basic auth OFF`, which is expected here.

### A public deploy

A public service is open to anyone with the URL, and behind it an agent that spends metered models and a Qloo key
on whatever a visitor types, so it **always carries basic auth**. Create the `polson-basic-auth` secret first (§3),
then deploy in two steps:

1. Deploy as above, still with `--no-allow-unauthenticated`, but with
   `POLSON_BASIC_AUTH=polson-basic-auth:latest` added to `--set-secrets`. The startup log must then say
   `basic auth on: every request needs the password for user '…'`. A value that is not `user:password` stops the
   revision starting, so the last good one keeps serving.
2. Only once the log says so, open it:

   ```powershell
   gcloud run services add-iam-policy-binding polson-studio --project $P --region $R --member allUsers --role roles/run.invoker
   ```

Never the other way round: a public revision without the secret is open to everyone.

Every route then needs the shared `user:password`: the studio, `/new`, ADK's API and its console at `/dev-ui/`.
Judges need only the user name and password. The keepalive sends the same credential with its own request
(`src/adk_agent/keepalive.py`), so runs nobody is watching keep their CPU.

Reach a private service with `gcloud run services proxy polson-studio --project $P --region $R --port 8080`.

## 6. Monitoring

`monitoring/` holds the log-based metrics and alert policies, exported without project ids or addresses. Make an
email notification channel in the console (Monitoring › Alerting › Edit notification channels), then:

```powershell
python deploy/monitoring/apply.py $P "projects/$P/notificationChannels/<id>"
```

Budgets belong to the billing account, not the project, and are not here.

## 7. Locally

`gcloud config set project $P`, and anything that names the old project: the assistant's memory notes, and the
`cloud-studio-proxy` entry in `.claude/launch.json`.
