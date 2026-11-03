# ADR-0009: Canary delivery and automated analysis

- **Status:** proposed
- **Date:** 2026-11-03

## Context

ADR-0003 shipped every release as a Kubernetes RollingUpdate and deferred canary delivery until the release and recovery paths were proven. A rolling update only knows whether pods start and pass their probes. A build that starts cleanly and then fails a share of real requests replaces every pod before anyone notices, and the verifier's smoke test sees only a handful of requests.

The cluster had no metrics backend. The application already exported OpenTelemetry metrics, including `http.server.request.duration` with the release sequence as `service.version`, but nothing stored them.

## Decision

1. **A delivery platform component** (`deploy/platform/components/delivery-platform/`) is installed once per cluster and names no application: an OpenTelemetry Collector, Prometheus with its native OTLP receiver (`service.name` and `service.version` promoted to labels, two days of retention), the Argo Rollouts controller (vendored, pinned by digest), and a generic `ClusterAnalysisTemplate` named `http-canary`. It is written to move to platform-guardrails unchanged.
2. **The application is an Argo `Rollout`** with the same pod spec as before. Steps: half the replicas move to the new release, then `http-canary` runs, then the rest follow. There is no traffic router. The Service selects both versions, so traffic splits by replica count.
3. **`http-canary`** judges only the canary version's own requests, with health probes excluded. Two metrics are measured four times, 20 seconds apart, after a 30-second delay: the 5xx ratio must stay at or below 1% (no failures allowed), and p95 latency must stay at or below 0.5 seconds (one failure allowed). A query that returns no sample is an error, never a pass, and repeated errors fail the analysis.
4. **A failed analysis or a stalled canary aborts the Rollout** (`progressDeadlineAbort: true`). The stable pods keep serving, and the Rollout reports `Degraded`.
5. **Flux and the verifier understand Rollouts.** The `orders-app` Kustomization uses CEL health checks: healthy when `phase == 'Healthy'`, failed when `phase == 'Degraded'`. Both checks require `observedGeneration == generation`, because right after a new release is applied the status still describes the previous one, which may itself have aborted. The verifier reads the Rollout. It fails the release as soon as the canary for that release's image is aborted, and it passes only once the Rollout is `Healthy` and the smoke test succeeds. Recovery is unchanged: roll forward to the last verified content (ADR-0006).
6. **A synthetic client** (`deploy/platform/base/synthetic-traffic.yaml`) creates and reads orders about five times a second, because the demo environments have no users and the analysis needs requests.

## Alternatives rejected

| Option | Why not |
| ------ | ------- |
| Argo CD in place of Flux | Delivery is already pull-based from immutable OCI artifacts, with a locked-down Flux tenant (ADR-0003, ADR-0004). Rollouts works with any applier, so changing appliers would buy nothing. |
| ALB or service-mesh traffic splitting | The cluster has no ingress, and replica-weighted splitting proves the same decision logic. This is a later step once there is a real edge. |
| Flagger | It needs a mesh or ingress for its main mode and would replace rather than extend the existing Deployment-style manifests. |
| Analysis on the verifier's smoke test alone | A few requests cannot detect a partial error rate. A 30% failure rate passed probes in testing. |

## Consequences

- Local scenario 12 proves the point. A build failing about 30% of order requests passed every probe, the first measurement read an error ratio of 0.286, the Rollout aborted, and only stable pods kept serving.
- A release now takes about two minutes longer. The Flux timeout for `orders-app` is 8 minutes.
- The Argo Rollouts CRDs exceed the client-side apply annotation limit, so the platform layer is applied with server-side apply.
- Kustomize does not know the Rollout kind. `rollout-transform.yaml` tells it where the pod template annotations are, so the release annotations reach the pods. Image digests need no entry.
- **Known limit:** the deployment guard identifies a release by image digest. A change to the manifests alone (as this conversion was) returns `NoChange` and does not ship until the image changes. To be fixed before the pipeline is extracted for other applications.
- Not yet done: AWS overlays for the platform component (mirrored images in ECR) and traces to CloudWatch on EKS.
