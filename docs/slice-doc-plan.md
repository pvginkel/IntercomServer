# Slice documentation plan

How a shipped slice brings this repo's documentation up to date. This is the doc `.aiworkflowrc`
names as `doc_phase.plan`: the run loop's doc phase is "read this and execute it", from the slice's
whole diff, in one pass.

## The surfaces

1. **`docs/`** holds the topic docs:
   - [`devices.md`](devices.md), the devices: how the server finds them under its topic prefixes,
     the microphone, the doorbell's reach, what a device is sent (its format, packets and
     counter), the built-in sounds and FFmpeg, and their environment variables. A slice that
     changes any of them updates it.
   - [`CHATGPT_MCP.md`](CHATGPT_MCP.md), the ChatGPT bridge: how a conversation starts and ends,
     its environment variables, the prompt placeholders, the MCP server configuration and the
     memory store. A slice that changes any of them updates it.
   - The three procedure docs, [`change-discipline.md`](change-discipline.md),
     [`slice-test-plan.md`](slice-test-plan.md) and this one. A slice that changed how the repo is
     changed, verified or documented updates the matching doc: a new test-suite convention goes in
     the change discipline's testing section, and a new start line or check in the test plan.
   - A part of the server worth a page of its own (a new subsystem, such as a web host or the
     ambient player) gets a topic doc here, named for what it covers.
2. **The architecture model, `docs/architecture/architecture.yaml`**, is hand-authored here. A slice
   that added or removed an external edge (an interface, a dependency, an MQTT namespace) and did not
   update it in its code phases updates it now, keeping the existing ids stable.
3. **`.kubecoder/project.yaml`'s comments** say why each verb is what it is. A slice that changed a
   verb brings its comment along.
4. **pvginkel/IntercomDeploy's `README.md`**, checked out at `/work/IntercomDeploy`, when the slice
   changed the deploy's shape: a new secret, volume, port or Keycloak client. The chart's
   `chart/files/*.md` are the server's prompts, product behaviour rather than docs: never edited
   here.
5. **Another repo the slice created or changed** follows its own slice doc plan where it has one,
   else its `README.md`.

**Consumers in other repos are named, not edited.** The device firmware, the ToiletAutomation
design and Home Assistant's automations read this server's MQTT and UDP contracts. Where the slice
changed one, or the implementation diverged from a design doc another repo keeps, the close-out says
what changed and which doc it touches; this phase never edits that repo.

There is no README and no `CLAUDE.md` in this repo, and the doc phase writes neither unless the
slice asked for one.

## What "up to date" means

**State the design as it is**, as implemented. Where the implementation diverged from the plan,
the doc describes what shipped. No changelog entries, no "as of slice NNN", no tombstones: rewrite
the doc, per [`change-discipline.md`](change-discipline.md). **Ground every claim in the shipped
source**; a sentence that cannot be checked against the merged tree does not go in.

This repo is public: no hostnames, IP addresses or tokens of the operator's network in its docs.
IntercomDeploy is private, and is where those belong.

## Gates

The doc phase must not move code:

```bash
kc project lint       # still green: arch-validate on docs/architecture/*.yaml
```

Check that relative links resolve, then commit.

## When there is little to do

A slice that changed no design, convention, contract or reader-facing surface owes nothing here,
and saying so plainly is the correct outcome. Do not invent doc work.
