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

A slice can also ship pvginkel/IntercomClient, the Android app, and pvginkel/Architecture's
producer registry, each pushed after this repo's deploy. A push to IntercomClient's `main` makes
Jenkins job `Firmware/IntercomClient` sign its APK and publish it to this server, which offers it
to every installed app as its update, and `AaC/IntercomClient` validate the app's architecture
model: step 5. A push to Architecture's `main` rebuilds the estate's architecture viewer from every
registered producer: step 6.

No gate is known red: a failure in step 1 is this slice's and blocks.

There is no local live check. The server does nothing without the MQTT broker and the devices on
it; what a local boot would show, the suite shows with an in-process broker, and the rest only
production can.

**Standing rule, here and in every check against production: nothing audible in the house.** Never
publish to the production broker's command topics (`intercom/server/set/…`,
`intercom/server/devices/<id>/set/…`, and `<prefix>/<id>/set/…` under every device prefix,
`intercom/client` and `toilet_automation`), never
ring the doorbell, never start a call, a conversation or a sound. Those are the operator's checks,
named in the close-out. Everything below only reads.

## 0. Preconditions

The driver has ff-merged every code phase into the base branch. Confirm `git status --short` is
clean in this repo, in `/work/IntercomDeploy` and in `/work/IntercomClient`, and in
`/work/scratch/Architecture` when the slice changed it: a dirty tree means an earlier phase left
something behind, and that is a finding. Run every `kc project` verb from the repo's root; the CLI
is cwd-bound.

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

When the slice changed IntercomClient, its gates too, which run in the `android` and `aac-tools`
tool containers:

```bash
cd /work/IntercomClient && kc project build && kc project test && kc project lint
```

`build` is the release build, with Android Lint's fatal checks; `test` the JVM unit tests, which
its `Jenkinsfile`'s `Test` stage runs before the release build; `lint` the architecture model's
`arch-validate`, a network call read as this repo's is. The `android` container holds 5 GiB, and
one warm Gradle daemon has run it out of memory: a verb whose connection to it drops while
`kc env describe` says it restarted for memory is run again on its own, once, and a second drop is
*not verified*.

pvginkel/Architecture has no gate here: its verbs run in the `modern-app` tool container, which
this environment does not carry. Its proof is the `AaC/Architecture` build its push starts
(step 6).

## 2. Push, then follow the roll

Pushing is this phase's job, honouring any repo named in `plan.md`'s `## Push holds`. Rebase onto
`origin/main` first: docs may land concurrently, and Jenkins commits every image pin to
IntercomDeploy's `main`.

**When the slice changed IntercomDeploy, it goes first**, so that the chart already sets what the
new image needs (a new environment variable, prompt file or secret) when the image rolls. Its
landing rolls `intercom-prd` on its own, with the old image. The exception is a change that removes
what the old image still reads: then this repo goes first.

**IntercomClient goes last**, in step 5, once step 3 has found this repo's new pod healthy, so that
its build publishes to the server the slice deployed. Architecture's registry follows in step 6,
once the producer jobs its new entries name have a green build.

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

## 5. IntercomClient's release

When the slice changed pvginkel/IntercomClient, push it once step 3 has found the new pod healthy;
after a rollback in step 4 it is not pushed, and the report says so. **Its push is a production
deploy too**: the build publishes the APK to this server, and every installed app is offered it on
its next start. The repo's one push hook builds two jobs: `Firmware/IntercomClient` (`Jenkinsfile`)
tests, builds, signs, archives and publishes the APK, and `AaC/IntercomClient`
(`Jenkinsfile.architecture`) validates and archives the architecture model.

```bash
cd /work/IntercomClient
git fetch origin && git rebase origin/main && git push origin HEAD:main
SHA=$(git rev-parse HEAD)
timeout 3300 ~/.local/bin/track_build.py Firmware/IntercomClient --hash "$SHA" --diagnose
timeout 1800 ~/.local/bin/track_build.py AaC/IntercomClient --hash "$SHA" --diagnose
```

Neither job pushes to a deploy repo, so `track_build.py` follows the build alone, its exit statuses
as in step 2. `Publish apk` is `Firmware/IntercomClient`'s last stage, so a build red before it
published nothing: a finding. Its pod inherits `jenkins-agent`, whose build container has no memory
request or limit and holds what its node has free. A build the console shows killed for memory is a
finding whose fix is the job on `jenkins-agent-large`, a change to the pipeline guide's POD-3 list
in pvginkel/JenkinsPipelineUtils, made outside the slice.

The build, read through Jenkins' API with the environment's `JENKINS_USER` and `JENKINS_TOKEN`:

```bash
J=(-fsS -u "$JENKINS_USER:$JENKINS_TOKEN"); N=<n>    # <n>: the build track_build.py named
B="$JENKINS_URL/job/Firmware/job/IntercomClient/$N"
curl "${J[@]}" "$B/wfapi/describe" | python3 -I -c 'import json,sys; [print(s["name"], s["status"]) for s in json.load(sys.stdin)["stages"]]'
curl "${J[@]}" "$B/consoleText" | grep 'IntercomServer answered'
curl "${J[@]}" "$B/api/json?tree=artifacts%5BrelativePath%5D"; echo
```

It reads `Checkout`, `Test`, `Build apk` and `Publish apk`, each `SUCCESS`; one line
`IntercomServer answered 201`; and the one artifact, `app-release.apk`.

Then the APK it archived, in a directory of its own, read in the `android` tool container:

```bash
D=/work/scratch/intercom-client-$N; mkdir -p "$D" && cd "$D"
curl "${J[@]}" -o app-release.apk "$B/artifact/app-release.apk"
cexec android sh -c '
aapt2 dump badging app-release.apk | grep -E "^(package|minSdkVersion|targetSdkVersion|uses-permission):"
res() { aapt2 dump resources app-release.apk | sed -n "\| $1\$|{n;s/.*(file) \([^ ]*\).*/\1/p}"; }
aapt2 dump resources app-release.apk | grep -E " (xml/network_security_config|raw/homelab_root)\$"
aapt2 dump xmltree app-release.apk --file "$(res xml/network_security_config)"
unzip -p app-release.apk "$(res raw/homelab_root)" | cmp - /work/IntercomClient/app/src/main/res/raw/homelab_root.crt && echo "bundled root: the same file"
apksigner verify --verbose --print-certs app-release.apk | grep -E "^(Verifies|Verified using v[23] |Signer #1 certificate SHA-256)"'
```

It reads:

- `package: name='org.webathome.intercom' versionCode='<n>' versionName='<n>'`, the build's own
  number as both, then `minSdkVersion:'26'` and `targetSdkVersion:'35'`;
- the permissions, exactly: `android.permission.RECORD_AUDIO`, `REQUEST_INSTALL_PACKAGES`,
  `INTERNET`, `ACCESS_NETWORK_STATE` and `WAKE_LOCK`, and androidx.core's own signature permission,
  `org.webathome.intercom.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`. Another is a finding unless
  the slice's criteria name it;
- the network security config: one `domain-config`, its one `domain` this server's name (`$HOST`
  below) with `includeSubdomains=false`, and its `trust-anchors` one `certificates` whose
  `src=@0x…` is the id the resource line gives `raw/homelab_root`. No `base-config`, so every
  other host keeps the system's trust. Then `bundled root: the same file`;
- the signature: `Verifies`, v2 and v3 `true`, and `Signer #1 certificate SHA-256 digest: <d>`.
  `<d>` differs from what the same command reads of `MyDownloads/MyDownloadsClient`'s last
  successful `app-release.apk`, since the key is IntercomClient's own `keystore.jks`. From the
  job's second build on, `<d>` is the previous build's: Android installs an update only over an
  app the same key signed.

Then the deployed proof of the publish, with no token, on the name and root the app uses:

```bash
HOST=$(sed -n 's/^  serverName: //p' /work/IntercomDeploy/config/prd/values.yaml)
CA=/work/IntercomClient/app/src/main/res/raw/homelab_root.crt
curl -fsS --cacert "$CA" "https://$HOST/updates?action=version"; echo
curl -fsS --cacert "$CA" -o "$D/served.apk" "https://$HOST/updates?action=download" && cmp "$D/served.apk" "$D/app-release.apk" && echo "served: the archived apk"
```

The first answers `<n>`, the build's number: the release every installed app is now offered. The
second shows the server serves the very file the build archived. A release whose checks above fail
is offered all the same: a blocking finding, reported at once. The server offers the highest
version code it holds and has no route that withdraws one, so only a later good build replaces it.

Once IntercomClient's producer is registered (step 6), a green `AaC/IntercomClient` build starts
`AaC/Architecture` itself, which `track_build.py` never follows from an architecture job: when the
slice changed IntercomClient's model, read the report of the `AaC/Architecture` build that started
after it, as step 6 reads one.

**The phone's checks are the operator's**, named in the close-out; nothing here installs the app,
signs in or touches its screen:

- the first install, of the signed `app-release.apk` this build archived, from the build's page in
  Jenkins: the app starts on its sign-in;
- the screen, against production: signing in, the list, playing, renaming, disabling, enabling and
  deleting, recording and uploading, as the slice's criteria word them;
- the update offer, once a later push to IntercomClient's `main` has published a release above the
  installed one: the app's start offers it, and before its first install Android asks the operator
  to let Intercom install apps.

A criterion `verification.json` marks owed after one of them is marked as the operator's, never as
passed.

## 6. Architecture's producer registry

When the slice changed pvginkel/Architecture's `pipeline-producers.yaml`, checked out at
`/work/scratch/Architecture`, push it only once each producer job its new entries name has a green
build: `AaC/Architecture` copies every registered producer's last successful artifacts, and a job
with none fails it. For IntercomClient's producer, that job is step 5's `AaC/IntercomClient`:

```bash
J=(-fsS -u "$JENKINS_USER:$JENKINS_TOKEN")
curl "${J[@]}" "$JENKINS_URL/job/AaC/job/IntercomClient/lastSuccessfulBuild/api/json?tree=number,result"; echo
```

It answers the build's number and `SUCCESS`; a 404 is no green build, and the push waits.

```bash
cd /work/scratch/Architecture
git fetch origin && git rebase origin/main && git push origin HEAD:main
timeout 3300 ~/.local/bin/track_build.py AaC/Architecture --hash "$(git rev-parse HEAD)" --diagnose
```

The build collects every producer's model, validates the federation, builds the
`architecture_viewer` image and pins it into the estate's deploy repo, whose roll `track_build.py`
follows; its exit statuses read as in step 2. The job waits 90 seconds in the queue first
(`quietPeriod(90)`), and any producer's green build starts it too and aborts the one under way: a
build aborted so is not a failure, and `track_build.py AaC/Architecture --buildnr <m>` follows the
one that replaced it. A red build pinned nothing, since collection and validation run before the
image is built, and the viewer stays on its last image: a finding. So is a failed roll, with the
diagnosis `track_build.py` wrote. Its pin commit is Jenkins', and nothing is reverted from here.

Then its validation report. The collector runs `--relaxed`, so an edge naming an id no producer
declares is a warning, and the build stays green:

```bash
M=<m>    # <m>: the build track_build.py named
curl "${J[@]}" "$JENKINS_URL/job/AaC/job/Architecture/$M/artifact/dist/data/v0.1/validation-report.json" | python3 -I -c '
import json,sys
r=json.load(sys.stdin); print(r["summary"])
for w in r["warnings"]: print(w["kind"], w.get("producer"), w.get("reference", ""))'
grep -c '^  - id:' /work/scratch/Architecture/pipeline-producers.yaml
```

The summary's `producers` is the pushed list's entry count, the last line, and no warning's
producer is a new entry's id. A `dangling-reference` from `intercom-client` is an edge of
IntercomClient's model that names no element of the federation: a finding against that model.
Other producers' warnings are not the slice's.

## 7. Check off `verification.json`

Mark each acceptance criterion with the evidence that settled it: the command run and what it
returned. A criterion nothing in steps 1 to 6 exercised is an untested criterion (a finding) or a
check missing from this doc, not "passed by inspection". An operator's check is marked as theirs,
never as passed.

## Findings

Blocking findings come back as appended phases; sub-bar findings go in the close-out report. A check
that cannot be run is reported as *not verified*, never as passed.
