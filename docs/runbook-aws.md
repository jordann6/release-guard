# AWS demo runbook (Phase 5)

Deploy, prove, demo, destroy. Nothing hourly is left running. The operator runs every
apply and destroy from a reviewed saved plan. Infrastructure lives in
`~/aws-landing-zone`; this repository only consumes its deployment contract.

## Before starting

- State the projected cost from current pricing and get approval. Network plus workload
  is the hourly line (hub firewall, NAT, transit gateway, endpoints, EKS, nodes,
  Multi-AZ PostgreSQL). The delivery root adds no hourly resources.
- The workload's backup vault lock has a three-day grace period. Plan teardown inside it,
  and clear recovery points before destroying the vault.
- Tools: AWS CLI with the session-manager-plugin, terraform, kubectl, flux 2.9.6, crane,
  docker buildx, jq, gh.
- An AWS profile for the prod account, used by the scripts here
  (`AWS_PROFILE=release-guard-prod` by default):

  ```sh
  aws configure set profile.release-guard-prod.source_profile terraform-user
  aws configure set profile.release-guard-prod.role_arn arn:aws:iam::<prod-account-id>:role/OrganizationAccountAccessRole
  aws configure set profile.release-guard-prod.region us-east-1
  ```

## Terraform and AWS_PROFILE

The state backend lives in the management account. If `AWS_PROFILE=release-guard-prod`
is exported, Terraform reads state as the prod account and the S3 backend returns 403.
Run Terraform, and the scripts below that call `terraform output`, through a wrapper
that drops the profile while the AWS CLI calls in the scripts keep it:

```sh
mkdir -p /private/tmp/rg-cli-shim
printf '#!/bin/sh\nunset AWS_PROFILE\nexec /opt/homebrew/bin/terraform "$@"\n' > /private/tmp/rg-cli-shim/terraform
chmod +x /private/tmp/rg-cli-shim/terraform
export PATH=/private/tmp/rg-cli-shim:$PATH
```

Every `terraform` command and script in this runbook assumes that PATH.

## 1. Infrastructure (operator applies)

Each root reads a gitignored, mode-0600 `phase-c.tfvars.json`. The prepare script writes
the network file, and with `--workload` (after the network applies) the workload file.
Write `delivery/phase-c.tfvars.json` by hand with the keys in `delivery/example.tfvars`
plus `github_repository`.

```sh
python3 /Users/jordannelson/aws-landing-zone/scripts/prepare-workload-inputs.py
terraform -chdir=/Users/jordannelson/aws-landing-zone/network plan -var-file=phase-c.tfvars.json -out tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/network apply tfplan
python3 /Users/jordannelson/aws-landing-zone/scripts/prepare-workload-inputs.py --workload
terraform -chdir=/Users/jordannelson/aws-landing-zone/workload plan -var-file=phase-c.tfvars.json -out tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/workload apply tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/delivery init
terraform -chdir=/Users/jordannelson/aws-landing-zone/delivery plan -var-file=phase-c.tfvars.json -out tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/delivery apply tfplan
```

Publish the deployment contract (ADR-0005) with platform-guardrails, exactly as
`tf-apply.yml` does after a CI apply:

```sh
AWS_PROFILE=release-guard-prod /Users/jordannelson/platform-guardrails-v1.5.0/scripts/publish-contract.sh \
  /Users/jordannelson/aws-landing-zone/delivery /delivery/release-guard/prod/contract \
  /Users/jordannelson/aws-landing-zone/delivery/contract-allowlist.txt
```

## 2. Platform bootstrap (once per environment)

```sh
/Users/jordannelson/projects/release-guard/scripts/aws/mirror-images.sh
/Users/jordannelson/projects/release-guard/scripts/aws/eks-tunnel.sh        # own terminal, leave running
/Users/jordannelson/projects/release-guard/scripts/aws/bootstrap-platform.sh
```

`bootstrap-platform.sh` installs Flux from the mirrored images, creates the `orders`
database and its two IAM-authenticated roles, and applies `deploy/platform/aws`.
The verifier image is tagged with the HEAD commit, so rerun `mirror-images.sh` before
rerunning bootstrap on a new commit.

## 3. Turn on delivery

Set the GitHub variables the release workflows read. Role ARNs come from the contract;
the workflows check them against it again on every run.

```sh
gh variable set AWS_REGION --repo jordann6/release-guard --body us-east-1
gh variable set PUBLISH_ROLE_ARN --repo jordann6/release-guard --env publish \
  --body "$(aws ssm get-parameter --name /delivery/release-guard/prod/contract --query Parameter.Value --output text | jq -r .publish_role_arn)"
gh variable set PROMOTE_ROLE_ARN --repo jordann6/release-guard --env production \
  --body "$(aws ssm get-parameter --name /delivery/release-guard/prod/contract --query Parameter.Value --output text | jq -r .promote_role_arn)"
gh variable set PROMOTE_ROLE_ARN --repo jordann6/release-guard --env production-override \
  --body "$(aws ssm get-parameter --name /delivery/release-guard/prod/contract --query Parameter.Value --output text | jq -r .promote_role_arn)"
gh variable set DELIVERY_ENABLED --repo jordann6/release-guard --body true
```

The first release needs `--initialize` (ADR-0006). Run the release pipeline once by hand
for main's head; the evidence gate applies as for any merge:

```sh
gh workflow run release.yml --repo jordann6/release-guard --ref main -f initialize=true
```

## 4. Demo scenarios

Each is a PR or a manual workflow; record every run URL.

| Scenario | How |
|---|---|
| Successful release | Merge a clean PR |
| Vulnerable image rejected | PR pinning the 10.0.0 runtime image: blocked at `image-scan` |
| Failed rollout | Merge a release that needs a missing setting: verifier records Fail |
| Promotion blocked | Merge any next PR: `promote` exits blocked |
| Recovery | Run `rollback.yml`: last verified artifact republished as the next version |
| Audited override | Run `override.yml` with a digest, reason and expiry; approve the environment |

Check state at any point:

```sh
releasectl status --table release-guard-deployments --region us-east-1
kubectl --kubeconfig ~/.kube/release-guard-aws.yaml -n orders get kustomization,deploy
```

## When a rollout fails on AWS

Two failures the local scenarios cannot reproduce, both fixed in the manifests:

- **IRSA refuses the migrator.** The migrate Kustomization's `nameSuffix` renamed its
  ServiceAccount, and the IRSA trust only accepts `orders-migrator`. The ServiceAccount
  now lives in `deploy/platform/base/migrator.yaml`, outside the suffixed Kustomization.
- **Pods rejected by PodSecurity.** The CloudWatch observability add-on annotates pod
  templates and injects OpenTelemetry init containers that violate the `restricted`
  profile. The orders pod template sets the add-on's inject and auto-annotate
  annotations to `false`.

If a failed first release leaves no healthy release, both promote and initialize are
blocked. Recover with `override.yml` and an approval in `production-override`.

## 5. Teardown (operator destroys)

The workload backup vault must have no recovery points:

```sh
aws backup list-backup-vaults --profile release-guard-prod \
  --query 'BackupVaultList[].[BackupVaultName,NumberOfRecoveryPoints]' --output table
```

Keep the account's EBS encryption default by removing it from state before the workload
destroy.

```sh
gh variable set DELIVERY_ENABLED --repo jordann6/release-guard --body false
AWS_PROFILE=release-guard-prod /Users/jordannelson/platform-guardrails-v1.5.0/scripts/publish-contract.sh --delete /delivery/release-guard/prod/contract
terraform -chdir=/Users/jordannelson/aws-landing-zone/delivery plan -destroy -var-file=phase-c.tfvars.json -out destroy.tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/delivery apply destroy.tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/workload state rm aws_ebs_encryption_by_default.this
terraform -chdir=/Users/jordannelson/aws-landing-zone/workload plan -destroy -var-file=phase-c.tfvars.json -out destroy.tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/workload apply destroy.tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/network plan -destroy -var-file=phase-c.tfvars.json -out destroy.tfplan
terraform -chdir=/Users/jordannelson/aws-landing-zone/network apply destroy.tfplan
```

Review each destroy plan before applying it. Then confirm every root's state is empty
and the hourly guard passes:

```sh
for r in delivery workload network; do
  terraform -chdir=/Users/jordannelson/aws-landing-zone/$r state list \
    | /Users/jordannelson/platform-guardrails-v1.5.0/scripts/hourly-guard.sh && echo "$r clean"
done
```

Keep the member accounts and the retained foundation, and check the bill the next day.
