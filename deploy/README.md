# Deploying the studio to another Google Cloud project

Nothing in the source names a Google Cloud project, region, bucket or service URL. Everything that ties a
deployment to one is configuration, listed here in the order it has to exist. The service, the image repository
and the runtime account's project number all follow from the project; the rest is recreated by hand.

**Keep it that way: real names stay out of the repository**, this file included. The studio is deployed without a
sign-in, so a project id, region or bucket name in a public repository is a head start for anyone probing it. Every
value below is a placeholder.

The commands are PowerShell. On a machine where gcloud cannot find Python, set
`$env:CLOUDSDK_PYTHON` to its bundled interpreter first. `$P` is the project, `$R` its region, `$S` the service and
`$B` its bucket:

```powershell
$P = '<project id>'; $R = '<region>'; $S = '<service name>'; $B = '<bucket name>'
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
  `--set-secrets`, and add `POLSON_NEBIUS_BASE_URL=<your Token Factory base URL>` **and
  `POLSON_NEMOTRON_PREFER=nebius`** to `--set-env-vars`. The base URL has no default, because the
  host is regional.
- **Leave `POLSON_NVIDIA_NIM_KEY` unset.** Kept as the backup, NIM would serve every read Token Factory failed — the
  same terms, reached whenever the paid service has a bad minute. With no backup, a failed Token Factory read falls
  back to Gemini for documents, and `Video.ask` answers with the failure.
- NIM on a deployment only under a paid NVIDIA subscription: then create a `polson-nvidia-nim-key` secret the same
  way and add `POLSON_NVIDIA_NIM_KEY=polson-nvidia-nim-key:latest` to `--set-secrets`. Its base URL defaults to
  `https://integrate.api.nvidia.com/v1`.

## 4. The bucket

One per service, private, readable and writable only by the runtime account. Bucket names are global, so choose
one that is not derived from the project id:

```powershell
gcloud storage buckets create "gs://$B" --project $P --location $R --uniform-bucket-level-access --public-access-prevention
gcloud storage buckets add-iam-policy-binding "gs://$B" --member "serviceAccount:$SA" --role roles/storage.objectAdmin
```

**Two services never share a bucket.** The studio lists every run under `POLSON_MIRROR_URI` and restores whichever
one is opened (`src/studio/archive.py`), so a shared mirror shows each service's visitors the other's work. A
separate prefix would fix only that: `POLSON_ARTIFACT_SERVICE_URI` reads the bucket name and drops any path, so two
services with a project of the same name would write the same artifact versions.

**Before deleting a bucket, repoint or delete every service that names it.** A deleted name can be created again
by anyone, and a service still configured for it would then write its runs into their bucket.

## 5. The service

The service URL contains the project number, and `POLSON_SELF_URL` must be that URL: pointing it anywhere else
leaves the keepalive quietly holding nothing, and a run nobody watches stops (`src/adk_agent/keepalive.py`).

```powershell
$URL = "https://$S-$N.$R.run.app"
gcloud run deploy $S --source . --project $P --region $R `
  --no-allow-unauthenticated --cpu 4 --memory 4Gi --max-instances 1 --timeout 3600 `
  --set-secrets "POLSON_AGENT_PLATFORM_KEY=polson-agent-key:latest,POLSON_PARALLEL_KEY=polson-parallel-key:latest,POLSON_QLOO_KEY=polson-qloo-key:latest" `
  --set-env-vars "^;^POLSON_SELF_URL=$URL;POLSON_ARTIFACT_SERVICE_URI=gs://$B;POLSON_MIRROR_URI=gs://$B/studio;POLSON_ASK_SECONDS=300;POLSON_MAX_CREDIT_SECONDS=900;POLSON_BUDGET_TOKENS=3000000;POLSON_QLOO_BUDGET=360;POLSON_ASSETS_BUDGET=30;POLSON_RESEARCH_PROCESSOR=core;ADK_MAX_LLM_CALLS=500;POLSON_PEEK_STABLE_NAME=1;POLSON_ALLOW_ORIGINS=http://127.0.0.1:8080,http://localhost:8080"
gcloud run services add-iam-policy-binding $S --project $P --region $R --member "serviceAccount:$SA" --role roles/run.invoker
```

`^;^` makes `;` the separator, because `POLSON_ALLOW_ORIGINS` contains a comma. The invoker grant is what lets the
keepalive reach a private service.

This deploy is **private**: only accounts with `roles/run.invoker` reach it, so it runs without basic auth and needs
no `polson-basic-auth` secret. The startup log says `basic auth OFF`, which is expected here.

### A public deploy

A public service is open to anyone with the URL, and behind it an agent that spends metered models, a Qloo key and
a research key on whatever a visitor types. It is opened one of two ways.

**Open, with no sign-in**, which is what a hackathon entry needs: judges are given a URL and nowhere to put a
password. Then nothing stands between a visitor and a run but the spend brakes — `POLSON_MAX_RUNS_PER_DAY` (25) and
`POLSON_MAX_CONCURRENT_RUNS` (2), counted in memory per instance (another reason for `--max-instances 1`), and each
run's `POLSON_BUDGET_TOKENS`. Set those to what you will pay for a day before opening it, and put a budget on the
billing account. The brakes are global, so a stranger who finds the URL can spend the day's runs before a judge
does; finished runs stay readable when the allowance is gone.

ADK's own API is served beside the studio and checks no allowance: `/run_sse` alone would start runs past it. On
Cloud Run `src/adk_agent/api_gate.py` answers it, the console and everything else outside the studio and `/new` with
a 404, and the console is off. The startup log must say `API gate on`. Deploy as above, then:

```powershell
gcloud run services add-iam-policy-binding $S --project $P --region $R --member allUsers --role roles/run.invoker
```

**Behind basic auth.** Create the `polson-basic-auth` secret first (§3), then deploy in two steps:

1. Deploy as above, still with `--no-allow-unauthenticated`, but with
   `POLSON_BASIC_AUTH=polson-basic-auth:latest` added to `--set-secrets`. The startup log must then say
   `basic auth on: every request needs the password for user '…'`. A value that is not `user:password` stops the
   revision starting, so the last good one keeps serving.
2. Only once the log says so, open it:

   ```powershell
   gcloud run services add-iam-policy-binding $S --project $P --region $R --member allUsers --role roles/run.invoker
   ```

Never the other way round: a public revision without the secret is open to everyone.

Every route then needs the shared `user:password`: the studio, `/new`, ADK's API and its console at `/dev-ui/`.
Judges need only the user name and password. The keepalive sends the same credential with its own request
(`src/adk_agent/keepalive.py`), so runs nobody is watching keep their CPU.

Reach a private service with `gcloud run services proxy $S --project $P --region $R --port 8080`.

### More than one service from one image

Two entries that differ only in configuration — one reading films with Gemini, one with Nemotron — are two
services on the same image, each with its own `$S`, `$B` (§4) and `POLSON_SELF_URL`. Deploy the first with
`--source .`, then the second from the image the first built, with `--image` in place of `--source .`:

```powershell
$IMAGE = gcloud run services describe $S --project $P --region $R --format='value(spec.template.spec.containers[0].image)'
```

`POLSON_VERIFY_READER=nemotron` makes Nemotron the reader of finished films; unset, it is Gemini. Each service keeps
its own run allowance, so two services can spend twice what one can.

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
