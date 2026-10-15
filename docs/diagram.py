#!/usr/bin/env python3
"""Render architecture.png with mingrammer diagrams and the official AWS icons.

Install diagrams and Graphviz, then run python3 docs/diagram.py.
The output path is relative to this source file, independent of the working directory.
"""

from pathlib import Path

from diagrams import Cluster, Diagram, Edge
from diagrams.aws.compute import EC2ContainerRegistry
from diagrams.aws.database import RDS, Dynamodb
from diagrams.aws.management import SystemsManagerParameterStore
from diagrams.aws.security import IAMRole
from diagrams.k8s.compute import Deploy, Job
from diagrams.onprem.ci import GithubActions
from diagrams.onprem.gitops import Flux
from diagrams.onprem.vcs import Github
from diagrams.programming.language import Csharp

GITHUB = {"bgcolor": "#f4f4f4", "color": "#888888", "fontsize": "17"}
AWS = {"bgcolor": "#fff4e7", "color": "#d49447", "fontsize": "17"}
CLUSTER = {"bgcolor": "#eef6ff", "color": "#7299bd", "fontsize": "17"}

with Diagram(
    "release-guard | verified, recoverable delivery to EKS\n"
    "Runners never reach the cluster; only the in-cluster verifier can declare a release healthy",
    filename=str(Path(__file__).resolve().with_name("architecture")),
    show=False,
    direction="LR",
    graph_attr={"fontsize": "23", "labelloc": "t", "bgcolor": "white", "pad": "0.5", "nodesep": "0.9", "ranksep": "1.3", "splines": "spline"},
    node_attr={"fontsize": "13"},
    edge_attr={"fontsize": "11"},
):
    with Cluster("GitHub | jordann6/release-guard", graph_attr=GITHUB):
        pr = Github("Pull request\n10 required checks\nno AWS credentials")
        app = Csharp("Orders API\n.NET 10 / EF Core")
        release = GithubActions("release.yml on main\nevidence gate, build,\nTrivy, SBOM, provenance")
        releasectl = Csharp("releasectl\npromote / rollback\noverride")
        app >> Edge(style="dotted") >> pr
        pr >> Edge(label="squash merge\nsame tree") >> release
        release >> Edge(style="dotted") >> releasectl

    with Cluster("AWS prod account | us-east-1", graph_attr=AWS):
        roles = IAMRole("publish / promote roles\nGitHub OIDC per environment")
        contract = SystemsManagerParameterStore("Deployment contract\nfrom aws-landing-zone")
        app_repo = EC2ContainerRegistry("release-guard/app\nimmutable, by digest")
        deploy_repo = EC2ContainerRegistry("release-guard/deploy\nrendered manifests (OCI)")
        table = Dynamodb("Deployment state\nLOCK / DEPLOY / VERIFY")
        rds = RDS("PostgreSQL\nIAM auth, no passwords")

        with Cluster("Amazon EKS | private API endpoint", graph_attr=CLUSTER):
            flux = Flux("Flux\nsource + kustomize\n(IRSA, restricted SA)")
            migrate = Job("orders-migrate\nexpand-only")
            api = Deploy("orders-api\nrestricted PSS")
            verifier = Deploy("Release verifier\nrollout + smoke")
            flux >> Edge(label="apply") >> migrate
            flux >> Edge(label="apply") >> api
            verifier >> Edge(style="dashed", label="watches") >> api

    release >> Edge(label="OIDC") >> roles
    roles >> Edge(style="dotted", label="reads") >> contract
    releasectl >> Edge(label="publish image") >> app_repo
    releasectl >> Edge(label="promote artifact") >> deploy_repo
    releasectl >> Edge(xlabel="DEPLOY# record,\nconditional write") >> table
    deploy_repo >> Edge(label="pull over\nVPC endpoints", color="#ac6413") >> flux
    app_repo >> Edge(style="dashed", label="image by digest") >> api
    migrate >> Edge(style="dotted", label="IAM token") >> rds
    api >> Edge(style="dotted", label="IAM token") >> rds
    verifier >> Edge(xlabel="VERIFY# verdict\n(only writer)", color="#2e7d32") >> table
