# Slice testing strategy

How a slice is proven once its phases are merged. This is the doc `.aiworkflowrc` names as
`test_phase.strategy`: the run loop's test phase is "read this and execute it". Read it top to
bottom and do what it says.

## What this phase proves

**Verification is local, then the push, then production.** This repo has one deployment,
production, and no dev instance. A push to `main` makes Jenkins job `Firmware/IntercomServer`
publish `registry:5000/intercom-server:<n>` and pin it into pvginkel/IntercomDeploy
(`config/prd/values.yaml`, `images.intercomServer`), from which Argo CD's Application
`intercom-prd` rolls Deployment `intercom` in namespace `intercom-prd`. **The push to `main` is the
production deploy.** The Deployment is `Recreate`: the intercoms, the doorbell and the ChatGPT
replies are down from the old pod's stop until the new one has connected to MQTT, a few minutes.

No gate is known red: a failure in step 1 is this slice's and blocks.

There is no local live check. The server does nothing without the MQTT broker and the devices on
it; what a local boot would show, the suite shows with an in-process broker, and the rest only
production can.

**Standing rule, here and in every check against production: nothing audible in the house.** Never
publish to the production broker's command topics (`intercom/server/set/…`, and
`<prefix>/<id>/set/…` under every device prefix, `intercom/client` and `toilet_automation`), never
ring the doorbell, never start a call, a conversation or a sound. Those are the operator's checks,
named in the close-out. Everything below only reads.

## 0. Preconditions

The driver has ff-merged every code phase into the base branch. Confirm `git status --short` is
clean in this repo and in `/work/IntercomDeploy`: a dirty tree means an earlier phase left something
behind, and that is a finding. Run every `kc project` verb from the repo's root; the CLI is
cwd-bound.

## 1. The suites

```bash
kc project build    # IntercomServer.csproj, never the solution
kc project test     # IntercomServer.Tests, against the real FFmpeg
kc project lint     # arch-validate on docs/architecture/*.yaml, a network call
```

All three must exit 0. Read each verb's own exit status, never a pipe's. The suite runs the
`ffmpeg` that `kc project setup` installs in the `dotnet` sidecar, and a sidecar that restarted on
its own comes back without it: the suite then fails on `FFmpeg is missing: …`, and
`kc project setup` puts it back before the suite runs again.

`lint` posts the artifact to the federation's validation service. A refusal is a finding; a
network failure is *not verified*, reported as such, never passed.

When the slice changed pvginkel/IntercomDeploy, its gates too, which run in the `iac` and
`aac-tools` tool containers:

```bash
cd /work/IntercomDeploy && kc project test && kc project lint
```

## 2. Push, then follow the roll

Pushing is this phase's job, honouring any repo named in `plan.md`'s `## Push holds`. Rebase onto
`origin/main` first: docs may land concurrently, and Jenkins commits every image pin to
IntercomDeploy's `main`.

**When the slice changed IntercomDeploy, it goes first**, so that the chart already sets what the
new image needs (a new environment variable, prompt file or secret) when the image rolls. Its
landing rolls `intercom-prd` on its own, with the old image. The exception is a change that removes
what the old image still reads: then this repo goes first.

**IntercomDeploy reaches `main` only at a SHA a plan Job has read.** Its PreSync hook applies its
Terraform, the Keycloak clients among it, from whatever reaches `main`, so every commit there goes
to a topic branch first, and `main` takes it once a plan Job of stage `prd` has read it at its SHA
(Ansible's runbook `docs/runbooks/oidc-client.md`, "Add an OIDC client to an app"). Starting that
Job is a write this environment cannot make: Pilot runs it through an Ansible work item. So the
push takes two passes of this phase. The first stops on the plan Job, and the retry, which runs
this doc from the top again, reads the plan and lands `main`. Which pass this is, `origin` says:

```bash
cd /work/IntercomDeploy
git fetch origin && git rebase origin/main
BRANCH=slice-<NNN>; SHA=$(git rev-parse HEAD)    # <NNN>: the slice's number
git rev-parse origin/main; git rev-parse -q --verify "origin/$BRANCH"
```

- **`$SHA` is `origin/main`**: IntercomDeploy has nothing of the slice left to push. Where
  `origin/$BRANCH` still stands, a pass landed `main` and ended before it had followed the roll:
  run the landing block's last three lines first, from `track_build.py` on, so that the roll is
  confirmed and the hook's `Apply complete!` line read. Then go on to this repo.
- **The branch is missing, or its head is not `$SHA`**: the plan to read is still to come. Push the
  branch, `git push --force-with-lease origin "HEAD:refs/heads/$BRANCH"`, and stop with `blocked`,
  naming the action for Pilot's Ansible work item with `$SHA` in full: "Run the plan Job of Ansible
  `docs/runbooks/argocd.md` § Planning a stage's Terraform as its hook applies it, for
  IntercomDeploy's stage `prd`, `APP_PATH` empty and `NS=intercom-prd`, at `<SHA>`, the head of its
  branch `slice-<NNN>`. Leave the Job standing: skip the sheet's last `delete job`." A moved
  `origin/main` brings the run back here, since the rebase gives a new SHA.
- **The branch's head is `$SHA`**: read the plan. The read-only kubeconfig in `iac` reads
  `argocd-hooks`' Jobs and their logs:

```bash
J=(cexec iac kubectl --context prd -n argocd-hooks)
"${J[@]}" get pods -l job-name=tf-plan-intercom-prd -o jsonpath='{.items[0].status.containerStatuses[0].state.terminated.exitCode}{"\n"}'
"${J[@]}" logs job/tf-plan-intercom-prd | grep '^presync: '
```

A Job that is gone left its log in Elasticsearch, each attempt a pod of its own name; read the
newest:

```bash
curl -s -u "$ELASTIC_USER:$ELASTIC_PASSWORD" "$ELASTIC_URL/filebeat-*/_search" -H 'Content-Type: application/json' -d '{"size":5000,"sort":[{"@timestamp":"asc"}],"_source":["kubernetes.pod.name","message"],"query":{"bool":{"filter":[{"term":{"kubernetes.namespace":"argocd-hooks"}},{"wildcard":{"kubernetes.pod.name":"tf-plan-intercom-prd-*"}}]}}}' | python3 -I -c '
import json,sys
for h in json.load(sys.stdin)["hits"]["hits"]:
    s=h["_source"]; m=s.get("message","")
    if m.startswith("presync: "): print(s["kubernetes"]["pod"]["name"], m)'
```

The plan lands `main` only when all of these hold:

- it planned `$SHA` as stage `prd`: `presync: cloning https://github.com/pvginkel/IntercomDeploy.git
  at <SHA>` and `presync: terraform runs as stage=prd, namespace=intercom-prd`;
- it finished: exit code `0`, and last `presync: plan only: nothing was applied and no volume was
  reattached`. Read from Elasticsearch, which keeps no exit code, that last line stands for it: the
  hook prints it as its final step, once the plan has succeeded;
- `presync: the plan carries N change(s):` lists the changes the slice's acceptance criteria name,
  each `presync:   <address>: <action>` with the action they name, and nothing else.

A change they do not name, one with another action (a `replace` where they name an `update`), or
a plan that failed stops the push: stop with `blocked`, quoting the change list as read. No plan of
`$SHA` stops it too, a Job that was never run or one that cloned another SHA (the Ansible sheet's
`REV` defaults to the head of `main`): stop with `blocked`, naming the plan Job's action as the
bullet that pushes the branch words it, with `$SHA` in full. Otherwise `main` fast-forwards to the
SHA the plan read, the roll is followed, and the topic branch goes:

```bash
git push origin "$SHA:refs/heads/main"
timeout 3300 ~/.local/bin/track_build.py --deploy IntercomDeploy main "$SHA" --roll-timeout 1800
"${J[@]}" logs job/tf-presync-intercom-prd | grep 'Apply complete!'
git push origin --delete "$BRANCH"
```

A push refused as no fast-forward means `origin/main` moved after the rebase: run this step again
from the top, which asks for the plan again. The hook's apply reads `Apply complete! Resources: <n>
added, <m> changed, 0 destroyed.`, `<n>` and `<m>` the plan's creates and updates.

Then this repo, once IntercomDeploy's `main` has rolled. **This is the production deploy.**

```bash
cd /work/IntercomServer
git fetch origin && git rebase origin/main && git push origin HEAD:main
timeout 3300 ~/.local/bin/track_build.py Firmware/IntercomServer --hash "$(git rev-parse HEAD)" --roll-timeout 1800 --diagnose
```

`track_build.py` follows the build, the pin commit it pushes to IntercomDeploy and Argo CD's roll of
it, and its summary says how each went. Budget about 40 minutes: the build takes 3 to 13, and the
roll about 18 (on 2026-11-22: a 12-minute Terraform PreSync hook, then five minutes until the
container started), which is why `--roll-timeout` is raised from its 10-minute default. Its exit
status:

- `0`: the build is green and `intercom-prd` rolled the pin commit.
- `1`: the build failed; `--diagnose` printed the console tail. A finding.
- `3`: an operational problem (auth, a build that never appeared, a fetch). Fix the cause and run it
  again; the build is not re-run.
- `5`, `6`, `7`: the roll failed, timed out, or Argo CD did not pick it up. The diagnosis it wrote to
  disk is named in the summary. A finding.

A `NOT_BUILT` build superseded by a newer push is not a failure: read the build that replaced it.
`Healthy` in Argo CD is not proof of a good deploy on its own: the chart has no probes, so the pod
is `Ready` the moment its container starts. Step 3 is the proof.

## 3. The deployed checks

Read through Argo CD's API with the environment's token; there is no `kubectl` in this recipe.

```bash
A=(-s -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN"); B="https://$ARGOCD_SERVER/api/v1/applications/intercom-prd"
curl "${A[@]}" "$B" | python3 -I -c 'import json,sys; s=json.load(sys.stdin)["status"]; print(s["sync"]["status"], s["sync"]["revision"][:7], s["health"]["status"], s["operationState"]["phase"], *s["summary"]["images"])'
POD=$(curl "${A[@]}" "$B/resource-tree" | python3 -I -c 'import json,sys; print(max((n for n in json.load(sys.stdin)["nodes"] if n["kind"]=="Pod"), key=lambda n: n["createdAt"])["name"])'); echo "$POD"
curl "${A[@]}" -G "$B/resource" --data-urlencode resourceName=$POD --data-urlencode namespace=intercom-prd --data-urlencode kind=Pod --data-urlencode version=v1 --data-urlencode group= | python3 -I -c 'import json,sys; m=json.loads(json.load(sys.stdin)["manifest"]); [print(c["name"], c["image"], "ready" if c["ready"] else "NOT READY", "restarts", c["restartCount"]) for c in m["status"]["containerStatuses"]]'
```

The first line reads `Synced <pin commit> Healthy Succeeded` with
`registry:5000/intercom-server:<n>`, `<n>` the build `track_build.py` named. The container line reads
`intercom-server registry:5000/intercom-server:<n> ready restarts 0`.

Then the new pod's start, from its own log:

```bash
curl "${A[@]}" -G "$B/pods/$POD/logs" --data-urlencode container=intercom-server --data-urlencode tailLines=400 | python3 -I -c '
import json,re,sys
for l in sys.stdin:
    c=json.loads(l).get("result",{}).get("content","") if l.strip() else ""
    if re.match(r"\[[0-9:]+ (INF|WRN|ERR)\]",c) and "Registered MCP tool " not in c: print(c[:110])'
```

A healthy start reads, within about ten seconds of the container starting:

1. `Now listening on: http://[::]:8080`, first: the web host;
2. `Decoded the built-in sounds with FFmpeg: doorbell.mp3 3.10 s, rejected.mp3 1.65 s, ring.mp3
   6.26 s`, the proof that the image's FFmpeg works: a server whose FFmpeg is missing or cannot
   decode stops here, naming the cause;
3. `Found ffprobe <version>; the ambient tracks are in /data/ambient`, the proof that the image
   carries ffprobe: a server whose ffprobe is missing stops here, naming the cause;
4. `ChatGPT enabled; devices will stream audio to <AUDIO_HOST>:5004`;
5. `Received state for device 0x…` and `Received configuration for device 0x…`, one for each device:
   the broker's retained messages, and the only proof of the MQTT connection, which succeeds
   silently;
6. `Registered <N> MCP tool(s) across 6 server(s)`, `<N>` varying with the MCP servers.

A failure is any of: a line of the six missing; `Initial MQTT connection failed`; `MQTT connection
lost` with no later `Reconnected to the MQTT server`; an `[ERR]` or `[WRN]` line not named as noise
below; a container not ready or restarted.

Known noise, not a finding: `Failed to connect to MCP server youtrack` (or `homeassistant`), a 502
from that server's proxy; the server starts without its tools. A broker restart during the roll can
delay the `Received …` lines until `Reconnected to the MQTT server`: judge by the end state.

After the start the log is silent until a device acts, so an empty window is not a failure. For a
pod that is gone, Elasticsearch holds every line:

```bash
curl -s -u "$ELASTIC_USER:$ELASTIC_PASSWORD" "$ELASTIC_URL/filebeat-*/_search" -H 'Content-Type: application/json' -d '{"size":500,"sort":[{"@timestamp":"asc"}],"_source":["@timestamp","message"],"query":{"bool":{"filter":[{"term":{"kubernetes.namespace":"intercom-prd"}},{"term":{"kubernetes.pod.name":"'"$POD"'"}},{"term":{"kubernetes.container.name":"intercom-server"}}]}}}'
```

Then the slice's own checks against production, read-only and bound by the standing rule above.
What only a person in the house can check (a sound, the doorbell, a ChatGPT reply, the phone's
screen) goes in the close-out as the operator's check, with what to listen for.

## 4. A pod that does not come up

A red build leaves production on the previous image: a finding, nothing more. A roll that leaves the
server down, a crash loop or a start that never reaches MQTT, means the doorbell is dead. **Roll back
first, then report:** revert the slice's own pin commit in IntercomDeploy, the one `track_build.py`
named, and push it to `main` at once. A pin commit, and so its revert, changes nothing the hook
applies: it reaches `main` with no plan Job, as Jenkins' own pin commits do.

```bash
cd /work/IntercomDeploy && git fetch origin && git rebase origin/main
git revert --no-edit <pin commit> && git push origin HEAD:main
timeout 3300 ~/.local/bin/track_build.py --deploy IntercomDeploy main "$(git rev-parse HEAD)" --roll-timeout 1800
```

Then repeat step 3 against the pod the revert rolled, and report the cause as a blocking finding.

A roll of IntercomDeploy's own commits, before this repo's push, that leaves the server down has no
pin commit to revert. Its revert would be a commit like any other, through the plan Job: stop with
`blocked` at once, with the roll's diagnosis.

## 5. Check off `verification.json`

Mark each acceptance criterion with the evidence that settled it: the command run and what it
returned. A criterion nothing in steps 1–3 exercised is an untested criterion (a finding) or a check
missing from this doc, not "passed by inspection". An operator's check is marked as theirs, never
as passed.

## Findings

Blocking findings come back as appended phases; sub-bar findings go in the close-out report. A check
that cannot be run is reported as *not verified*, never as passed.
